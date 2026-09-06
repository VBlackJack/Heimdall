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

using System.Reflection;
using System.Runtime.ExceptionServices;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Terminal;

namespace Heimdall.App.Tests;

/// <summary>
/// Exercises the real <see cref="EmbeddedSessionManager.DisconnectSession"/> dispatch over
/// <see cref="SessionPaneModel.HostControl"/>.
/// </summary>
/// <remarks>
/// The <c>EmbeddedRdpView</c> arm is intentionally not covered: it is pure delegation to
/// <c>DisconnectForTeardown</c> and would require a WPF/COM host harness. The manager is built
/// with <c>null!</c> dependencies on purpose because <c>DisconnectSession</c> uses no instance
/// state; a future dependency on those fields would surface here. The post-cancel behavior where
/// MsTscAx raises <c>OnDisconnected</c> after <c>CancelAutoReconnect</c>, re-surfacing the overlay,
/// is a runtime COM contract and is <b>not</b> validated: the cancel travelled through an event the
/// control never delivered, because the handler was declared on another event's dispatch id. That
/// is fixed, so the behavior is now reachable for the first time and still awaits a live pass.
/// </remarks>
[Collection(CredentialDialogPasswordDirtyCollection.Name)]
public sealed class EmbeddedSessionManagerDisconnectTests
{
    /// <summary>
    /// Reproduction for the parked finding C-07 of the SSH audit of 2026-09-06. The WinRM
    /// process-exit handler runs on the process's exit thread and the pane close runs on the
    /// UI thread; both read the pane's tunnel port, release it, and only then tear the state
    /// down. When the close lands between the exit handler's read and its teardown, one
    /// acquisition is released twice and a tunnel another pane still holds is closed.
    /// </summary>
    /// <remarks>
    /// The interleaving is forced, not raced: the tunnel service double parks the exit
    /// handler inside its release call, after the read, until the close has run.
    /// </remarks>
    [Fact]
    public void C07_WinRmProcessExitRacingAPaneClose_ReleasesOneAcquisitionOnce()
    {
        RunOnStaThread(() =>
        {
            App application = CreateApplication();
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"heimdall-c07-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tempDir, "config"));
            try
            {
                const string serverId = "winrm-shared-tunnel";
                const int localPort = 45140;
                const string chainKey = "chain-c07";
                const string remoteHost = "10.0.0.9";
                const int remotePort = 5985;
                ConnectionStateMachine stateMachine = new ConnectionStateMachine();
                SeedWinRmHandedOffState(stateMachine, serverId, localPort);

                using TunnelManager tunnelManager = new TunnelManager();
                TunnelInfo info = TunnelManager.BuildTunnelInfo(
                    "gateway", localPort, remoteHost, remotePort,
                    socksProxyPort: 0, remoteBindPort: 0, remoteLocalPort: 0,
                    gatewayRoute: null, gatewayChainKey: chainKey);
                // Reference 1: this pane. Reference 2: another pane sharing the tunnel.
                Assert.True(tunnelManager.TryRegisterExternalTunnel(info, new NoopDisposable(), () => true));
                Assert.NotNull(tunnelManager.AcquireReusableTunnel(chainKey, remoteHost, remotePort, 0, 0, 0));

                GatedTunnelService tunnelService = new GatedTunnelService(tunnelManager);
                RaisingTerminalSession terminalSession = new RaisingTerminalSession();
                EmbeddedSessionManager manager = CreateManager(stateMachine, tunnelService);
                SessionPaneModel pane = new SessionPaneModel
                {
                    PaneId = "winrm-shared-pane",
                    ServerId = serverId,
                    OriginalServerId = "winrm-inventory",
                    Title = "WinRM shared",
                    ConnectionType = "WINRM",
                    Status = "RemoteSessionHandedOff"
                };
                SessionPaneModel sibling = new SessionPaneModel
                {
                    PaneId = "sibling-tool",
                    ConnectionType = "TOOL:NOTES"
                };
                SessionTabViewModel tab = new SessionTabViewModel
                {
                    RootContent = new SplitContainerModel
                    {
                        First = pane,
                        Second = sibling,
                        Orientation = SplitOrientation.Horizontal
                    }
                };
                object host = manager.CreateHostControl(
                    tab,
                    pane.Title,
                    pane.ConnectionType,
                    new TerminalSessionResult(terminalSession));
                pane.HostControl = host;
                ((EmbeddedSshView)host).SetOwningPane(pane);

                SplitService split = new SplitService(
                    new ConfigManager(tempDir),
                    new LocalizationManager(),
                    stateMachine,
                    tunnelManager,
                    manager,
                    null!,
                    new ToolRegistry(),
                    null!,
                    new PaneCloseArbiter());

                // The process exits: its handler reads the port and enters the release.
                Thread exitThread = new Thread(() => terminalSession.RaiseProcessExited(0));
                exitThread.Start();
                Assert.True(tunnelService.ReadDone.Wait(TimeSpan.FromSeconds(10)), "the exit handler never reached its release");

                // Meanwhile the user closes the pane: the state still carries the port.
                split.ClosePane(tab, pane.PaneId, CloseRequest.Interactive(DisconnectReason.UserAction));

                tunnelService.Proceed.Set();
                Assert.True(exitThread.Join(TimeSpan.FromSeconds(10)), "the exit handler never finished");

                Assert.NotNull(tunnelManager.GetTunnel(localPort));
            }
            finally
            {
                ResetApplicationSingletonForTest(application);
                try
                {
                    System.IO.Directory.Delete(tempDir, recursive: true);
                }
                catch (System.IO.IOException)
                {
                }
            }
        });
    }

