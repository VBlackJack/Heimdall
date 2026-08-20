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

using System.ComponentModel;
using System.IO;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Settings;
using Heimdall.App.ViewModels.Shell;
using Heimdall.App.ViewModels.Tunnels;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Terminal;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static ConnectionResult SuccessWithTerminalSession()
    {
        return new ConnectionResult(true, null, new TerminalSessionResult(new FakeTerminalSession()));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }

    // Metadata proves the factory is a dependency; it cannot prove the shell calls it, hands over
    // itself, exposes what came back, and keeps ownership of it.
    [Fact]
    public void MainViewModel_BuildsTunnelsThroughTheFactoryExactlyOnce()
    {
        RecordingTunnelsFactory factory = new();
        using TestHarness harness = TestHarness.Create(tunnelsFactory: factory);

        Assert.Equal(1, factory.CreateCalls);

        // The owner handed over is the very shell under construction.
        Assert.Same(harness.Main, factory.LastOwner);

        // What Create returned is what the shell exposes.
        Assert.Same(factory.LastCreated, harness.Main.Tunnels);

        // Disposal-by-owner is deliberately not asserted here: this harness supplies null! for the
        // settings view model, so MainViewModel.Dispose faults on that unrelated gap before reaching
        // the tunnels view model. Asserting around it would prove the workaround, not the ownership.
    }

    // Metadata proves the coordinator is a dependency of the shell; only running the shell's own
    // load sequence shows that it hands the replay over, once, with its own inventory as the host.
    [Fact]
    public async Task MainViewModel_DelegatesSessionRestoreToTheCoordinatorWithItsOwnInventory()
    {
        RecordingSessionRestoreCoordinator restore = new();
        using TestHarness harness = TestHarness.Create(sessionRestore: restore);

        await harness.Main.LoadCommand.ExecuteAsync(null);

        Assert.Equal(1, restore.RestoreCalls);

        // The host handed over is the shell's live server inventory, not a copy or a stand-in:
        // a replay against anything else would reopen sessions the user cannot see.
        Assert.Same(harness.Main.ServerList, restore.LastHost);

        // And the inventory the host exposes is the live collection, not a copy taken at
        // composition time: a snapshot of it would already be stale by the time the dialog
        // resolves the entries against it.
        Assert.Same(
            harness.Main.ServerList.Servers,
            ((ISessionRestoreHost)harness.Main.ServerList).RestorableServers);
    }

    // The palette moved behind a factory in the same lot that removed the service locator, but only
    // its presence on the constructor was ever pinned. The palette's own behaviour is covered
    // end to end elsewhere through this harness; what was missing is the composition itself, which
    // is the property its tunnels twin above already carries.
    [Fact]
    public void MainViewModel_BuildsTheCommandPaletteThroughTheFactoryExactlyOnce()
    {
        RecordingCommandPaletteFactory factory = new();
        using TestHarness harness = TestHarness.Create(paletteFactory: factory);

        Assert.Equal(1, factory.CreateCalls);

        // The owner handed over is the very shell under construction.
        Assert.Same(harness.Main, factory.LastOwner);

        // What Create returned is what the shell exposes.
        Assert.Same(factory.LastCreated, harness.Main.CommandPalette);
    }

    private sealed class RecordingCommandPaletteFactory : ICommandPaletteViewModelFactory
    {
        private readonly CommandPaletteViewModelFactory _inner = new(
            new LocalizationManager(),
            new FakeDialogService(),
            new ToolRegistry(),
            new InMemoryConfigManager(),
            new FakeEmbeddedSessionManager(),
            new ExternalToolLaunchService(new FakeDialogService()),
            new RecentConnectionTracker(),
            new ServiceCollection().BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>());

        public int CreateCalls { get; private set; }

        public MainViewModel? LastOwner { get; private set; }

        public CommandPaletteViewModel? LastCreated { get; private set; }

        public CommandPaletteViewModel Create(MainViewModel owner)
        {
            CreateCalls++;
            LastOwner = owner;
            LastCreated = _inner.Create(owner);
            return LastCreated;
        }
    }

    private sealed class RecordingTunnelsFactory : ITunnelsViewModelFactory
    {
        private readonly TunnelsViewModelFactory _inner = new(
            new LocalizationManager(),
            new TunnelManager(),
            new ConnectionStateMachine(),
            new HostKeyStore(),
            Heimdall.Core.Ssh.RejectingHostKeyVerifier.Instance,
            new InMemoryConfigManager());

        public int CreateCalls { get; private set; }

        public MainViewModel? LastOwner { get; private set; }

        public TunnelsViewModel? LastCreated { get; private set; }

        public TunnelsViewModel Create(MainViewModel owner)
        {
            CreateCalls++;
            LastOwner = owner;
            LastCreated = _inner.Create(owner);
            return LastCreated;
        }
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public void SetText(string text)
        {
        }
    }

    private sealed class RecordingSessionRestoreCoordinator : ISessionRestoreCoordinator
    {
        public int RestoreCalls { get; private set; }

        public ISessionRestoreHost? LastHost { get; private set; }

        public Task RestoreAsync(
            ISessionRestoreHost host,
            CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            LastHost = host;
            return Task.CompletedTask;
        }
    }

    private sealed class TestHarness : IDisposable
    {
        private readonly string _rootPath;

        private TestHarness(
            string rootPath,
            MainViewModel main,
            FakeUiDispatcher dispatcher,
            FakeDialogService dialogService,
            FakeEmbeddedSessionManager embeddedSessionManager,
            ConnectionStateMachine stateMachine,
            TunnelManager tunnelManager,
            RecordingPaneCloseArbiter closeArbiter,
            IReadOnlyDictionary<string, ControlledProtocolHandler> handlers)
        {
            _rootPath = rootPath;
            Main = main;
            Dispatcher = dispatcher;
            DialogService = dialogService;
            EmbeddedSessionManager = embeddedSessionManager;
            StateMachine = stateMachine;
            TunnelManager = tunnelManager;
            CloseArbiter = closeArbiter;
            Handlers = handlers;
        }

        public MainViewModel Main { get; }

        public FakeUiDispatcher Dispatcher { get; }

        public FakeDialogService DialogService { get; }

        public FakeEmbeddedSessionManager EmbeddedSessionManager { get; }

        public ConnectionStateMachine StateMachine { get; }

        public TunnelManager TunnelManager { get; }

        /// <summary>
        /// The one arbiter shared by SplitService, ConnectionViewModel and MainViewModel, exactly
        /// as production shares it. Three separate instances - which this harness used to build -
        /// let a grant issued on one path go unseen by the next, so the requests observed here
        /// would not be the requests the shell actually negotiates.
        /// </summary>
        internal RecordingPaneCloseArbiter CloseArbiter { get; }

        private IReadOnlyDictionary<string, ControlledProtocolHandler> Handlers { get; }

        public static TestHarness Create(
            bool checkAccess = true,
            ITunnelsViewModelFactory? tunnelsFactory = null,
            ISessionRestoreCoordinator? sessionRestore = null,
            ICommandPaletteViewModelFactory? paletteFactory = null)
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall-session-premount-tests",
                Guid.NewGuid().ToString("N"));
            ConfigManager configManager = new ConfigManager(rootPath);
            configManager.InitializeAsync().GetAwaiter().GetResult();

            LocalizationManager localizer = new LocalizationManager();
            localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en")
                .GetAwaiter()
                .GetResult();

            FakeDialogService dialogService = new FakeDialogService();
            ConnectionStateMachine connectionStateMachine = new ConnectionStateMachine();
            ApplicationStatusMachine appStatus = new ApplicationStatusMachine();
            TunnelManager tunnelManager = new TunnelManager();
            HostKeyStore hostKeyStore = new HostKeyStore();
            FakeEmbeddedSessionManager embeddedSessionManager = new FakeEmbeddedSessionManager();
            ToolRegistry toolRegistry = new ToolRegistry();
            Dictionary<string, ControlledProtocolHandler> handlers = new Dictionary<string, ControlledProtocolHandler>(StringComparer.OrdinalIgnoreCase)
            {
                ["SSH"] = new("SSH"),
                ["RDP"] = new("RDP"),
                ["SFTP"] = new("SFTP"),
                ["WINRM"] = new("WINRM")
            };
            ConnectionService connectionService = new ConnectionService(
                configManager,
                localizer,
                new FakeTunnelService(),
                handlers.Values);
            RecordingPaneCloseArbiter closeArbiter = new();
            SplitService splitService = new SplitService(
                configManager,
                localizer,
                connectionStateMachine,
                tunnelManager,
                embeddedSessionManager,
                connectionService,
                toolRegistry,
                dialogService, closeArbiter);
            FakeUiDispatcher dispatcher = new(checkAccess);
            ConnectionViewModel connection = new ConnectionViewModel(
                localizer,
                dialogService,
                splitService,
                closeArbiter,
                new SessionWindowService(static (_, _) => { }));
            ServerListViewModel serverList = new ServerListViewModel(
                configManager,
                localizer,
                dispatcher,
                connectionStateMachine,
                connectionService,
                dialogService,
                new FakeRdpImportService(),
                new PuttySessionImporter(new FakePuttySessionRegistrySource(), configManager),
                new Heimdall.App.Services.Import.KnownHostsImporter(configManager, hostKeyStore));
            // A real trusted-host-keys view model rather than null!: the shell's own load
            // sequence runs LoadFromSettings, which refreshes it, so the null stood between this
            // harness and every assertion about what loading the shell does.
            TrustedHostKeysSettingsViewModel trustedHostKeys = new(
                new HostKeyTrustService(new HostKeyStore()),
                () => new KnownHostsImportReport(0, 0, []),
                () => new KnownHostsExportReport(0, 0, 0),
                localizer,
                dialogService,
                new FakeClipboardService(),
                dispatcher);
            SettingsViewModel settings = new SettingsViewModel(
                configManager,
                localizer,
                dialogService,
                trustedHostKeys,
                new PinManager(),
                new Heimdall.Core.Security.Vault.VaultLifecycleService(configManager),
                null!,
                null!,
                null!,
                null!);
            MainViewModel main = new MainViewModel(
                configManager,
                localizer,
                appStatus,
                hostKeyStore,
                dialogService,
                embeddedSessionManager,
                new HeimdallThemeService(configManager),
                sessionRestore ?? new RecordingSessionRestoreCoordinator(),
                new FakePostConnectSequenceRunner(),
                new FakePostConnectStepResolver(),
                toolRegistry,
                splitService,
                new ToolsTabPopulationService(toolRegistry),
                new FakeToolContextProvider(),
                dispatcher,
                new Heimdall.App.Services.WorkspaceLockService(
                    new Heimdall.Core.Security.Vault.VaultLifecycleService(configManager)),
                serverList,
                connection,
                settings,
                null!,
                closeArbiter,
                paletteFactory ?? new CommandPaletteViewModelFactory(
                    localizer,
                    dialogService,
                    toolRegistry,
                    configManager,
                    embeddedSessionManager,
                    new ExternalToolLaunchService(dialogService),
                    new RecentConnectionTracker(),
                    new ServiceCollection().BuildServiceProvider()
                        .GetRequiredService<IServiceScopeFactory>()),
                tunnelsFactory ?? new TunnelsViewModelFactory(
                    localizer,
                    tunnelManager,
                    connectionStateMachine,
                    hostKeyStore,
                    Heimdall.Core.Ssh.RejectingHostKeyVerifier.Instance,
                    configManager));

            return new TestHarness(
                rootPath,
                main,
                dispatcher,
                dialogService,
                embeddedSessionManager,
                connectionStateMachine,
                tunnelManager,
                closeArbiter,
                handlers);
        }

        public ControlledProtocolHandler GetHandler(string protocol) => Handlers[protocol];

        public ServerProfileDto CreateServer(string protocol)
        {
            return new ServerProfileDto
            {
                Id = $"server-{protocol.ToLowerInvariant()}",
                DisplayName = $"Demo {protocol}",
                RemoteServer = "demo.example.com",
                RemotePort = 3389,
                ConnectionType = protocol,
                UseDirectConnection = true,
                SshUsername = "admin",
                SshPort = 22,
                SshMode = "Embedded",
                RdpMode = "Embedded"
            };
        }

        /// <summary>
        /// Saves the server to the inventory and refreshes the ServerList VM so
        /// reconnect lookups via <c>_main.ServerList.Servers.FirstOrDefault</c>
        /// can find the server by id (the lookup is the same path used by the
        /// production reconnect flow).
        /// </summary>
        public async Task PersistServerAsync(ServerProfileDto server)
        {
            List<ServerProfileDto> inventory = await Main.ConfigManager.LoadServersAsync();
            List<ServerProfileDto> list = inventory.ToList();
            list.RemoveAll(s => string.Equals(s.Id, server.Id, StringComparison.Ordinal));
            list.Add(server);
            await Main.ConfigManager.SaveServersAsync(list);

            AppSettings settings = await Main.ConfigManager.LoadSettingsAsync();
            Main.ServerList.LoadServers(list, settings);
        }

        public void ResetHandler(string protocol)
        {
            Handlers[protocol].Reset();
        }

        public Task<BulkConnectOutcome> RunPipelineAsync(
            ServerProfileDto server,
            string sessionId,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
        {
            ServerItemViewModel item = ServerItemViewModel.FromDto(server, localizer: Main.GetLocalizer());
            return Main.ServerList.RunConnectionPipelineAsync(
                server,
                new AppSettings { SftpAutoOpenOnSsh = false },
                sessionId,
                server.Id,
                item,
                CancellationToken.None,
                rdpModeOverride);
        }

        public void Dispose()
        {
            Main.Session.Dispose();
            TunnelManager.Dispose();

            try
            {
                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not mask assertion failures.
            }
        }
    }

    private sealed class ControlledProtocolHandler(string protocol) : IProtocolHandler
    {
        public string Protocol { get; } = protocol;

        public TaskCompletionSource<CancellationToken> Started { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ConnectionResult> Result { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Replaces the started/result task completion sources so the next
        /// <see cref="ConnectAsync"/> call starts fresh. Used by reconnect
        /// tests that need to observe a second connection attempt after the
        /// first has already completed.
        /// </summary>
        public void Reset()
        {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// The profile instance the last connection attempt was handed, kept by reference so tests
        /// can observe what the coordinator actually built rather than a copy of it.
        /// </summary>
        public ServerProfileDto? LastServer { get; private set; }

        public Task<ConnectionResult> ConnectAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
        {
            LastServer = server;
            Started.TrySetResult(ct);
            return Result.Task.WaitAsync(ct);
        }
    }

    private sealed class FakeEmbeddedSessionManager : IEmbeddedSessionManager
    {
        public Action<byte[], object?>? BroadcastCallback { get; set; }
        public Action<SessionTabViewModel>? SplitRequestedCallback { get; set; }
        public Func<bool>? IsBroadcastActive { get; set; }
        public Action<SessionTabViewModel, string, string>? ReconnectRequestedCallback { get; set; }
        public Action<SessionTabViewModel, SessionPaneModel>? ReconnectPaneRequestedCallback { get; set; }
        public Action<SessionTabViewModel, SessionPaneModel, DisconnectReason>? DisconnectRequestedCallback { get; set; }
        public Action<string>? EditServerRequestedCallback { get; set; }
        public Action<SessionTabViewModel>? CloseRequestedCallback { get; set; }
        public Func<string, string, ToolContext?, Task>? OpenToolCallback { get; set; }

        public int CreateHostControlCalls { get; private set; }
        public int CreateConnectingSshHostControlCalls { get; private set; }
        public int AttachSshSessionCalls { get; private set; }
        public Func<SessionTabViewModel, string, object>? CreateHostControlBehavior { get; set; }
        public Func<SessionTabViewModel, string, ToolContext?, AppSettings?, object>? CreateToolControlBehavior { get; set; }
        public int CreateToolControlCalls { get; private set; }

        public object CreateHostControl(
            SessionTabViewModel sessionTab,
            string displayName,
            string connectionType,
            ISessionResult session,
            AppSettings? settings = null,
            string? initialRemotePath = null)
        {
            CreateHostControlCalls++;
            return CreateHostControlBehavior?.Invoke(sessionTab, connectionType) ?? new object();
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
            CreateConnectingSshHostControlCalls++;
            return null!;
        }

        public void AttachSshSession(
            SessionTabViewModel sessionTab,
            ISessionResult sessionResult,
            AppSettings? settings = null)
        {
            AttachSshSessionCalls++;
        }

        public object CreateToolControl(
            SessionTabViewModel sessionTab,
            string toolId,
            ToolContext? context,
            AppSettings? settings = null)
        {
            CreateToolControlCalls++;
            return CreateToolControlBehavior?.Invoke(sessionTab, toolId, context, settings) ?? new object();
        }

        public bool TrySendCommandToSession(SessionTabViewModel session, string command) => false;
    }

    private sealed class FakeTerminalSession : ITerminalSession
    {
        public event Action<ReadOnlyMemory<byte>>? DataReceived;
        public event Action<int>? ProcessExited;

        public bool IsRunning => true;
        public int? ProcessId => 1234;
        public Dictionary<string, string>? EnvironmentVariables { get; set; }
        public int DisposeCount { get; private set; }

        public Task StartAsync(
            string executable,
            string arguments,
            int columns = 80,
            int rows = 24,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            DataReceived?.Invoke(data.ToArray());
        }

        public void Write(string text)
        {
        }

        public void Resize(int columns, int rows)
        {
        }

        public void Kill()
        {
            ProcessExited?.Invoke(0);
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class FakeTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome>
            SetupTunnelIfNeededAsync(
                ServerProfileDto server,
                int remotePort,
                AppSettings settings,
                CancellationToken ct,
                bool preferDistinctLoopback = false)
        {
            return Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class FakeRdpImportService : IRdpImportService
    {
        public Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct)
        {
            return Task.FromResult(new RdpImportPreview
            {
                Entries = [],
                FilesNotFound = [],
                FilesUnreadable = []
            });
        }

        public Task<RdpImportResult> ApplyAsync(
            RdpImportPreview preview,
            RdpImportSelection selection,
            CancellationToken ct)
        {
            return Task.FromResult(new RdpImportResult());
        }
    }

    private sealed class FakePuttySessionRegistrySource : IPuttySessionRegistrySource
    {
        public Task<IReadOnlyList<RawPuttySession>> ReadSessionsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<RawPuttySession>>([]);
        }
    }

    private sealed class FakePostConnectSequenceRunner : IPostConnectSequenceRunner
    {
        public Task<PostConnectRunResult> RunAsync(
            IReadOnlyList<PostConnectStep> steps,
            Action<string> writeCallback,
            IProgress<PostConnectRunProgress>? progress,
            CancellationToken ct,
            IPostConnectStepResolver? resolver = null)
        {
            return Task.FromResult(new PostConnectRunResult());
        }
    }

    private sealed class FakePostConnectStepResolver : IPostConnectStepResolver
    {
        public Task<PostConnectResolveResult> ResolveAsync(PostConnectStep step, CancellationToken ct)
        {
            return Task.FromResult(new PostConnectResolveResult
            {
                Status = PostConnectResolveStatus.Literal,
                ResolvedInput = step.Input
            });
        }
    }

    private sealed class FakeToolContextProvider : IToolContextProvider
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string? TargetHost => null;
        public bool HasTarget => false;
        public string ContextLabel => string.Empty;
        public string ContextTooltip => string.Empty;
        public string ContextBrushKey => "TextSecondaryBrush";

        public void SetSelectedServer(ServerItemViewModel? server)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetHost)));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public int ErrorCallCount { get; private set; }

        public bool ConfirmResult { get; set; } = true;

        public int ConfirmCallCount { get; private set; }

        public string? LastConfirmTitle { get; private set; }

        public string? LastConfirmMessage { get; private set; }

        public string? LastConfirmSeverity { get; private set; }

        public Action? ConfirmInvoked { get; set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            ConfirmCallCount++;
            LastConfirmTitle = title;
            LastConfirmMessage = message;
            LastConfirmSeverity = severity;
            ConfirmInvoked?.Invoke();
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
        {
            return Task.FromResult<bool?>(false);
        }

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
        {
            return Task.FromResult(defaultValue);
        }

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
        {
            return Task.FromResult<ServerDialogResult?>(null);
        }

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
        {
            return Task.FromResult<GatewayDialogResult?>(null);
        }

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
        {
            return Task.FromResult<ProjectDialogResult?>(null);
        }

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(
            ScheduledTaskDialogViewModel? editVm = null)
        {
            return Task.FromResult<ScheduledTaskDialogResult?>(null);
        }

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
        {
            return Task.FromResult<PinSetupResult?>(null);
        }

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(
            SnapshotRestoreDialogViewModel viewModel)
        {
            return Task.FromResult<SnapshotRestoreDialogResult?>(null);
        }

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
        {
            return Task.FromResult<RdpImportSelection?>(null);
        }

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
        {
            return Task.FromResult<ImportOutcome?>(null);
        }

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
        {
            return Task.FromResult<ImportOutcome?>(null);
        }

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
        {
            return Task.FromResult<KnownHostsImportOutcome?>(null);
        }

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
        {
            return Task.FromResult<ImportKnownHostsConflictResolution?>(null);
        }

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
        {
            return Task.FromResult<CommandLibraryPickerResult?>(null);
        }

        public Task<int?> ShowBulkEditPortAsync(
            int count,
            int? initialPort,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(initialPort);
        }

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(initialUsername);
        }

        public Task<string?> ShowBulkEditPasswordAsync(
            int count,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public void ShowError(string title, string message)
        {
            ErrorCallCount++;
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }
    }

    /// <summary>
    /// Records every request handed to the arbiter, and decides nothing itself.
    /// </summary>
    /// <remarks>
    /// A decorator over the real <see cref="PaneCloseArbiter"/>, not a replacement for it. A
    /// recorder that answered allow on its own would let these tests observe requests flowing
    /// through a decision-maker the application does not use, so the guard protocol under
    /// observation would not be the one that ships.
    /// <para>
    /// It records the poll only: that is the one observation the close-intent oracles read, and a
    /// list nothing asserts on is a list that will drift. Counting Silent against Interactive
    /// would be useless anyway - swapping two producers' intents leaves every total unchanged,
    /// which is exactly how a user gesture reached the silent path in the first place.
    /// </para>
    /// </remarks>
    private sealed class RecordingPaneCloseArbiter : IPaneCloseArbiter
    {
        private readonly PaneCloseArbiter _inner = new();
        private readonly List<CloseRequest> _polled = [];

        /// <summary>Requests handed to <see cref="Poll"/>, in order.</summary>
        public IReadOnlyList<CloseRequest> Polled => _polled;

        /// <summary>Drops the observations, so one harness can drive several gestures.</summary>
        public void Reset() => _polled.Clear();

        public CloseDecision Poll(CloseRequest request, IReadOnlyList<object?> hosts)
        {
            _polled.Add(request);
            return _inner.Poll(request, hosts);
        }

        public Task<bool> ResolveAsync(CloseRequest request, IReadOnlyList<object?> hosts)
            => _inner.ResolveAsync(request, hosts);

        public void Release(CloseRequest request) => _inner.Release(request);
    }
}
