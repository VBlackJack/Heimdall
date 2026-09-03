/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Certificates;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="SplitService"/>. Covers the synchronous and
/// self-contained methods that can be exercised without a live WPF dispatcher
/// or a full integration harness: the per-session cancellation token
/// lifecycle, server-pane tunnel cleanup, <c>CloseAllPanes</c>'s close guards,
/// <c>ToggleSplitOrientation</c>, and <c>SplitSessionWithTool</c>'s short-circuit
/// guards (unknown tool, max panes). Async coverage includes dispatcher-free
/// split identity, orphan cleanup, and reconnect error handling; the WPF
/// dispatcher-dependent swap flow remains out of scope here.
/// </summary>
public sealed class SplitServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConfigManager _configManager;
    private readonly LocalizationManager _localizer;
    private readonly ToolRegistry _toolRegistry;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly TunnelManager _tunnelManager;
    private readonly SplitService _sut;

    public SplitServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"heimdall-split-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));

        _configManager = new ConfigManager(_tempDir);
        _localizer = new LocalizationManager();
        _toolRegistry = new ToolRegistry();
        _connectionSm = new ConnectionStateMachine();
        _tunnelManager = new TunnelManager();

        // ConnectionStateMachine and TunnelManager are sealed, so use real
        // lightweight instances. The fake session manager is enough for host
        // teardown, and async connection tests supply a tiny IConnectionService
        // double through CreateSplitService.
        _sut = new SplitService(
            _configManager,
            _localizer,
            _connectionSm,
            _tunnelManager,
            sessionManager: new FakeEmbeddedSessionManager(),
            connectionService: null!,
            _toolRegistry,
            dialogService: null!, new PaneCloseArbiter());
    }

    public void Dispose()
    {
        _tunnelManager.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* test cleanup */ }
        GC.SuppressFinalize(this);
    }

    // ── Category A: CancellationTokenSource lifecycle ───────────────────

    [Fact]
    public void RegisterSession_CreatesToken_ThatIsNotCancelled()
    {
        var session = new SessionTabViewModel();
        _sut.RegisterSession(session);

        var token = _sut.GetType()
            .GetMethod("GetSessionToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_sut, new object[] { session });

        Assert.IsType<CancellationToken>(token);
        Assert.False(((CancellationToken)token!).IsCancellationRequested);
    }

    [Fact]
    public void CancelSession_CancelsTheToken()
    {
        var session = new SessionTabViewModel();
        _sut.RegisterSession(session);
        var tokenBefore = InvokeGetSessionToken(session);
        Assert.False(tokenBefore.IsCancellationRequested);

        _sut.CancelSession(session);

        // After CancelSession, the session is unregistered; the previously
        // captured token should now observe the cancellation signal.
        Assert.True(tokenBefore.IsCancellationRequested);
    }

    [Fact]
    public void GetSessionToken_ReturnsNone_ForUnknownSession()
    {
        var session = new SessionTabViewModel();

        var token = InvokeGetSessionToken(session);

        Assert.Equal(CancellationToken.None, token);
    }

    [Fact]
    public void CancelSession_UnknownSession_DoesNotThrow()
    {
        var session = new SessionTabViewModel();

        var ex = Record.Exception(() => _sut.CancelSession(session));

        Assert.Null(ex);
    }

    [Fact]
    public void RegisterSession_TwiceForSameSession_IsIdempotent()
    {
        var session = new SessionTabViewModel();

        _sut.RegisterSession(session);
        var token1 = InvokeGetSessionToken(session);
        _sut.RegisterSession(session);
        var token2 = InvokeGetSessionToken(session);

        // ConcurrentDictionary.TryAdd keeps the first entry on collision,
        // so the second Register must not replace the original token.
        Assert.Equal(token1, token2);
        Assert.False(token1.IsCancellationRequested);
    }

    // ── Category B0: close guards, whatever the pane hosts ──────────────
    // The guard is consulted for every pane, tool or not, and nothing is torn down while a close
    // is withheld. These are behavioural rather than source-level on purpose: a bypass in one
    // primitive must not be masked by the other primitive still calling the arbiter.

    [Fact]
    public void CloseAllPanes_GuardDefers_WithholdsTheCloseAndTearsNothingDown()
    {
        var session = new SessionTabViewModel();
        var pane = MakePane(connectionType: "SSH");
        var guard = new StubCloseGuard { IsBusy = true, Verdict = CloseVerdict.Defer };
        pane.HostControl = guard;
        session.RootContent = pane;

        var result = _sut.CloseAllPanes(session, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.Equal(PaneCloseOutcome.Deferred, result.Outcome);
        Assert.Same(guard, pane.HostControl);
    }

    [Fact]
    public void CloseAllPanes_GuardDenies_WithholdsTheCloseAndTearsNothingDown()
    {
        var session = new SessionTabViewModel();
        var pane = MakePane(connectionType: "SSH");
        var guard = new StubCloseGuard { IsBusy = true, Verdict = CloseVerdict.Deny };
        pane.HostControl = guard;
        session.RootContent = pane;

        var result = _sut.CloseAllPanes(session, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.Equal(PaneCloseOutcome.Blocked, result.Outcome);
        Assert.Same(guard, pane.HostControl);
    }

    [Fact]
    public void CloseAllPanes_SilentRequest_ClosesEvenThroughABusyGuard()
    {
        var session = new SessionTabViewModel();
        var pane = MakePane(connectionType: "SSH");
        pane.HostControl = new StubCloseGuard { IsBusy = true, Verdict = CloseVerdict.Deny };
        session.RootContent = pane;

        var result = _sut.CloseAllPanes(session, CloseRequest.Silent(DisconnectReason.UserAction));

        // Application exit and the other programmatic teardowns must not be blockable, or a pane
        // could keep its host alive past shutdown.
        Assert.True(result.IsClosed);
        Assert.Null(pane.HostControl);
    }

    [Fact]
    public void ClosePane_GuardDefers_WithholdsTheCloseAndKeepsThePane()
    {
        var session = new SessionTabViewModel();
        var first = MakePane(connectionType: "SSH", serverId: "a");
        var second = MakePane(connectionType: "SSH", serverId: "b");
        var guard = new StubCloseGuard { IsBusy = true, Verdict = CloseVerdict.Defer };
        second.HostControl = guard;
        session.RootContent = new SplitContainerModel
        {
            First = first,
            Second = second,
            Orientation = SplitOrientation.Vertical
        };

        var result = _sut.ClosePane(session, second.PaneId, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.Equal(PaneCloseOutcome.Deferred, result.Outcome);
        Assert.Same(guard, second.HostControl);
        Assert.NotNull(Core.Models.SplitTreeHelper.FindPane(session.RootContent, second.PaneId));
    }

    [Fact]
    public void ClosePane_GuardAllows_ClosesNormally()
    {
        var session = new SessionTabViewModel();
        var first = MakePane(connectionType: "SSH", serverId: "a");
        var second = MakePane(connectionType: "SSH", serverId: "b");
        second.HostControl = new StubCloseGuard { IsBusy = false };
        session.RootContent = new SplitContainerModel
        {
            First = first,
            Second = second,
            Orientation = SplitOrientation.Vertical
        };

        var result = _sut.ClosePane(session, second.PaneId, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.True(result.IsClosed);
        Assert.Null(Core.Models.SplitTreeHelper.FindPane(session.RootContent, second.PaneId));
    }

    /// <summary>
    /// A pane content that guards its close. Not a tool, not a WPF control - which is the point:
    /// the contract is neutral, and a pane's content is typed <c>object?</c>.
    /// </summary>
    private sealed class StubCloseGuard : ICloseGuard
    {
        public bool IsBusy { get; init; }

        public CloseVerdict Verdict { get; init; } = CloseVerdict.Allow;

        public CloseGuardState SampleCloseGuardState() => new(IsBusy, 1);

        public CloseDecision PollClose(CloseRequest request) => Verdict switch
        {
            CloseVerdict.Defer => CloseDecision.Defer("StubGuardBusy", 1),
            CloseVerdict.Deny => CloseDecision.Deny("StubGuardBusy", 1),
            _ => CloseDecision.Allow(1)
        };

        public Task<bool> ResolveCloseAsync(CloseRequest request, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    // ── Category B: CloseAllPanes tool-pane blocking ────────────────────

    [Fact]
    public void CloseAllPanes_EmptyTree_ReturnsTrue()
    {
        var session = new SessionTabViewModel();
        // Default RootContent is a single empty SessionPaneModel with
        // ServerId="" and ConnectionType="" — no server cleanup path hit.

        var result = _sut.CloseAllPanes(session, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.True(result.IsClosed);
    }

    [Fact]
    public void CloseAllPanes_ToolPaneCanClose_ReturnsTrue_AndClearsHostControl()
    {
        var session = new SessionTabViewModel();
        var toolPane = MakePane(connectionType: "TOOL:PING");
        var closableView = new StubToolView(canClose: true);
        toolPane.HostControl = closableView;
        session.RootContent = toolPane;

        var result = _sut.CloseAllPanes(session, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.True(result.IsClosed);
        Assert.Null(toolPane.HostControl);
        Assert.True(closableView.Disposed);
    }

    [Fact]
    public void CloseAllPanes_ToolPaneBlocking_ReturnsFalse_AndPreservesHostControl()
    {
        var session = new SessionTabViewModel();
        var toolPane = MakePane(connectionType: "TOOL:PING");
        var blockingView = new StubToolView(canClose: false);
        toolPane.HostControl = blockingView;
        session.RootContent = toolPane;

        var result = _sut.CloseAllPanes(session, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.False(result.IsClosed);
        Assert.Same(blockingView, toolPane.HostControl);
        Assert.False(blockingView.Disposed);
    }

    [Fact]
    public void CloseAllPanes_MixedTree_OneBlockingTool_ReturnsFalse_NothingDisposed()
    {
        var session = new SessionTabViewModel();
        var freePane = MakePane(connectionType: "TOOL:HASH");
        var freeView = new StubToolView(canClose: true);
        freePane.HostControl = freeView;

        var blockedPane = MakePane(connectionType: "TOOL:PING");
        var blockedView = new StubToolView(canClose: false);
        blockedPane.HostControl = blockedView;

        session.RootContent = new SplitContainerModel
        {
            First = freePane,
            Second = blockedPane,
            Orientation = SplitOrientation.Vertical
        };

        var result = _sut.CloseAllPanes(session, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.False(result.IsClosed);
        // The blocking check runs before the disposal loop, so neither
        // host control is torn down when any tool pane is busy.
        Assert.Same(freeView, freePane.HostControl);
        Assert.False(freeView.Disposed);
        Assert.Same(blockedView, blockedPane.HostControl);
        Assert.False(blockedView.Disposed);
    }

    [Fact]
    public void CloseAllPanes_MixedServerAndTool_ReleasesServerTunnelAndDisposesHosts()
    {
        const string serverId = "session-server";
        const int localPort = 45124;
        RegisterTrackedTunnel(serverId, localPort);

        var serverHost = new DisposableHost();
        var serverPane = MakePane(paneId: "server-pane", serverId: serverId, connectionType: "RDP");
        serverPane.OriginalServerId = "profile-server";
        serverPane.Title = "Server";
        serverPane.HostControl = serverHost;

        var toolHost = new StubToolView(canClose: true);
        var toolPane = MakePane(paneId: "tool-pane", serverId: "tool-ping", connectionType: "TOOL:PING");
        toolPane.HostControl = toolHost;

        var session = new SessionTabViewModel
        {
            RootContent = new SplitContainerModel
            {
                First = serverPane,
                Second = toolPane,
                Orientation = SplitOrientation.Vertical
            }
        };

        var result = _sut.CloseAllPanes(session, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.True(result.IsClosed);
        Assert.Null(serverPane.HostControl);
        Assert.Null(toolPane.HostControl);
        Assert.True(serverHost.Disposed);
        Assert.True(toolHost.Disposed);
        AssertServerStateReset(serverId);
        AssertSingleTunnelReferenceReleased(localPort);
    }

    // ── Category C: ClosePane / server-pane cleanup ──────────────────────

    [Fact]
    public void ClosePane_ServerPaneWithTunnel_ReleasesTunnelResetsStateAndPromotesSibling()
    {
        const string serverId = "split-server";
        const int localPort = 45125;
        RegisterTrackedTunnel(serverId, localPort);

        var host = new DisposableHost();
        var serverPane = MakePane(paneId: "server-pane", serverId: serverId, connectionType: "SSH");
        serverPane.OriginalServerId = "profile-server";
        serverPane.Title = "Server";
        serverPane.HostControl = host;

        var sibling = MakePane(paneId: "sibling", connectionType: "TOOL:NOTES");
        var session = new SessionTabViewModel
        {
            RootContent = new SplitContainerModel
            {
                First = serverPane,
                Second = sibling,
                Orientation = SplitOrientation.Horizontal
            }
        };

        _sut.ClosePane(session, serverPane.PaneId, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.Same(sibling, session.RootContent);
        Assert.Null(serverPane.HostControl);
        Assert.True(host.Disposed);
        AssertServerStateReset(serverId);
        AssertSingleTunnelReferenceReleased(localPort);
    }

    [Fact]
    public void ClosePane_SftpCompanionWithDistinctSessionKey_LeavesSshStateAndReleasesOneTunnelReference()
    {
        const string sshSessionId = "ssh-session";
        const string sftpSessionId = "sftp-companion-session";
        const int localPort = 45127;

        var info = new TunnelInfo("gateway", localPort, "target.internal", 22, DateTime.UtcNow, true);
        Assert.True(_tunnelManager.TryRegisterExternalTunnel(info, new DisposableHost(), () => true));
        _tunnelManager.AddReference(localPort);
        RegisterConnectedState(sshSessionId, ConnectionState.LaunchingSsh, localPort);
        RegisterConnectedState(sftpSessionId, ConnectionState.LaunchingSftp, localPort);

        var sshPane = MakePane(paneId: "ssh-pane", serverId: sshSessionId, connectionType: "SSH");
        sshPane.OriginalServerId = "profile-server";
        sshPane.Title = "SSH";
        sshPane.HostControl = new DisposableHost();

        var sftpHost = new DisposableHost();
        var sftpPane = MakePane(paneId: "sftp-pane", serverId: sftpSessionId, connectionType: "SFTP");
        sftpPane.OriginalServerId = "profile-server";
        sftpPane.Title = "SFTP";
        sftpPane.HostControl = sftpHost;

        var session = new SessionTabViewModel
        {
            RootContent = new SplitContainerModel
            {
                First = sshPane,
                Second = sftpPane,
                Orientation = SplitOrientation.Vertical
            }
        };

        _sut.ClosePane(session, sftpPane.PaneId, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.Same(sshPane, session.RootContent);
        Assert.True(sftpHost.Disposed);
        Assert.Equal(ConnectionState.Connected, _connectionSm.GetState(sshSessionId));
        Assert.Equal(localPort, _connectionSm.GetStateData(sshSessionId)?.TunnelLocalPort);
        Assert.Equal(ConnectionState.Disconnected, _connectionSm.GetState(sftpSessionId));
        Assert.True(_tunnelManager.HasTunnel(localPort));
        Assert.True(_tunnelManager.ReleaseReference(localPort));
        Assert.False(_tunnelManager.HasTunnel(localPort));
    }

    [Fact]
    public void ClosePane_ToolPaneBlocking_PreservesTreeAndHost()
    {
        var blockingView = new StubToolView(canClose: false);
        var toolPane = MakePane(paneId: "tool-pane", serverId: "tool-ping", connectionType: "TOOL:PING");
        toolPane.Title = "Ping";
        toolPane.HostControl = blockingView;

        var sibling = MakePane(paneId: "sibling");
        var root = new SplitContainerModel
        {
            First = sibling,
            Second = toolPane,
            Orientation = SplitOrientation.Vertical
        };
        var session = new SessionTabViewModel { RootContent = root };

        _sut.ClosePane(session, toolPane.PaneId, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.Same(root, session.RootContent);
        Assert.Same(blockingView, toolPane.HostControl);
        Assert.False(blockingView.Disposed);
    }

    [Fact]
    public void CleanupOrphanedPane_ServerWithTunnel_ReleasesTunnelAndResetsState()
    {
        const string serverId = "orphan-server";
        const int localPort = 45126;
        RegisterTrackedTunnel(serverId, localPort);

        _sut.CleanupOrphanedPane(serverId);

        AssertServerStateReset(serverId);
        AssertSingleTunnelReferenceReleased(localPort);
    }

    [Fact]
    public async Task SplitSessionWithServerAsync_WinRm_InitializesRuntimeStateBeforeDispatch()
    {
        const string inventoryServerId = "winrm-inventory-1";
        DisposableSessionResult connectionSession = new DisposableSessionResult();
        RecordingConnectionService connectionService = new RecordingConnectionService(
            stateMachine: _connectionSm,
            successfulWinRmSession: connectionSession);
        RecordingPaneOwnerHost ownerHost = new RecordingPaneOwnerHost();
        FakeEmbeddedSessionManager hostManager = new FakeEmbeddedSessionManager
        {
            CreateHostControlCallback = (_, _, connectionType, sessionResult, _, _) =>
            {
                Assert.Equal("WINRM", connectionType);
                Assert.Same(connectionSession, sessionResult);
                return ownerHost;
            }
        };
        SplitService sut = CreateSplitService(connectionService, hostManager);
        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new ServerProfileDto
            {
                Id = inventoryServerId,
                DisplayName = "WinRM server",
                ConnectionType = "WINRM"
            }
        });

        SessionPaneModel primaryPane = MakePane(
            paneId: "primary-pane",
            serverId: "primary-runtime",
            connectionType: "SSH");
        primaryPane.OriginalServerId = "primary-inventory";
        SessionTabViewModel session = new SessionTabViewModel { RootContent = primaryPane };
        ObservableCollection<SessionTabViewModel> activeSessions = new ObservableCollection<SessionTabViewModel>
        {
            session
        };
        sut.ActiveSessionsProvider = () => activeSessions;

        await sut.SplitSessionWithServerAsync(
            session,
            inventoryServerId,
            SplitOrientation.Vertical,
            primaryPane.PaneId);

        SessionPaneModel winRmPane = Assert.Single(
            SplitTreeHelper.EnumerateLeaves(session.RootContent),
            pane => string.Equals(
                pane.OriginalServerId,
                inventoryServerId,
                StringComparison.Ordinal));
        Assert.Equal(ConnectionState.Initializing, connectionService.WinRmStateAtDispatch);
        Assert.Equal(winRmPane.ServerId, connectionService.WinRmServerIdAtDispatch);
        Assert.NotEqual(inventoryServerId, winRmPane.ServerId);
        Assert.Equal(ConnectionState.RemoteSessionHandedOff, _connectionSm.GetState(winRmPane.ServerId));
        Assert.Equal("Connected", winRmPane.Status);
        Assert.Same(winRmPane, ownerHost.OwningPane);
        Assert.Null(_connectionSm.GetStateData(inventoryServerId));
    }

    [Fact]
    public async Task SplitSessionWithServerAsync_SameWinRmProfile_UsesIndependentRuntimeStateKeys()
    {
        const string inventoryServerId = "winrm-server-1";
        const int localPort = 45127;
        TunnelInfo tunnelInfo = new(
            "gateway-1",
            localPort,
            "winrm.example.test",
            5986,
            DateTime.UtcNow,
            true);
        SharedTunnelWinRmConnectionService connectionService = new(
            _connectionSm,
            _tunnelManager,
            tunnelInfo);
        FakeEmbeddedSessionManager hostManager = new FakeEmbeddedSessionManager
        {
            CreateHostControlCallback = (sessionTab, _, _, _, _, _) =>
            {
                SessionPaneModel connectingPane = Assert.Single(
                    SplitTreeHelper.EnumerateLeaves(sessionTab.RootContent),
                    pane => string.Equals(
                        pane.OriginalServerId,
                        inventoryServerId,
                        StringComparison.Ordinal)
                        && pane.HostControl is null);
                Assert.Equal(connectionService.ServerIds[^1], connectingPane.ServerId);
                return new DisposableHost();
            }
        };
        SplitService sut = CreateSplitService(connectionService, hostManager);
        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new()
            {
                Id = inventoryServerId,
                DisplayName = "WinRM server",
                ConnectionType = "WINRM",
                RemoteServer = tunnelInfo.RemoteHost,
                RemotePort = tunnelInfo.RemotePort
            }
        });

        SessionPaneModel primaryPane = MakePane(
            paneId: "primary-pane",
            serverId: "primary-session",
            connectionType: "SSH");
        primaryPane.OriginalServerId = "primary-server";
        SessionTabViewModel session = new SessionTabViewModel { RootContent = primaryPane };
        ObservableCollection<SessionTabViewModel> activeSessions = new() { session };
        sut.ActiveSessionsProvider = () => activeSessions;

        await sut.SplitSessionWithServerAsync(
            session,
            inventoryServerId,
            SplitOrientation.Vertical,
            primaryPane.PaneId);
        await sut.SplitSessionWithServerAsync(
            session,
            inventoryServerId,
            SplitOrientation.Horizontal,
            primaryPane.PaneId);

        List<SessionPaneModel> splitPanes = SplitTreeHelper
            .EnumerateLeaves(session.RootContent)
            .Where(pane => string.Equals(
                pane.OriginalServerId,
                inventoryServerId,
                StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, splitPanes.Count);
        Assert.Equal(2, connectionService.ServerIds.Count);
        Assert.Equal(2, connectionService.ServerIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(connectionService.ServerIds, serverId =>
            Assert.NotEqual(inventoryServerId, serverId));
        Assert.All(splitPanes, pane =>
        {
            Assert.Equal(inventoryServerId, pane.OriginalServerId);
            Assert.NotEqual(inventoryServerId, pane.ServerId);
            Assert.Contains(pane.ServerId, connectionService.ServerIds);
            Assert.Equal(localPort, _connectionSm.GetStateData(pane.ServerId)?.TunnelLocalPort);
        });
        Assert.True(_tunnelManager.HasTunnel(localPort));

        PaneCloseResult closed = sut.CloseAllPanes(session, CloseRequest.Interactive(DisconnectReason.UserAction));

        Assert.True(closed.IsClosed);
        Assert.All(connectionService.ServerIds, serverId =>
            Assert.Null(_connectionSm.GetStateData(serverId)));
        Assert.False(_tunnelManager.HasTunnel(localPort));
    }

    [Fact]
    public async Task SplitSessionWithServerAsync_Rdp_PaneProfilesStillResolveToTheInventoryProfileForTrust()
    {
        // The seam between the two halves of one rule. SplitService gives each pane its own state
        // key by writing it over the profile copy's Id, and the certificate question is filed
        // against whatever Id the pane runs on. Those two decisions only agree because the key is
        // minted by SessionIdCodec, which is the only thing that inverts it: mint it any other
        // way and every split-pane approval is stored under an identifier that dies with the
        // pane, and the two panes stop sharing a coalescing scope.
        const string inventoryServerId = "rdp-inventory-1";
        AllProfilesRecordingConnectionService connectionService = new AllProfilesRecordingConnectionService();
        SplitService sut = CreateSplitService(connectionService);
        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new ServerProfileDto
            {
                Id = inventoryServerId,
                DisplayName = "Production",
                ConnectionType = "RDP",
                RemoteServer = "srv01",
                RemotePort = 3389
            }
        });

        SessionPaneModel primaryPane = MakePane(
            paneId: "primary-pane",
            serverId: "primary-runtime",
            connectionType: "RDP");
        primaryPane.OriginalServerId = "primary-inventory";
        SessionTabViewModel session = new SessionTabViewModel { RootContent = primaryPane };
        ObservableCollection<SessionTabViewModel> activeSessions = new ObservableCollection<SessionTabViewModel>
        {
            session
        };
        sut.ActiveSessionsProvider = () => activeSessions;

        await sut.SplitSessionWithServerAsync(
            session,
            inventoryServerId,
            SplitOrientation.Vertical,
            primaryPane.PaneId);
        await sut.SplitSessionWithServerAsync(
            session,
            inventoryServerId,
            SplitOrientation.Horizontal,
            primaryPane.PaneId);

        Assert.Equal(2, connectionService.Profiles.Count);
        Assert.Equal(
            2,
            connectionService.Profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            connectionService.Profiles,
            profile => Assert.NotEqual(inventoryServerId, profile.Id));

        List<RdpCertificateVerificationRequest> requests = connectionService.Profiles
            .Select(profile => RdpCertificateVerificationRequestBuilder.Build(
                profile,
                new RdpCertificateProbeTarget("127.0.0.1", 53211),
                "pane-" + profile.Id))
            .ToList();

        Assert.All(requests, request => Assert.Equal(inventoryServerId, request.ProfileId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitSessionWithServerAsync_AbortedAfterConnect_CleansRuntimeState(
        bool removeSessionBeforeMaterialization)
    {
        const string inventoryServerId = "winrm-orphan-server";
        const int localPort = 45128;
        TunnelInfo tunnelInfo = new(
            "gateway-1",
            localPort,
            "winrm.example.test",
            5986,
            DateTime.UtcNow,
            true);
        SharedTunnelWinRmConnectionService connectionService = new(
            _connectionSm,
            _tunnelManager,
            tunnelInfo);
        FakeEmbeddedSessionManager hostManager = new FakeEmbeddedSessionManager
        {
            CreateHostControlCallback = (_, _, _, _, _, _) =>
                throw new InvalidOperationException("Host factory failed.")
        };
        SplitService sut = CreateSplitService(connectionService, hostManager);
        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new()
            {
                Id = inventoryServerId,
                DisplayName = "WinRM orphan server",
                ConnectionType = "WINRM",
                RemoteServer = tunnelInfo.RemoteHost,
                RemotePort = tunnelInfo.RemotePort
            }
        });

        SessionPaneModel primaryPane = MakePane(
            paneId: "primary-pane",
            serverId: "primary-session",
            connectionType: "SSH");
        SessionTabViewModel session = new SessionTabViewModel { RootContent = primaryPane };
        ObservableCollection<SessionTabViewModel> activeSessions = new() { session };
        sut.ActiveSessionsProvider = () => removeSessionBeforeMaterialization
            ? new ObservableCollection<SessionTabViewModel>()
            : activeSessions;

        await sut.SplitSessionWithServerAsync(
            session,
            inventoryServerId,
            SplitOrientation.Vertical,
            primaryPane.PaneId);

        string runtimeServerId = Assert.Single(connectionService.ServerIds);
        Assert.NotEqual(inventoryServerId, runtimeServerId);
        Assert.Null(_connectionSm.GetStateData(runtimeServerId));
        Assert.False(_tunnelManager.HasTunnel(localPort));
        if (!removeSessionBeforeMaterialization)
        {
            Assert.Same(primaryPane, session.RootContent);
        }
    }

    [Theory]
    [InlineData(WinRmConnectionOutcome.FailureAfterTransportCleanup)]
    [InlineData(WinRmConnectionOutcome.CancellationAfterTransportCleanup)]
    [InlineData(WinRmConnectionOutcome.ExceptionAfterTransportCleanup)]
    public async Task SplitSessionWithServerAsync_FailedDispatch_TearsDownStateWithoutDoubleRelease(
        WinRmConnectionOutcome outcome)
    {
        const string inventoryServerId = "winrm-failed-server";
        const int localPort = 45129;
        TunnelInfo tunnelInfo = new(
            "gateway-1",
            localPort,
            "winrm.example.test",
            5986,
            DateTime.UtcNow,
            true);
        SharedTunnelWinRmConnectionService connectionService = new(
            _connectionSm,
            _tunnelManager,
            tunnelInfo,
            outcome);
        SplitService sut = CreateSplitService(connectionService);
        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new()
            {
                Id = inventoryServerId,
                DisplayName = "Failed WinRM server",
                ConnectionType = "WINRM",
                RemoteServer = tunnelInfo.RemoteHost,
                RemotePort = tunnelInfo.RemotePort
            }
        });

        SessionPaneModel primaryPane = MakePane(
            paneId: "primary-pane",
            serverId: "primary-session",
            connectionType: "SSH");
        SessionTabViewModel session = new SessionTabViewModel { RootContent = primaryPane };
        ObservableCollection<SessionTabViewModel> activeSessions = new() { session };
        sut.ActiveSessionsProvider = () => activeSessions;

        Exception? exception = await Record.ExceptionAsync(() =>
            sut.SplitSessionWithServerAsync(
                session,
                inventoryServerId,
                SplitOrientation.Vertical,
                primaryPane.PaneId));

        Assert.Null(exception);
        string runtimeServerId = Assert.Single(connectionService.ServerIds);
        Assert.NotEqual(inventoryServerId, runtimeServerId);
        Assert.Null(_connectionSm.GetStateData(runtimeServerId));
        Assert.True(_tunnelManager.HasTunnel(localPort));
        Assert.True(_tunnelManager.ReleaseReference(localPort));
        Assert.False(_tunnelManager.HasTunnel(localPort));
    }

    // ── Category D: Reconnect exception handling ─────────────────────────

    [Fact]
    public async Task ReconnectPaneAsync_AlreadyReconnectingPane_SkipsConnect()
    {
        RecordingConnectionService connectionService = new RecordingConnectionService();
        SplitService sut = CreateSplitService(connectionService);
        SessionPaneModel pane = MakePane(paneId: "pane-1", serverId: "old-session", connectionType: "RDP");
        pane.OriginalServerId = "server-1";
        pane.HostControl = null;

        SessionTabViewModel session = new SessionTabViewModel { RootContent = pane };
        ObservableCollection<SessionTabViewModel> activeSessions = new ObservableCollection<SessionTabViewModel> { session };
        sut.ActiveSessionsProvider = () => activeSessions;

        string? capturedStatus = null;
        sut.SetStatusText = (string s) => capturedStatus = s;

        Exception? ex = await Record.ExceptionAsync(() => sut.ReconnectPaneAsync(session, pane.PaneId));

        Assert.Null(ex);
        Assert.False(connectionService.ConnectInvoked);
        Assert.Null(capturedStatus);
        Assert.Null(pane.HostControl);
    }

    [Fact]
    public async Task ReconnectPaneAsync_EmptyOriginalServerId_SetsErrorAndSkipsConnectAndTeardown()
    {
        RecordingConnectionService connectionService = new RecordingConnectionService();
        SplitService sut = CreateSplitService(connectionService);
        DisposableHost host = new DisposableHost();
        SessionPaneModel pane = MakePane(paneId: "pane-1", serverId: "old-session", connectionType: "RDP");
        pane.OriginalServerId = string.Empty;
        pane.HostControl = host;

        SessionTabViewModel session = new SessionTabViewModel { RootContent = pane };
        ObservableCollection<SessionTabViewModel> activeSessions = new ObservableCollection<SessionTabViewModel> { session };
        sut.ActiveSessionsProvider = () => activeSessions;

        string? capturedStatus = null;
        sut.SetStatusText = (string s) => capturedStatus = s;

        Exception? ex = await Record.ExceptionAsync(() => sut.ReconnectPaneAsync(session, pane.PaneId));

        Assert.Null(ex);
        Assert.Equal(_localizer["ErrorSplitSessionFailed"], capturedStatus);
        Assert.False(connectionService.ConnectInvoked);
        Assert.False(host.Disposed);
        Assert.Same(host, pane.HostControl);
    }

    [Fact]
    public async Task ReconnectPaneAsync_UnexpectedConnectException_SetsErrorAndDoesNotThrow()
    {
        var sut = CreateSplitService(new ThrowingConnectionService(new InvalidOperationException("boom")));
        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new()
            {
                Id = "server-1",
                DisplayName = "Server 1",
                ConnectionType = "RDP"
            }
        });

        var host = new DisposableHost();
        var pane = MakePane(paneId: "pane-1", serverId: "old-session", connectionType: "RDP");
        pane.OriginalServerId = "server-1";
        pane.Title = "Server 1";
        pane.HostControl = host;

        var session = new SessionTabViewModel { RootContent = pane };
        session.Title = "Split tab";
        var activeSessions = new ObservableCollection<SessionTabViewModel> { session };
        sut.ActiveSessionsProvider = () => activeSessions;

        string? capturedStatus = null;
        sut.SetStatusText = s => capturedStatus = s;

        _connectionSm.TryTransition("old-session", ConnectionState.Initializing);
        _connectionSm.SetTunnelInfo("old-session", localPort: 45123, processId: 123);

        var ex = await Record.ExceptionAsync(() => sut.ReconnectPaneAsync(session, pane.PaneId));

        Assert.Null(ex);
        Assert.Equal("Error", pane.Status);
        Assert.Null(pane.HostControl);
        Assert.True(host.Disposed);
        Assert.NotNull(capturedStatus);
        Assert.Contains("boom", capturedStatus);
        Assert.Equal(ConnectionState.Disconnected, _connectionSm.GetState("old-session"));
        Assert.Null(_connectionSm.GetStateData("old-session")?.TunnelLocalPort);
    }

    [Fact]
    public async Task ReconnectPaneAsync_FailedConnectionWithDiagnostic_CopiesFailureDetails()
    {
        var diagnostic = new SessionDiagnostic(
            SessionFailureStage.SshGateway,
            "ErrorConnectionFailed",
            null,
            "gateway refused");
        var sut = CreateSplitService(
            new RecordingConnectionService(
                failureResult: new ConnectionResult(false, "gateway refused", null, diagnostic)));
        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new()
            {
                Id = "server-1",
                DisplayName = "Server 1",
                ConnectionType = "RDP"
            }
        });

        var pane = MakePane(paneId: "pane-1", serverId: "old-session", connectionType: "RDP");
        pane.OriginalServerId = "server-1";
        pane.HostControl = new DisposableHost();
        var session = new SessionTabViewModel { RootContent = pane };
        sut.ActiveSessionsProvider = () => new ObservableCollection<SessionTabViewModel> { session };

        // Producer covered: SplitService.ReconnectPaneAsync post-connect failure branch
        // copies ConnectionResult.Failure into the pane around src/Heimdall.App/Services/SplitService.cs:602.
        var ex = await Record.ExceptionAsync(() => sut.ReconnectPaneAsync(session, pane.PaneId));

        Assert.Null(ex);
        Assert.Equal("Error", pane.Status);
        Assert.Same(diagnostic, pane.FailureDetails);
    }

    [Fact]
    public async Task ReconnectPaneAsync_FailedConnectionWithoutDiagnostic_LeavesFailureDetailsNull()
    {
        var staleDiagnostic = new SessionDiagnostic(
            SessionFailureStage.SshGateway,
            "StaleFailure",
            null,
            "stale");
        var sut = CreateSplitService(new RecordingConnectionService());
        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new()
            {
                Id = "server-1",
                DisplayName = "Server 1",
                ConnectionType = "RDP"
            }
        });

        var pane = MakePane(paneId: "pane-1", serverId: "old-session", connectionType: "RDP");
        pane.OriginalServerId = "server-1";
        pane.HostControl = new DisposableHost();
        pane.FailureDetails = staleDiagnostic;
        var session = new SessionTabViewModel { RootContent = pane };
        sut.ActiveSessionsProvider = () => new ObservableCollection<SessionTabViewModel> { session };

        var ex = await Record.ExceptionAsync(() => sut.ReconnectPaneAsync(session, pane.PaneId));

        Assert.Null(ex);
        Assert.Equal("Error", pane.Status);
        Assert.Null(pane.FailureDetails);
    }

    [Fact]
    public async Task ReconnectPaneAsync_Success_PreservesFreshConnectionState_WhenServerIdIsReused()
    {
        var newSession = new DisposableSessionResult();
        var hostManager = new FakeEmbeddedSessionManager();
        var newHost = new DisposableHost();
        hostManager.CreateHostControlCallback = (_, _, _, sessionResult, _, _) =>
        {
            Assert.Same(newSession, sessionResult);
            return newHost;
        };

        var sut = CreateSplitService(
            new SuccessfulRdpConnectionService(_connectionSm, newSession),
            hostManager);

        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new()
            {
                Id = "server-1",
                DisplayName = "Server 1",
                ConnectionType = "RDP"
            }
        });

        var oldHost = new DisposableHost();
        var pane = MakePane(paneId: "pane-1", serverId: "server-1", connectionType: "RDP");
        pane.OriginalServerId = "server-1";
        pane.Title = "Server 1";
        pane.HostControl = oldHost;

        var session = new SessionTabViewModel { RootContent = pane };
        session.Title = "Split tab";
        var activeSessions = new ObservableCollection<SessionTabViewModel> { session };
        sut.ActiveSessionsProvider = () => activeSessions;

        Assert.True(_connectionSm.TryTransition("server-1", ConnectionState.Initializing));
        Assert.True(_connectionSm.TryTransition("server-1", ConnectionState.ValidatingConfig));
        Assert.True(_connectionSm.TryTransition("server-1", ConnectionState.LaunchingRdp));
        Assert.True(_connectionSm.TryTransition("server-1", ConnectionState.Connected));

        var ex = await Record.ExceptionAsync(() => sut.ReconnectPaneAsync(session, pane.PaneId));

        Assert.Null(ex);
        Assert.True(oldHost.Disposed);
        Assert.False(newSession.Disposed);
        Assert.Same(newHost, pane.HostControl);
        Assert.Equal("server-1", pane.ServerId);
        Assert.Equal("Connected", pane.Status);
        Assert.Equal(ConnectionState.Connected, _connectionSm.GetState("server-1"));
    }

    [Fact]
    public async Task ReconnectPaneAsync_WinRm_InitializesRuntimeStateBeforeDispatch()
    {
        const string inventoryServerId = "winrm-inventory-1";
        const string runtimeServerId = "winrm-runtime-1";
        DisposableSessionResult connectionSession = new DisposableSessionResult();
        RecordingConnectionService connectionService = new RecordingConnectionService(
            stateMachine: _connectionSm,
            successfulWinRmSession: connectionSession);
        RecordingPaneOwnerHost ownerHost = new RecordingPaneOwnerHost();
        FakeEmbeddedSessionManager hostManager = new FakeEmbeddedSessionManager
        {
            CreateHostControlCallback = (_, _, connectionType, sessionResult, _, _) =>
            {
                Assert.Equal("WINRM", connectionType);
                Assert.Same(connectionSession, sessionResult);
                return ownerHost;
            }
        };
        SplitService sut = CreateSplitService(connectionService, hostManager);
        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new ServerProfileDto
            {
                Id = inventoryServerId,
                DisplayName = "WinRM server",
                ConnectionType = "WINRM"
            }
        });

        DisposableHost oldHost = new DisposableHost();
        SessionPaneModel pane = MakePane(
            paneId: "winrm-pane",
            serverId: runtimeServerId,
            connectionType: "WINRM");
        pane.OriginalServerId = inventoryServerId;
        pane.Title = "WinRM server";
        pane.HostControl = oldHost;
        SessionPaneModel primaryPane = MakePane(
            paneId: "primary-pane",
            serverId: "primary-runtime",
            connectionType: "SSH");
        SessionTabViewModel session = new SessionTabViewModel
        {
            RootContent = new SplitContainerModel
            {
                First = primaryPane,
                Second = pane,
                Orientation = SplitOrientation.Vertical
            }
        };
        ObservableCollection<SessionTabViewModel> activeSessions = new ObservableCollection<SessionTabViewModel>
        {
            session
        };
        sut.ActiveSessionsProvider = () => activeSessions;

        Assert.True(_connectionSm.TryTransition(runtimeServerId, ConnectionState.Initializing));
        Assert.True(_connectionSm.TryTransition(runtimeServerId, ConnectionState.ValidatingConfig));
        Assert.True(_connectionSm.TryTransition(runtimeServerId, ConnectionState.LaunchingWinRm));
        Assert.True(_connectionSm.TryTransition(runtimeServerId, ConnectionState.RemoteSessionHandedOff));

        await sut.ReconnectPaneAsync(session, pane.PaneId);

        Assert.True(oldHost.Disposed);
        Assert.Equal(ConnectionState.Initializing, connectionService.WinRmStateAtDispatch);
        Assert.Equal(runtimeServerId, connectionService.WinRmServerIdAtDispatch);
        Assert.Equal(ConnectionState.RemoteSessionHandedOff, _connectionSm.GetState(runtimeServerId));
        Assert.Equal("Connected", pane.Status);
        Assert.Same(pane, ownerHost.OwningPane);
        Assert.Null(_connectionSm.GetStateData(inventoryServerId));
    }

    [Fact]
    public async Task ReconnectPaneAsync_PassesSftpReconnectPathHintToHostFactoryAndClearsIt()
    {
        var newSession = new DisposableSessionResult();
        var hostManager = new FakeEmbeddedSessionManager();
        var newHost = new DisposableHost();
        string? capturedInitialRemotePath = null;
        hostManager.CreateHostControlCallback = (_, _, connectionType, sessionResult, _, initialRemotePath) =>
        {
            Assert.Equal("SFTP", connectionType);
            Assert.Same(newSession, sessionResult);
            capturedInitialRemotePath = initialRemotePath;
            return newHost;
        };

        var sut = CreateSplitService(
            new RecordingConnectionService(successfulSftpSession: newSession),
            hostManager);

        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new()
            {
                Id = "server-1",
                DisplayName = "Server 1",
                ConnectionType = "SFTP"
            }
        });

        var oldHost = new DisposableHost();
        var pane = MakePane(paneId: "pane-1", serverId: "server-1", connectionType: "SFTP");
        pane.OriginalServerId = "server-1";
        pane.Title = "Server 1";
        pane.HostControl = oldHost;
        pane.SftpReconnectPathHint = "/var/log";

        var session = new SessionTabViewModel { RootContent = pane };
        session.Title = "SFTP tab";
        var activeSessions = new ObservableCollection<SessionTabViewModel> { session };
        sut.ActiveSessionsProvider = () => activeSessions;

        var ex = await Record.ExceptionAsync(() => sut.ReconnectPaneAsync(session, pane.PaneId));

        Assert.Null(ex);
        Assert.True(oldHost.Disposed);
        Assert.False(newSession.Disposed);
        Assert.Same(newHost, pane.HostControl);
        Assert.Equal("/var/log", capturedInitialRemotePath);
        Assert.Null(pane.SftpReconnectPathHint);
        Assert.Equal("Connected", pane.Status);
    }

    [Fact]
    public async Task ReconnectPaneAsync_UsesPaneProtocolAndStateKey_ForSftpCompanionOfSshProfile()
    {
        var newSession = new DisposableSessionResult();
        var hostManager = new FakeEmbeddedSessionManager();
        hostManager.CreateHostControlCallback = (_, _, connectionType, sessionResult, _, _) =>
        {
            Assert.Equal("SFTP", connectionType);
            Assert.Same(newSession, sessionResult);
            return new DisposableHost();
        };

        var connectionService = new RecordingConnectionService(successfulSftpSession: newSession);
        var sut = CreateSplitService(connectionService, hostManager);

        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new()
            {
                Id = "server-1",
                DisplayName = "Server 1",
                ConnectionType = "SSH",
                RemoteServer = "server.example.com",
                SshUsername = "operator"
            }
        });

        const string sftpSessionId = "sftp-server-1-session";
        var pane = MakePane(paneId: "pane-1", serverId: sftpSessionId, connectionType: "SFTP");
        pane.OriginalServerId = "server-1";
        pane.Title = "Server 1";
        pane.HostControl = new DisposableHost();

        var session = new SessionTabViewModel { RootContent = pane };
        sut.ActiveSessionsProvider = () => new ObservableCollection<SessionTabViewModel> { session };

        var ex = await Record.ExceptionAsync(() => sut.ReconnectPaneAsync(session, pane.PaneId));

        Assert.Null(ex);
        Assert.Equal("SFTP", connectionService.LastProtocol);
        Assert.NotNull(connectionService.LastServer);
        Assert.Equal(sftpSessionId, connectionService.LastServer.Id);
        Assert.Equal("SFTP", connectionService.LastServer.ConnectionType);
        Assert.Equal(sftpSessionId, pane.ServerId);
        Assert.Equal("SFTP", pane.ConnectionType);
        Assert.Equal("Connected", pane.Status);
    }

    [Fact]
    public async Task ReconnectPaneAsync_HostFactoryFailure_DisposesNewSessionAndResetsNewState()
    {
        var newSession = new DisposableSessionResult();
        var hostManager = new FakeEmbeddedSessionManager();
        hostManager.CreateHostControlCallback = (_, _, _, _, _, _) =>
            throw new InvalidOperationException("factory failed");

        var sut = CreateSplitService(
            new SuccessfulRdpConnectionService(_connectionSm, newSession),
            hostManager);

        await _configManager.SaveServersAsync(new List<ServerProfileDto>
        {
            new()
            {
                Id = "server-1",
                DisplayName = "Server 1",
                ConnectionType = "RDP"
            }
        });

        var oldHost = new DisposableHost();
        var pane = MakePane(paneId: "pane-1", serverId: "server-1", connectionType: "RDP");
        pane.OriginalServerId = "server-1";
        pane.Title = "Server 1";
        pane.HostControl = oldHost;

        var session = new SessionTabViewModel { RootContent = pane };
        session.Title = "Split tab";
        var activeSessions = new ObservableCollection<SessionTabViewModel> { session };
        sut.ActiveSessionsProvider = () => activeSessions;

        Assert.True(_connectionSm.TryTransition("server-1", ConnectionState.Initializing));
        Assert.True(_connectionSm.TryTransition("server-1", ConnectionState.ValidatingConfig));
        Assert.True(_connectionSm.TryTransition("server-1", ConnectionState.LaunchingRdp));
        Assert.True(_connectionSm.TryTransition("server-1", ConnectionState.Connected));

        var ex = await Record.ExceptionAsync(() => sut.ReconnectPaneAsync(session, pane.PaneId));

        Assert.Null(ex);
        Assert.True(oldHost.Disposed);
        Assert.True(newSession.Disposed);
        Assert.Null(pane.HostControl);
        Assert.Equal("Error", pane.Status);
        Assert.Equal(ConnectionState.Disconnected, _connectionSm.GetState("server-1"));
    }

    // ── Category E: ToggleSplitOrientation ──────────────────────────────

    [Fact]
    public void ToggleSplitOrientation_HorizontalBecomesVertical()
    {
        var session = new SessionTabViewModel();
        var container = new SplitContainerModel
        {
            First = MakePane(),
            Second = MakePane(),
            Orientation = SplitOrientation.Horizontal
        };
        session.RootContent = container;

        _sut.ToggleSplitOrientation(session);

        Assert.Equal(SplitOrientation.Vertical, container.Orientation);
    }

    [Fact]
    public void ToggleSplitOrientation_VerticalBecomesHorizontal()
    {
        var session = new SessionTabViewModel();
        var container = new SplitContainerModel
        {
            First = MakePane(),
            Second = MakePane(),
            Orientation = SplitOrientation.Vertical
        };
        session.RootContent = container;

        _sut.ToggleSplitOrientation(session);

        Assert.Equal(SplitOrientation.Horizontal, container.Orientation);
    }

    [Fact]
    public void ToggleSplitOrientation_UnsplitSession_NoOp()
    {
        var session = new SessionTabViewModel();
        var leaf = MakePane();
        session.RootContent = leaf;

        var ex = Record.Exception(() => _sut.ToggleSplitOrientation(session));

        Assert.Null(ex);
        Assert.Same(leaf, session.RootContent);
    }

    // ── Category F: SplitSessionWithTool guards ─────────────────────────

    [Fact]
    public void SplitSessionWithTool_UnknownToolId_LeavesTreeUnchanged()
    {
        var session = new SessionTabViewModel();
        var originalRoot = session.RootContent;

        _sut.SplitSessionWithTool(session, "NOT_A_REAL_TOOL", SplitOrientation.Vertical);

        // Unknown toolId short-circuits before touching _sessionManager.
        Assert.Same(originalRoot, session.RootContent);
        Assert.False(session.IsSplit);
    }

    [Fact]
    public void SplitSessionWithTool_AtMaxPanes_SetsStatusAndLeavesTreeUnchanged()
    {
        var session = new SessionTabViewModel();
        session.RootContent = BuildEightLeafTree();
        Assert.Equal(SplitService.MaxPanesPerTab, SplitTreeHelper.CountLeaves(session.RootContent));
        var rootBefore = session.RootContent;

        string? capturedStatus = null;
        _sut.SetStatusText = s => capturedStatus = s;

        _sut.SplitSessionWithTool(session, "PING", SplitOrientation.Horizontal);

        Assert.Same(rootBefore, session.RootContent);
        Assert.Equal(SplitService.MaxPanesPerTab, SplitTreeHelper.CountLeaves(session.RootContent));
        // LocalizationManager has no strings loaded so the key is returned verbatim.
        Assert.Equal("SplitMaxPanesReached", capturedStatus);
    }

    // ── Category G: MergeExistingSession guards ─────────────────────────

    [Fact]
    public void MergeExistingSession_SourcePaneStillConnecting_IsBlocked()
    {
        SessionPaneModel connectedLeaf = MakePane(paneId: "connected", connectionType: "SSH");
        connectedLeaf.HostControl = new DisposableHost();

        SessionPaneModel connectingLeaf = MakePane(paneId: "connecting", connectionType: "SSH");
        connectingLeaf.Title = "Connecting host";
        // HostControl null + no FailureDetails => still connecting.

        SplitContainerModel sourceRoot = new()
        {
            First = connectedLeaf,
            Second = connectingLeaf,
            Orientation = SplitOrientation.Vertical
        };
        SessionTabViewModel source = new() { RootContent = sourceRoot };
        source.ServerId = "source-session";
        source.Title = "Source";

        SessionTabViewModel target = new();
        target.Title = "Target";
        ISplitContent targetRootBefore = target.RootContent;

        ObservableCollection<SessionTabViewModel> activeSessions = new() { target, source };
        _sut.ActiveSessionsProvider = () => activeSessions;
        string? capturedStatus = null;
        _sut.SetStatusText = s => capturedStatus = s;

        _sut.MergeExistingSession(target, "source-session", SplitOrientation.Vertical);

        // Merge blocked: nothing mutated, both sessions intact.
        Assert.Equal("SplitMergeBlockedByConnecting", capturedStatus);
        Assert.Contains(source, activeSessions);
        Assert.Same(sourceRoot, source.RootContent);
        Assert.Same(targetRootBefore, target.RootContent);
        Assert.False(target.IsSplit);
    }

    [Fact]
    public void MergeExistingSession_SourcePaneFailed_IsNotBlockedByConnectingGuard()
    {
        SessionPaneModel connectedLeaf = MakePane(paneId: "connected", connectionType: "SSH");
        connectedLeaf.HostControl = new DisposableHost();

        // Failed pane: HostControl null but HasFailureDetails true => terminal,
        // not in-flight. The connecting guard must NOT block it.
        SessionPaneModel failedLeaf = MakePane(paneId: "failed", connectionType: "SSH");
        failedLeaf.Title = "Failed host";
        failedLeaf.FailureDetails = new SessionDiagnostic(SessionFailureStage.Unknown, "TestFailure");

        SplitContainerModel sourceRoot = new()
        {
            First = connectedLeaf,
            Second = failedLeaf,
            Orientation = SplitOrientation.Vertical
        };
        SessionTabViewModel source = new() { RootContent = sourceRoot };
        source.ServerId = "source-session";
        source.Title = "Source";

        SessionTabViewModel target = new();
        target.Title = "Target";

        ObservableCollection<SessionTabViewModel> activeSessions = new() { target, source };
        _sut.ActiveSessionsProvider = () => activeSessions;
        string? capturedStatus = null;
        _sut.SetStatusText = s => capturedStatus = s;

        _sut.MergeExistingSession(target, "source-session", SplitOrientation.Vertical);

        // Merge proceeded: the connecting guard did not fire.
        Assert.NotEqual("SplitMergeBlockedByConnecting", capturedStatus);
        Assert.DoesNotContain(source, activeSessions);
        Assert.True(target.IsSplit);
    }

    [Fact]
    public void MergeExistingSession_ProfileIdOnly_DoesNotResolveDuplicate()
    {
        SessionTabViewModel source = new();
        source.ServerId = "runtime-source";
        source.OriginalServerId = "profile-shared";
        source.HostControl = new DisposableHost();
        ISplitContent sourceRootBefore = source.RootContent;

        SessionTabViewModel target = new();
        target.ServerId = "runtime-target";
        target.OriginalServerId = "profile-shared";
        target.HostControl = new DisposableHost();
        ISplitContent targetRootBefore = target.RootContent;

        ObservableCollection<SessionTabViewModel> activeSessions = new() { source, target };
        _sut.ActiveSessionsProvider = () => activeSessions;
        string? capturedStatus = null;
        _sut.SetStatusText = status => capturedStatus = status;

        _sut.MergeExistingSession(target, "profile-shared", SplitOrientation.Vertical);

        Assert.Equal("ErrorSplitSessionFailed", capturedStatus);
        Assert.Contains(source, activeSessions);
        Assert.Contains(target, activeSessions);
        Assert.Same(sourceRootBefore, source.RootContent);
        Assert.Same(targetRootBefore, target.RootContent);
        Assert.False(target.IsSplit);
    }

    [Fact]
    public void MergeExistingSession_EmptyRuntimeId_FailsClosed()
    {
        SessionTabViewModel firstEmptyIdSource = new();
        firstEmptyIdSource.ServerId = string.Empty;
        firstEmptyIdSource.HostControl = new DisposableHost();
        ISplitContent firstSourceRootBefore = firstEmptyIdSource.RootContent;

        SessionTabViewModel target = new();
        target.ServerId = "runtime-target";
        target.HostControl = new DisposableHost();
        ISplitContent targetRootBefore = target.RootContent;

        SessionTabViewModel secondEmptyIdSource = new();
        secondEmptyIdSource.ServerId = string.Empty;
        secondEmptyIdSource.HostControl = new DisposableHost();
        ISplitContent secondSourceRootBefore = secondEmptyIdSource.RootContent;

        ObservableCollection<SessionTabViewModel> activeSessions = new()
        {
            firstEmptyIdSource,
            target,
            secondEmptyIdSource
        };
        _sut.ActiveSessionsProvider = () => activeSessions;
        string? capturedStatus = null;
        _sut.SetStatusText = status => capturedStatus = status;

        _sut.MergeExistingSession(target, string.Empty, SplitOrientation.Vertical);

        Assert.Equal("ErrorSplitSessionFailed", capturedStatus);
        Assert.Contains(firstEmptyIdSource, activeSessions);
        Assert.Contains(target, activeSessions);
        Assert.Contains(secondEmptyIdSource, activeSessions);
        Assert.Same(firstSourceRootBefore, firstEmptyIdSource.RootContent);
        Assert.Same(targetRootBefore, target.RootContent);
        Assert.Same(secondSourceRootBefore, secondEmptyIdSource.RootContent);
        Assert.False(target.IsSplit);
    }

    [Fact]
    public void MergeExistingSession_RuntimeId_SelectsExactDuplicate()
    {
        SessionTabViewModel firstDuplicate = new();
        firstDuplicate.ServerId = "runtime-first";
        firstDuplicate.OriginalServerId = "profile-shared";
        firstDuplicate.HostControl = new DisposableHost();

        SessionTabViewModel target = new();
        target.ServerId = "runtime-target";
        target.OriginalServerId = "profile-shared";
        target.HostControl = new DisposableHost();

        SessionTabViewModel selectedDuplicate = new();
        selectedDuplicate.ServerId = "runtime-selected";
        selectedDuplicate.OriginalServerId = "profile-shared";
        selectedDuplicate.HostControl = new DisposableHost();

        ObservableCollection<SessionTabViewModel> activeSessions = new()
        {
            firstDuplicate,
            target,
            selectedDuplicate
        };
        _sut.ActiveSessionsProvider = () => activeSessions;

        _sut.MergeExistingSession(target, "runtime-selected", SplitOrientation.Vertical);

        Assert.Contains(firstDuplicate, activeSessions);
        Assert.Contains(target, activeSessions);
        Assert.DoesNotContain(selectedDuplicate, activeSessions);
        Assert.True(target.IsSplit);
        Assert.Contains(
            SplitTreeHelper.EnumerateLeaves(target.RootContent),
            (SessionPaneModel pane) => string.Equals(
                pane.ServerId,
                "runtime-selected",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            SplitTreeHelper.EnumerateLeaves(target.RootContent),
            (SessionPaneModel pane) => string.Equals(
                pane.ServerId,
                "runtime-first",
                StringComparison.Ordinal));
    }

    [Fact]
    public void RestoreHostControls_MissingPane_DisposesOrphanedControl()
    {
        var session = new SessionTabViewModel
        {
            RootContent = MakePane(paneId: "existing")
        };
        var orphanedHost = new DisposableHost();
        var hostControls = new Dictionary<string, object?>
        {
            ["missing"] = orphanedHost
        };

        SplitService.RestoreHostControls(session, hostControls);

        Assert.True(orphanedHost.Disposed);
    }

    // ── Category H: Forced embedded mode policy ────────────────────────

    [Fact]
    public void ForceEmbeddedMode_RdpExternal_ReturnsTrue_AndSetsEmbedded()
    {
        var server = new ServerProfileDto
        {
            ConnectionType = "RDP",
            RdpMode = "External"
        };

        var converted = SplitService.ForceEmbeddedMode(server);

        Assert.True(converted);
        Assert.Equal("Embedded", server.RdpMode);
    }

    [Fact]
    public void ForceEmbeddedMode_RdpEmbedded_ReturnsFalse_AndKeepsEmbedded()
    {
        var server = new ServerProfileDto
        {
            ConnectionType = "RDP",
            RdpMode = "Embedded"
        };

        var converted = SplitService.ForceEmbeddedMode(server);

        Assert.False(converted);
        Assert.Equal("Embedded", server.RdpMode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private CancellationToken InvokeGetSessionToken(SessionTabViewModel session)
    {
        var method = typeof(SplitService).GetMethod(
            "GetSessionToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (CancellationToken)method!.Invoke(_sut, new object[] { session })!;
    }

    private SplitService CreateSplitService(IConnectionService connectionService)
        => CreateSplitService(connectionService, new FakeEmbeddedSessionManager());

    private SplitService CreateSplitService(
        IConnectionService connectionService,
        IEmbeddedSessionManager sessionManager)
        => new(
            _configManager,
            _localizer,
            _connectionSm,
            _tunnelManager,
            sessionManager,
            connectionService,
            _toolRegistry,
            dialogService: null!,
            closeArbiter: new PaneCloseArbiter());

    private void RegisterTrackedTunnel(string serverId, int localPort)
    {
        var info = new TunnelInfo("gateway", localPort, "target.internal", 3389, DateTime.UtcNow, true);
        Assert.True(_tunnelManager.TryRegisterExternalTunnel(info, new DisposableHost(), () => true));
        _tunnelManager.AddReference(localPort);

        Assert.True(_connectionSm.TryTransition(serverId, ConnectionState.Initializing));
        _connectionSm.SetTunnelInfo(serverId, localPort, processId: 123);
    }

    private void RegisterConnectedState(string serverId, ConnectionState launchingState, int localPort)
    {
        Assert.True(_connectionSm.TryTransition(serverId, ConnectionState.Initializing));
        Assert.True(_connectionSm.TryTransition(serverId, ConnectionState.ValidatingConfig));
        _connectionSm.SetTunnelInfo(serverId, localPort, processId: 123);
        Assert.True(_connectionSm.TryTransition(serverId, launchingState));
        Assert.True(_connectionSm.TryTransition(serverId, ConnectionState.Connected));
    }

    private void AssertServerStateReset(string serverId)
    {
        Assert.Equal(ConnectionState.Disconnected, _connectionSm.GetState(serverId));
        Assert.Null(_connectionSm.GetStateData(serverId)?.TunnelLocalPort);
        Assert.Null(_connectionSm.GetStateData(serverId)?.TunnelProcessId);
    }

    private void AssertSingleTunnelReferenceReleased(int localPort)
    {
        Assert.True(_tunnelManager.HasTunnel(localPort));
        Assert.True(_tunnelManager.ReleaseReference(localPort));
        Assert.False(_tunnelManager.HasTunnel(localPort));
    }

    private static SessionPaneModel MakePane(
        string? paneId = null,
        string? serverId = null,
        string connectionType = "")
    {
        var pane = new SessionPaneModel { ConnectionType = connectionType };
        if (paneId is not null) pane.PaneId = paneId;
        if (serverId is not null) pane.ServerId = serverId;
        return pane;
    }

    /// <summary>
    /// Builds a binary tree with exactly <see cref="SplitService.MaxPanesPerTab"/>
    /// leaves (8), all marked as tool panes with canClose() == true so the
    /// tree passes <c>CloseAllPanes</c> but saturates the pane budget for
    /// <c>SplitSessionWithTool</c>.
    /// </summary>
    private static ISplitContent BuildEightLeafTree()
    {
        static SplitContainerModel Split(ISplitContent a, ISplitContent b)
            => new() { First = a, Second = b, Orientation = SplitOrientation.Vertical };

        return Split(
            Split(Split(MakePane(), MakePane()), Split(MakePane(), MakePane())),
            Split(Split(MakePane(), MakePane()), Split(MakePane(), MakePane())));
    }

    /// <summary>
    /// Minimal <see cref="IToolView"/> stub for exercising the
    /// <c>CanClose()</c> guard in <see cref="SplitService.CloseAllPanes"/>.
    /// </summary>
    private sealed class StubToolView : IToolView
    {
        private readonly bool _canClose;
        public bool Disposed { get; private set; }

        public StubToolView(bool canClose)
        {
            _canClose = canClose;
        }

        public void Initialize(ToolContext? context, LocalizationManager? localizer) { }
        public bool CanClose() => _canClose;
        public void Dispose() => Disposed = true;
    }

    private sealed class DisposableHost : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class DisposableSessionResult : ISessionResult, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Records every profile copy dispatched to a connect, so a test can read the identifier each
    /// pane actually ran under rather than the one it expected to be given.
    /// </summary>
    private sealed class AllProfilesRecordingConnectionService : IConnectionService
    {
        public List<ServerProfileDto> Profiles { get; } = [];

        public AppSettings? CurrentSettings => null;

        public PreflightResult RunPreflight(ServerProfileDto server, AppSettings settings)
            => PreflightResult.Ok();

        public Task<ConnectionResult> ConnectSshAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => Record(server, ct);

        public Task<ConnectionResult> ConnectRdpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
            => Record(server, ct);

        public Task<ConnectionResult> ConnectSftpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => Record(server, ct);

        public Task<ConnectionResult> ConnectVncAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => Record(server, ct);

        public Task<ConnectionResult> ConnectTelnetAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => Record(server, ct);

        public Task<ConnectionResult> ConnectFtpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => Record(server, ct);

        public Task<ConnectionResult> ConnectCitrixAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => Record(server, ct);

        public Task<ConnectionResult> ConnectLocalShellAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => Record(server, ct);

        public Task<ConnectionResult> ConnectWinRmAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => Record(server, ct);

        public void Dispose()
        {
        }

        private Task<ConnectionResult> Record(ServerProfileDto server, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Profiles.Add(server);

            // Failing the dispatch keeps the test clear of host-control materialization, which
            // needs a dispatcher. The profile has already been recorded by then.
            return Task.FromResult(new ConnectionResult(false, "recorded", null));
        }
    }

    private sealed class RecordingConnectionService : IConnectionService
    {
        private readonly ISessionResult? _successfulSftpSession;
        private readonly ConnectionResult? _failureResult;
        private readonly ConnectionStateMachine? _stateMachine;
        private readonly ISessionResult? _successfulWinRmSession;

        public RecordingConnectionService(
            ISessionResult? successfulSftpSession = null,
            ConnectionResult? failureResult = null,
            ConnectionStateMachine? stateMachine = null,
            ISessionResult? successfulWinRmSession = null)
        {
            _successfulSftpSession = successfulSftpSession;
            _failureResult = failureResult;
            _stateMachine = stateMachine;
            _successfulWinRmSession = successfulWinRmSession;
        }

        public bool ConnectInvoked { get; private set; }
        public string? LastProtocol { get; private set; }
        public ServerProfileDto? LastServer { get; private set; }
        public ConnectionState? WinRmStateAtDispatch { get; private set; }
        public string? WinRmServerIdAtDispatch { get; private set; }

        public AppSettings? CurrentSettings => null;

        public PreflightResult RunPreflight(ServerProfileDto server, AppSettings settings)
            => PreflightResult.Ok();

        public Task<ConnectionResult> ConnectSshAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => RecordConnectAsync("SSH", server, ct);

        public Task<ConnectionResult> ConnectRdpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
            => RecordConnectAsync("RDP", server, ct);

        public Task<ConnectionResult> ConnectSftpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
        {
            RecordConnect("SFTP", server, ct);
            if (_successfulSftpSession is null)
            {
                return Task.FromResult(_failureResult ?? new ConnectionResult(false, "unexpected connect", null));
            }

            return Task.FromResult(new ConnectionResult(true, null, _successfulSftpSession));
        }

        public Task<ConnectionResult> ConnectVncAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => RecordConnectAsync("VNC", server, ct);

        public Task<ConnectionResult> ConnectTelnetAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => RecordConnectAsync("TELNET", server, ct);

        public Task<ConnectionResult> ConnectFtpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => RecordConnectAsync("FTP", server, ct);

        public Task<ConnectionResult> ConnectCitrixAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => RecordConnectAsync("CITRIX", server, ct);

        public Task<ConnectionResult> ConnectLocalShellAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => RecordConnectAsync("LOCAL", server, ct);

        public Task<ConnectionResult> ConnectWinRmAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
        {
            RecordConnect("WINRM", server, ct);
            if (_stateMachine is null || _successfulWinRmSession is null)
            {
                return Task.FromResult(
                    _failureResult ?? new ConnectionResult(false, "unexpected connect", null));
            }

            WinRmServerIdAtDispatch = server.Id;
            WinRmStateAtDispatch = _stateMachine.GetState(server.Id);

            // Mirror WinRmHandler: attempt downstream transitions without
            // manufacturing the caller-owned Initializing state.
            _stateMachine.TryTransition(server.Id, ConnectionState.ValidatingConfig);
            _stateMachine.TryTransition(server.Id, ConnectionState.LaunchingWinRm);
            _stateMachine.TryTransition(server.Id, ConnectionState.RemoteSessionHandedOff);

            return Task.FromResult(new ConnectionResult(true, null, _successfulWinRmSession));
        }

        public void Dispose() { }

        private Task<ConnectionResult> RecordConnectAsync(
            string protocol,
            ServerProfileDto server,
            CancellationToken ct)
        {
            RecordConnect(protocol, server, ct);
            return Task.FromResult(_failureResult ?? new ConnectionResult(false, "unexpected connect", null));
        }

        private void RecordConnect(string protocol, ServerProfileDto server, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ConnectInvoked = true;
            LastProtocol = protocol;
            LastServer = server;
        }
    }

    private sealed class SharedTunnelWinRmConnectionService : IConnectionService
    {
        private readonly ConnectionStateMachine _connectionSm;
        private readonly TunnelManager _tunnelManager;
        private readonly TunnelInfo _tunnelInfo;
        private readonly WinRmConnectionOutcome _outcome;
        private readonly List<string> _serverIds = [];

        public SharedTunnelWinRmConnectionService(
            ConnectionStateMachine connectionSm,
            TunnelManager tunnelManager,
            TunnelInfo tunnelInfo,
            WinRmConnectionOutcome outcome = WinRmConnectionOutcome.Success)
        {
            _connectionSm = connectionSm;
            _tunnelManager = tunnelManager;
            _tunnelInfo = tunnelInfo;
            _outcome = outcome;
        }

        public IReadOnlyList<string> ServerIds => _serverIds;

        public AppSettings? CurrentSettings => null;

        public PreflightResult RunPreflight(ServerProfileDto server, AppSettings settings)
            => PreflightResult.Ok();

        public Task<ConnectionResult> ConnectSshAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectRdpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectSftpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectVncAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectTelnetAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectFtpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectCitrixAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectLocalShellAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectWinRmAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _serverIds.Add(server.Id);

            if (_serverIds.Count == 1)
            {
                bool registered = _tunnelManager.TryRegisterExternalTunnel(
                    _tunnelInfo,
                    new DisposableHost(),
                    () => true);
                if (!registered)
                {
                    throw new InvalidOperationException("Shared test tunnel registration failed.");
                }
            }
            else
            {
                _tunnelManager.AddReference(_tunnelInfo.LocalPort);
            }

            if (_outcome != WinRmConnectionOutcome.Success)
            {
                _tunnelManager.AddReference(_tunnelInfo.LocalPort);
            }

            _connectionSm.SetTunnelInfo(server.Id, _tunnelInfo.LocalPort, processId: 0);

            if (_outcome != WinRmConnectionOutcome.Success)
            {
                bool closed = _tunnelManager.ReleaseReference(_tunnelInfo.LocalPort);
                if (closed)
                {
                    throw new InvalidOperationException(
                        "Failed connection unexpectedly closed the shared test tunnel.");
                }
            }

            if (_outcome == WinRmConnectionOutcome.FailureAfterTransportCleanup)
            {
                return Task.FromResult(new ConnectionResult(false, "connection failed", null));
            }

            if (_outcome == WinRmConnectionOutcome.CancellationAfterTransportCleanup)
            {
                throw new OperationCanceledException(ct);
            }

            if (_outcome == WinRmConnectionOutcome.ExceptionAfterTransportCleanup)
            {
                throw new InvalidOperationException("connection failed after transport cleanup");
            }

            ISessionResult sessionResult = new DisposableSessionResult();
            return Task.FromResult(new ConnectionResult(true, null, sessionResult));
        }

        public void Dispose() { }

        private static Task<ConnectionResult> NotScriptedAsync()
            => Task.FromResult(new ConnectionResult(false, "not scripted", null));
    }

    /// <summary>
    /// Scripted WINRM dispatch outcomes used to verify split-state ownership.
    /// </summary>
    public enum WinRmConnectionOutcome
    {
        Success,
        FailureAfterTransportCleanup,
        CancellationAfterTransportCleanup,
        ExceptionAfterTransportCleanup
    }

    private sealed class SuccessfulRdpConnectionService : IConnectionService
    {
        private readonly ConnectionStateMachine _connectionSm;
        private readonly ISessionResult _sessionResult;

        public SuccessfulRdpConnectionService(
            ConnectionStateMachine connectionSm,
            ISessionResult sessionResult)
        {
            _connectionSm = connectionSm;
            _sessionResult = sessionResult;
        }

        public AppSettings? CurrentSettings => null;

        public PreflightResult RunPreflight(ServerProfileDto server, AppSettings settings)
            => PreflightResult.Ok();

        public Task<ConnectionResult> ConnectSshAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectRdpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
        {
            ct.ThrowIfCancellationRequested();

            if (!_connectionSm.TryTransition(server.Id, ConnectionState.Initializing)
                || !_connectionSm.TryTransition(server.Id, ConnectionState.ValidatingConfig)
                || !_connectionSm.TryTransition(server.Id, ConnectionState.LaunchingRdp)
                || !_connectionSm.TryTransition(server.Id, ConnectionState.Connected))
            {
                throw new InvalidOperationException("new connection state was not reset before reconnect");
            }

            return Task.FromResult(new ConnectionResult(true, null, _sessionResult));
        }

        public Task<ConnectionResult> ConnectSftpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectVncAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectTelnetAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectFtpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectCitrixAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectLocalShellAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectWinRmAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public void Dispose() { }

        private static Task<ConnectionResult> NotScriptedAsync()
            => Task.FromResult(new ConnectionResult(false, "not scripted", null));
    }

    private sealed class ThrowingConnectionService : IConnectionService
    {
        private readonly Exception _exception;

        public ThrowingConnectionService(Exception exception)
        {
            _exception = exception;
        }

        public AppSettings? CurrentSettings => null;

        public PreflightResult RunPreflight(ServerProfileDto server, AppSettings settings)
            => PreflightResult.Ok();

        public Task<ConnectionResult> ConnectSshAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectRdpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
            => Task.FromException<ConnectionResult>(_exception);

        public Task<ConnectionResult> ConnectSftpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectVncAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectTelnetAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectFtpAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectCitrixAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectLocalShellAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public Task<ConnectionResult> ConnectWinRmAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct = default)
            => NotScriptedAsync();

        public void Dispose() { }

        private static Task<ConnectionResult> NotScriptedAsync()
            => Task.FromResult(new ConnectionResult(false, "not scripted", null));
    }

    private sealed class FakeEmbeddedSessionManager : IEmbeddedSessionManager
    {
        public Func<SessionTabViewModel, string, string, ISessionResult, AppSettings?, string?, object>? CreateHostControlCallback { get; set; }
        public Action<byte[], object?>? BroadcastCallback { get; set; }
        public Action<SessionTabViewModel>? SplitRequestedCallback { get; set; }
        public Func<bool>? IsBroadcastActive { get; set; }
        public Action<SessionTabViewModel, string, string>? ReconnectRequestedCallback { get; set; }
        public Action<SessionTabViewModel, SessionPaneModel>? ReconnectPaneRequestedCallback { get; set; }
        public Action<SessionTabViewModel, SessionPaneModel, DisconnectReason>? DisconnectRequestedCallback { get; set; }
        public Action<string>? EditServerRequestedCallback { get; set; }
        public Action<SessionTabViewModel>? CloseRequestedCallback { get; set; }
        public Func<string, string, ToolContext?, Task>? OpenToolCallback { get; set; }

        public object CreateHostControl(
            SessionTabViewModel sessionTab,
            string displayName,
            string connectionType,
            ISessionResult session,
            AppSettings? settings = null,
            string? initialRemotePath = null)
        {
            if (CreateHostControlCallback is not null)
            {
                return CreateHostControlCallback(
                    sessionTab,
                    displayName,
                    connectionType,
                    session,
                    settings,
                    initialRemotePath);
            }

            throw new NotSupportedException();
        }

        public void DisconnectSession(SessionPaneModel pane, DisconnectReason reason)
        {
            if (pane.HostControl is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        public EmbeddedSshView CreateConnectingSshHostControl(
            SessionTabViewModel sessionTab,
            string displayName,
            ServerProfileDto server,
            AppSettings? settings = null)
        {
            throw new NotSupportedException();
        }

        public void AttachSshSession(
            SessionTabViewModel sessionTab,
            ISessionResult sessionResult,
            AppSettings? settings = null)
        {
            throw new NotSupportedException();
        }

        public object CreateToolControl(
            SessionTabViewModel sessionTab,
            string toolId,
            ToolContext? context,
            AppSettings? settings = null)
        {
            throw new NotSupportedException();
        }

        public bool TrySendCommandToSession(SessionTabViewModel session, string command) => false;
    }

    private sealed class RecordingPaneOwnerHost : ISessionPaneOwner
    {
        public SessionPaneModel? OwningPane { get; private set; }

        public void SetOwningPane(SessionPaneModel pane)
        {
            OwningPane = pane;
        }
    }

    // RDP-023. The pane-scoped copy used to be a hand-written assignment list that had drifted from
    // the one in the RDP path, in the other direction: it dropped the JSON extension data. Both now
    // go through the single fidelity primitive, and this pins that the split path really does.
    [Fact]
    public void APaneScopedProfile_CarriesWhatTheOldManualListDropped()
    {
        ServerProfileDto source = System.Text.Json.JsonSerializer.Deserialize<ServerProfileDto>(
            """{"id":"src","displayName":"Src","connectionType":"SSH","unknownFutureField":{"n":1}}""")!;
        source.SshKeyPath = @"C:\keys\id.ppk";
        source.SshPasswordEncrypted = "cipher";
        source.PostConnectSteps.Add(new Heimdall.Core.Models.PostConnectStep { Input = "uptime" });

        Assert.True(source.ExtensionData.ContainsKey("unknownFutureField"));
        Assert.False(source.HasSshKeyPassphraseEncryptedField);

        ServerProfileDto pane = SplitService.CreatePaneScopedServerProfile(source, "pane-1", "SSH");

        Assert.NotSame(source, pane);
        Assert.Equal("pane-1", pane.Id);

        // Extension data survives, which the old list dropped.
        Assert.True(pane.ExtensionData.ContainsKey("unknownFutureField"));

        // And the presence flag is preserved rather than fabricated, which is what the old guarded
        // copy existed to achieve and what the primitive now carries by construction.
        Assert.False(pane.HasSshKeyPassphraseEncryptedField);
        Assert.True(pane.UsesLegacySshCredentialMapping);

        // The step list is deep-copied, so a pane cannot rewrite the inventory profile's steps.
        pane.PostConnectSteps[0].Input = "rebooted";
        Assert.Equal("uptime", source.PostConnectSteps[0].Input);
    }
}