    [Fact]
    public void WinRmProcessExit_ReleasesTunnelAndTearsDownRuntimeState()
    {
        RunOnStaThread(() =>
        {
            App application = CreateApplication();
            try
            {
                const string runtimeServerId = "winrm-runtime-exit";
                const int localPort = 45131;
                ConnectionStateMachine stateMachine = new ConnectionStateMachine();
                SeedWinRmHandedOffState(stateMachine, runtimeServerId, localPort);
                RecordingTunnelService tunnelService = new RecordingTunnelService();
                RaisingTerminalSession terminalSession = new RaisingTerminalSession();
                EmbeddedSessionManager manager = CreateManager(stateMachine, tunnelService);
                SessionPaneModel pane = new SessionPaneModel
                {
                    PaneId = "winrm-root-pane",
                    ServerId = runtimeServerId,
                    OriginalServerId = "winrm-inventory",
                    Title = "WinRM root",
                    ConnectionType = "WINRM",
                    Status = "RemoteSessionHandedOff"
                };
                SessionTabViewModel tab = new SessionTabViewModel { RootContent = pane };
                object host = manager.CreateHostControl(
                    tab,
                    pane.Title,
                    pane.ConnectionType,
                    new TerminalSessionResult(terminalSession));
                pane.HostControl = host;

                terminalSession.RaiseProcessExited(0);
                terminalSession.RaiseProcessExited(0);

                Assert.Null(stateMachine.GetStateData(runtimeServerId));
                Assert.Equal(new[] { localPort }, tunnelService.ReleasedPorts);

                ((IDisposable)host).Dispose();

                const string primaryServerId = "winrm-primary-runtime";
                const string splitServerId = "winrm-split-runtime";
                const int primaryPort = 45132;
                const int splitPort = 45133;
                ConnectionStateMachine splitStateMachine = new ConnectionStateMachine();
                SeedWinRmHandedOffState(splitStateMachine, primaryServerId, primaryPort);
                SeedWinRmHandedOffState(splitStateMachine, splitServerId, splitPort);
                RecordingTunnelService splitTunnelService = new RecordingTunnelService();
                RaisingTerminalSession splitTerminalSession = new RaisingTerminalSession();
                EmbeddedSessionManager splitManager = CreateManager(splitStateMachine, splitTunnelService);
                SessionPaneModel primaryPane = new SessionPaneModel
                {
                    PaneId = "winrm-primary-pane",
                    ServerId = primaryServerId,
                    ConnectionType = "WINRM"
                };
                SessionPaneModel splitPane = new SessionPaneModel
                {
                    PaneId = "winrm-split-pane",
                    ServerId = splitServerId,
                    ConnectionType = "WINRM"
                };
                SessionTabViewModel splitTab = new SessionTabViewModel
                {
                    RootContent = new SplitContainerModel
                    {
                        First = primaryPane,
                        Second = splitPane,
                        Orientation = SplitOrientation.Vertical
                    }
                };
                object splitHost = splitManager.CreateHostControl(
                    splitTab,
                    "WinRM split",
                    "WINRM",
                    new TerminalSessionResult(splitTerminalSession));
                ISessionPaneOwner paneOwner = Assert.IsAssignableFrom<ISessionPaneOwner>(splitHost);
                paneOwner.SetOwningPane(splitPane);
                splitPane.HostControl = splitHost;

                splitTerminalSession.RaiseProcessExited(0);

                Assert.Equal(ConnectionState.RemoteSessionHandedOff, splitStateMachine.GetState(primaryServerId));
                Assert.Null(splitStateMachine.GetStateData(splitServerId));
                Assert.Equal(new[] { splitPort }, splitTunnelService.ReleasedPorts);

                ((IDisposable)splitHost).Dispose();
            }
            finally
            {
                application.Shutdown();
                application.Dispatcher.InvokeShutdown();
                ResetApplicationSingletonForTest(application);
            }
        });
    }

    [Fact]
    public void DisconnectSession_NullPane_Throws()
    {
        EmbeddedSessionManager manager = CreateManager();

        Assert.Throws<ArgumentNullException>(() =>
            manager.DisconnectSession(null!, DisconnectReason.UserAction));
    }

    [Fact]
    public void DisconnectSession_DisposableHost_DisposesOnce()
    {
        EmbeddedSessionManager manager = CreateManager();
        DisposableHostSpy host = new DisposableHostSpy();
        SessionPaneModel pane = CreatePane(host);

        manager.DisconnectSession(pane, DisconnectReason.UserAction);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public void DisconnectSession_HostThrowsObjectDisposed_Swallowed()
    {
        EmbeddedSessionManager manager = CreateManager();
        DisposableHostSpy host = new DisposableHostSpy(new ObjectDisposedException("host"));
        SessionPaneModel pane = CreatePane(host);

        manager.DisconnectSession(pane, DisconnectReason.UserAction);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public void DisconnectSession_HostThrowsGeneric_Swallowed()
    {
        EmbeddedSessionManager manager = CreateManager();
        DisposableHostSpy host = new DisposableHostSpy(new InvalidOperationException("boom"));
        SessionPaneModel pane = CreatePane(host);

        manager.DisconnectSession(pane, DisconnectReason.UserAction);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public void DisconnectSession_NullHost_DoesNotThrow()
    {
        EmbeddedSessionManager manager = CreateManager();
        SessionPaneModel pane = CreatePane(null);

        manager.DisconnectSession(pane, DisconnectReason.UserAction);

        Assert.Null(pane.HostControl);
    }

    [Fact]
    public void DisconnectSession_NonDisposableHost_DoesNotThrow()
    {
        EmbeddedSessionManager manager = CreateManager();
        object host = new object();
        SessionPaneModel pane = CreatePane(host);

        manager.DisconnectSession(pane, DisconnectReason.UserAction);

        Assert.Same(host, pane.HostControl);
    }

    [Fact]
    public void ResolveSftpFollowPane_ReturnsFollowEnabledSftpSibling()
    {
        object sshHost = new();
        object sftpHost = new();
        SessionPaneModel sshPane = CreatePane(sshHost);
        sshPane.ConnectionType = "SSH";
        SessionPaneModel sftpPane = CreatePane(sftpHost);
        sftpPane.ConnectionType = "SFTP";
        sftpPane.SftpFollowSshDirectory = true;
        SplitContainerModel root = new()
        {
            First = sshPane,
            Second = sftpPane,
            Orientation = SplitOrientation.Vertical
        };

        SessionPaneModel? resolved = EmbeddedSessionManager.ResolveSftpFollowPane(
            root,
            hostControl => ReferenceEquals(hostControl, sftpHost));

        Assert.Same(sftpPane, resolved);
    }

    [Fact]
    public void ResolveSftpFollowPane_ReturnsNullWhenFollowIsOff()
    {
        object sftpHost = new();
        SessionPaneModel sftpPane = CreatePane(sftpHost);
        sftpPane.ConnectionType = "SFTP";
        sftpPane.SftpFollowSshDirectory = false;

        SessionPaneModel? resolved = EmbeddedSessionManager.ResolveSftpFollowPane(
            sftpPane,
            hostControl => ReferenceEquals(hostControl, sftpHost));

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveSftpFollowPane_ReturnsNullWhenNoSftpSiblingExists()
    {
        object sshHost = new();
        SessionPaneModel sshPane = CreatePane(sshHost);
        sshPane.ConnectionType = "SSH";
        sshPane.SftpFollowSshDirectory = true;

        SessionPaneModel? resolved = EmbeddedSessionManager.ResolveSftpFollowPane(
            sshPane,
            static _ => false);

        Assert.Null(resolved);
    }

    private static EmbeddedSessionManager CreateManager()
        => new EmbeddedSessionManager(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private static App CreateApplication()
    {
        Assert.Null(System.Windows.Application.Current);
        App application = new App();
        application.InitializeComponent();
        return application;
    }

    private static void ResetApplicationSingletonForTest(App application)
    {
        Assert.Same(application, System.Windows.Application.Current);
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        FieldInfo? appInstance = typeof(System.Windows.Application).GetField("_appInstance", flags);
        FieldInfo? appCreated = typeof(System.Windows.Application).GetField("_appCreatedInThisAppDomain", flags);
        FieldInfo? isShuttingDown = typeof(System.Windows.Application).GetField("_isShuttingDown", flags);
        Assert.NotNull(appInstance);
        Assert.NotNull(appCreated);
        Assert.NotNull(isShuttingDown);
        appInstance.SetValue(null, null);
        appCreated.SetValue(null, false);
        isShuttingDown.SetValue(null, false);
        Assert.Null(System.Windows.Application.Current);
    }

    private static EmbeddedSessionManager CreateManager(
        ConnectionStateMachine stateMachine,
        ITunnelService tunnelService)
    {
        return new EmbeddedSessionManager(
            null!,
            null!,
            null!,
            stateMachine,
            null!,
            tunnelService,
            null!,
            null!,
            null!,
            null!);
    }

    private static void SeedWinRmHandedOffState(
        ConnectionStateMachine stateMachine,
        string serverId,
        int localPort)
    {
        Assert.True(stateMachine.TryTransition(serverId, ConnectionState.Initializing));
        Assert.True(stateMachine.TryTransition(serverId, ConnectionState.ValidatingConfig));
        Assert.True(stateMachine.TryTransition(serverId, ConnectionState.EstablishingTunnel));
        stateMachine.SetTunnelInfo(serverId, localPort, processId: 0);
        Assert.True(stateMachine.TryTransition(serverId, ConnectionState.TunnelEstablished));
        Assert.True(stateMachine.TryTransition(serverId, ConnectionState.LaunchingWinRm));
        Assert.True(stateMachine.TryTransition(serverId, ConnectionState.RemoteSessionHandedOff));
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;
        Thread thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    private static SessionPaneModel CreatePane(object? hostControl)
    {
        return new SessionPaneModel
        {
            PaneId = "disconnect-test-pane",
            Title = "Disconnect Test",
            ConnectionType = "RDP",
            HostControl = hostControl
        };
    }

    private sealed class DisposableHostSpy : IDisposable
    {
        private readonly Exception? _exceptionToThrow;

        public DisposableHostSpy(Exception? exceptionToThrow = null)
        {
            _exceptionToThrow = exceptionToThrow;
        }

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;

            if (_exceptionToThrow is not null)
            {
                throw _exceptionToThrow;
            }
        }
    }

    private sealed class RecordingTunnelService : ITunnelService
    {
        public List<int> ReleasedPorts { get; } = new List<int>();

        public Task<TunnelSetupOutcome>
            SetupTunnelIfNeededAsync(
                ServerProfileDto server,
                int remotePort,
                AppSettings settings,
                CancellationToken ct = default,
                bool useOsAssignedLocalPort = false)
        {
            throw new NotSupportedException();
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort)
            => null;

        public void ReleaseTunnelReference(int localPort)
            => ReleasedPorts.Add(localPort);
    }

    private sealed class RaisingTerminalSession : ITerminalSession
    {
        public event Action<ReadOnlyMemory<byte>>? DataReceived
        {
            add { }
            remove { }
        }

        public event Action<int>? ProcessExited;

        public bool IsRunning { get; private set; } = true;

        public int? ProcessId => IsRunning ? 1234 : null;

        public Dictionary<string, string>? EnvironmentVariables { get; set; }

        public Task StartAsync(
            string executable,
            string arguments,
            int columns = 80,
            int rows = 24,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Write(string text)
        {
        }

        public void Resize(int columns, int rows)
        {
        }

        public void Kill()
        {
            IsRunning = false;
        }

        public void Dispose()
        {
            IsRunning = false;
        }

        public void RaiseProcessExited(int exitCode)
        {
            IsRunning = false;
            ProcessExited?.Invoke(exitCode);
        }
    }

    /// <summary>
    /// Forwards releases to a real <see cref="TunnelManager"/>, but parks the first caller
    /// between its read of the port and the release until the test lets it proceed.
    /// </summary>
    private sealed class GatedTunnelService(TunnelManager tunnelManager) : ITunnelService
    {
        public ManualResetEventSlim ReadDone { get; } = new(false);

        public ManualResetEventSlim Proceed { get; } = new(false);

        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct = default,
            bool useOsAssignedLocalPort = false)
        {
            throw new NotSupportedException();
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort)
            => null;

        public void ReleaseTunnelReference(int localPort)
        {
            ReadDone.Set();
            Assert.True(Proceed.Wait(TimeSpan.FromSeconds(10)), "the test never released the exit handler");
            tunnelManager.ReleaseReference(localPort);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
