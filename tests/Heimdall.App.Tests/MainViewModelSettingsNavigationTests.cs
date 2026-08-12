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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Session;
using Heimdall.App.ViewModels.Settings;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Core.Updates;
using Heimdall.Sftp;
using Heimdall.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Tests;

/// <summary>
/// Verifies that MainViewModel keeps Settings selected until its save outcome is known.
/// </summary>
public sealed class MainViewModelSettingsNavigationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ensures the target tab is applied only after a delayed settings save succeeds.
    /// </summary>
    [Fact]
    public async Task SelectedTab_SavePending_KeepsSettingsUntilSaveSucceedsThenNavigatesOnce()
    {
        using TestHarness harness = await TestHarness.CreateAsync(MergeBehavior.DelayedSuccess);
        harness.Main.SelectedTab = "Settings";
        harness.Main.Settings.IsDirty = true;
        int aboutNavigationCount = 0;
        TaskCompletionSource<bool> aboutNavigated = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Main.PropertyChanged += (_, args) =>
        {
            if (string.Equals(
                    args.PropertyName,
                    nameof(MainViewModel.IsAboutTabSelected),
                    StringComparison.Ordinal)
                && string.Equals(harness.Main.SelectedTab, "About", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref aboutNavigationCount);
                aboutNavigated.TrySetResult(true);
            }
        };

        harness.Main.SelectedTab = "About";

        await harness.Config.MergeStarted.Task.WaitAsync(TestTimeout);
        Assert.Equal("Settings", harness.Main.SelectedTab);
        Assert.Equal(0, Volatile.Read(ref aboutNavigationCount));

        harness.Config.ReleaseMerge();
        await aboutNavigated.Task.WaitAsync(TestTimeout);

        Assert.Equal("About", harness.Main.SelectedTab);
        Assert.Equal(1, Volatile.Read(ref aboutNavigationCount));
        Assert.Equal(1, harness.Dialog.SavePromptCount);
    }

    /// <summary>
    /// Ensures settings validation failure prevents the requested tab change.
    /// </summary>
    [Fact]
    public async Task SelectedTab_SaveReturnsFalse_LeavesSettingsSelected()
    {
        using TestHarness harness = await TestHarness.CreateAsync(MergeBehavior.ImmediateSuccess);
        harness.Main.SelectedTab = "Settings";
        harness.Main.Settings.MaxEmbeddedSessions = 0;
        int aboutNavigationCount = 0;
        harness.Main.PropertyChanged += (_, args) =>
        {
            if (string.Equals(
                    args.PropertyName,
                    nameof(MainViewModel.IsAboutTabSelected),
                    StringComparison.Ordinal)
                && string.Equals(harness.Main.SelectedTab, "About", StringComparison.Ordinal))
            {
                aboutNavigationCount++;
            }
        };

        harness.Main.SelectedTab = "About";

        Assert.Equal("Settings", harness.Main.SelectedTab);
        Assert.Equal(0, aboutNavigationCount);
        Assert.False(harness.Config.MergeStarted.Task.IsCompleted);
        Assert.Equal(1, harness.Dialog.SavePromptCount);
    }

    /// <summary>
    /// Ensures the existing settings save exception path prevents the requested tab change.
    /// </summary>
    [Fact]
    public async Task SelectedTab_SaveThrows_LeavesSettingsSelected()
    {
        using TestHarness harness = await TestHarness.CreateAsync(MergeBehavior.Throw);
        harness.Main.SelectedTab = "Settings";
        harness.Main.Settings.IsDirty = true;
        int aboutNavigationCount = 0;
        harness.Main.PropertyChanged += (_, args) =>
        {
            if (string.Equals(
                    args.PropertyName,
                    nameof(MainViewModel.IsAboutTabSelected),
                    StringComparison.Ordinal)
                && string.Equals(harness.Main.SelectedTab, "About", StringComparison.Ordinal))
            {
                aboutNavigationCount++;
            }
        };

        harness.Main.SelectedTab = "About";

        Assert.True(harness.Config.MergeStarted.Task.IsCompletedSuccessfully);
        Assert.Equal("Settings", harness.Main.SelectedTab);
        Assert.Equal(0, aboutNavigationCount);
        Assert.Equal(1, harness.Dialog.SavePromptCount);
    }

    /// <summary>
    /// Ensures saving a new locale reprojects a status owned by the application state machine.
    /// </summary>
    [Fact]
    public async Task SelectedTab_LocaleSave_ReprojectsLocalizedApplicationStatusExactlyOnce()
    {
        using TestHarness harness = await TestHarness.CreateAsync(MergeBehavior.ImmediateSuccess);
        string englishStatus = harness.Main.StatusText;
        int statusNotificationCount = 0;
        harness.Main.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(MainViewModel.StatusText), StringComparison.Ordinal))
            {
                statusNotificationCount++;
            }
        };

        await SaveFrenchLocaleAndNavigateAsync(harness);

        Assert.NotEqual(englishStatus, harness.Main.StatusText);
        Assert.Equal(harness.Localizer["StatusReady"], harness.Main.StatusText);
        Assert.Equal(1, statusNotificationCount);
    }

    /// <summary>
    /// Ensures the computed split drop label is invalidated exactly once after a locale save.
    /// </summary>
    [Fact]
    public async Task SelectedTab_LocaleSave_NotifiesDropToMergeTextExactlyOnce()
    {
        using TestHarness harness = await TestHarness.CreateAsync(MergeBehavior.ImmediateSuccess);
        int dropTextNotificationCount = 0;
        harness.Main.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(MainViewModel.DropToMergeText), StringComparison.Ordinal))
            {
                dropTextNotificationCount++;
            }
        };

        await SaveFrenchLocaleAndNavigateAsync(harness);

        Assert.Equal(harness.Localizer["SplitDropToMerge"], harness.Main.DropToMergeText);
        Assert.Equal(1, dropTextNotificationCount);
    }

    /// <summary>
    /// Ensures a transient or raw status is not replaced by an application-state label.
    /// </summary>
    [Fact]
    public async Task SelectedTab_LocaleSave_PreservesTransientStatus()
    {
        using TestHarness harness = await TestHarness.CreateAsync(MergeBehavior.ImmediateSuccess);
        harness.Main.StatusText = "gateway refused";
        int statusNotificationCount = 0;
        harness.Main.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(MainViewModel.StatusText), StringComparison.Ordinal))
            {
                statusNotificationCount++;
            }
        };

        await SaveFrenchLocaleAndNavigateAsync(harness);

        Assert.Equal("gateway refused", harness.Main.StatusText);
        Assert.Equal(0, statusNotificationCount);
    }

    /// <summary>
    /// Ensures locale changes stop affecting the shell after the root view model is disposed.
    /// </summary>
    [Fact]
    public async Task LocaleChanged_AfterDispose_DoesNotRefreshShellState()
    {
        using TestHarness harness = await TestHarness.CreateAsync(MergeBehavior.ImmediateSuccess);
        string statusBeforeDispose = harness.Main.StatusText;
        int dropTextNotificationCount = 0;
        harness.Main.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(MainViewModel.DropToMergeText), StringComparison.Ordinal))
            {
                dropTextNotificationCount++;
            }
        };
        harness.Main.Dispose();

        await harness.Localizer.SwitchLocaleAsync("fr");

        Assert.Equal(statusBeforeDispose, harness.Main.StatusText);
        Assert.Equal(0, dropTextNotificationCount);
    }

    /// <summary>
    /// Ensures an off-thread locale change queues shell reprojection through the UI dispatcher.
    /// </summary>
    [Fact]
    public async Task SelectedTab_LocaleSave_OffUiThreadQueuesShellRefresh()
    {
        using TestHarness harness = await TestHarness.CreateAsync(
            MergeBehavior.ImmediateSuccess,
            checkAccess: false);
        List<Action> queuedActions = [];
        harness.Dispatcher.InvokeAsyncActionHandler = queuedActions.Add;
        string englishStatus = harness.Main.StatusText;

        await SaveFrenchLocaleAndNavigateAsync(harness);

        Assert.Equal(englishStatus, harness.Main.StatusText);
        Assert.NotEmpty(queuedActions);

        foreach (Action queuedAction in queuedActions.ToList())
        {
            queuedAction();
        }

        Assert.Equal(harness.Localizer["StatusReady"], harness.Main.StatusText);
    }

    private static async Task SaveFrenchLocaleAndNavigateAsync(TestHarness harness)
    {
        TaskCompletionSource<bool> aboutNavigated = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Main.PropertyChanged += (_, args) =>
        {
            if (string.Equals(
                    args.PropertyName,
                    nameof(MainViewModel.IsAboutTabSelected),
                    StringComparison.Ordinal)
                && string.Equals(harness.Main.SelectedTab, "About", StringComparison.Ordinal))
            {
                aboutNavigated.TrySetResult(true);
            }
        };
        harness.Main.SelectedTab = "Settings";
        harness.Main.Settings.DefaultLocale = "fr";
        harness.Main.Settings.IsDirty = true;

        harness.Main.SelectedTab = "About";

        await aboutNavigated.Task.WaitAsync(TestTimeout);
        Assert.Equal("fr", harness.Localizer.CurrentLocale);
        Assert.Equal("About", harness.Main.SelectedTab);
    }

    private enum MergeBehavior
    {
        ImmediateSuccess,
        DelayedSuccess,
        Throw
    }

    private sealed class TestHarness : IDisposable
    {
        private readonly string _rootPath;
        private readonly ServiceProvider _serviceProvider;

        private TestHarness(
            string rootPath,
            MainViewModel main,
            ControlledConfigManager config,
            RecordingDialogService dialog,
            LocalizationManager localizer,
            FakeUiDispatcher dispatcher,
            ServiceProvider serviceProvider)
        {
            _rootPath = rootPath;
            Main = main;
            Config = config;
            Dialog = dialog;
            Localizer = localizer;
            Dispatcher = dispatcher;
            _serviceProvider = serviceProvider;
        }

        public MainViewModel Main { get; }

        public ControlledConfigManager Config { get; }

        public RecordingDialogService Dialog { get; }

        public LocalizationManager Localizer { get; }

        public FakeUiDispatcher Dispatcher { get; }

        public static async Task<TestHarness> CreateAsync(
            MergeBehavior mergeBehavior,
            bool checkAccess = true)
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                nameof(MainViewModelSettingsNavigationTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            ControlledConfigManager config = new(rootPath, mergeBehavior);
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            RecordingDialogService dialog = new();
            FakeUiDispatcher dispatcher = new(checkAccess);
            HostKeyStore hostKeyStore = new();
            HostKeyTrustService hostKeyTrustService = new(hostKeyStore);
            Heimdall.App.Services.Import.KnownHostsImporter appKnownHostsImporter =
                new(config, hostKeyTrustService);
            Heimdall.Ssh.KnownHostsImporter settingsKnownHostsImporter =
                new(hostKeyTrustService);
            KnownHostsExporter knownHostsExporter = new(
                hostKeyTrustService,
                static (_, _) => { });
            TrustedHostKeysSettingsViewModel trustedHostKeys = new(
                hostKeyTrustService,
                settingsKnownHostsImporter,
                knownHostsExporter,
                localizer,
                dialog,
                new NullClipboardService(),
                dispatcher);
            StubUpdateService updateService = new();
            StubAppVersionProvider appVersionProvider = new();
            StubUpdateInstallFlow updateInstallFlow = new();
            StubBrowserLauncher browserLauncher = new();
            VaultLifecycleService vaultLifecycle = new(config);
            SettingsViewModel settings = new(
                config,
                localizer,
                dialog,
                trustedHostKeys,
                new PinManager(),
                vaultLifecycle,
                updateService,
                appVersionProvider,
                updateInstallFlow,
                browserLauncher);
            ConnectionStateMachine connectionStateMachine = new();
            ApplicationStatusMachine appStatus = new();
            TunnelManager tunnelManager = new();
            StubEmbeddedSessionManager embeddedSessionManager = new();
            ToolRegistry toolRegistry = new();
            IProtocolHandler[] handlers = [];
            ConnectionService connectionService = new(
                config,
                localizer,
                new StubTunnelService(),
                handlers);
            SplitService splitService = new(
                config,
                localizer,
                connectionStateMachine,
                tunnelManager,
                embeddedSessionManager,
                connectionService,
                toolRegistry,
                dialog);
            ConnectionViewModel connection = new(localizer, dialog, splitService);
            ServerListViewModel serverList = new(
                config,
                localizer,
                dispatcher,
                connectionStateMachine,
                connectionService,
                dialog,
                new StubRdpImportService(),
                new PuttySessionImporter(
                    new FakePuttySessionRegistrySource([]),
                    config),
                appKnownHostsImporter);
            ServiceProvider serviceProvider = new ServiceCollection().BuildServiceProvider();
            UpdateBannerViewModel update = new(
                updateService,
                config,
                appVersionProvider,
                browserLauncher,
                updateInstallFlow,
                dialog,
                localizer);
            MainViewModel main = new(
                config,
                localizer,
                connectionStateMachine,
                appStatus,
                tunnelManager,
                hostKeyStore,
                RejectingHostKeyVerifier.Instance,
                dialog,
                embeddedSessionManager,
                new HeimdallThemeService(config),
                new StubSessionSnapshotService(rootPath),
                new StubPostConnectSequenceRunner(),
                new StubPostConnectStepResolver(),
                toolRegistry,
                splitService,
                new ExternalToolLaunchService(dialog),
                new ToolsTabPopulationService(toolRegistry),
                new StubToolContextProvider(),
                dispatcher,
                new WorkspaceLockService(vaultLifecycle),
                serverList,
                connection,
                settings,
                update,
                serviceProvider);

            return new TestHarness(
                rootPath,
                main,
                config,
                dialog,
                localizer,
                dispatcher,
                serviceProvider);
        }

        public void Dispose()
        {
            Main.Dispose();
            _serviceProvider.Dispose();
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private sealed class ControlledConfigManager : IConfigManager
    {
        private readonly MergeBehavior _mergeBehavior;
        private readonly AppSettings _settings = new();
        private readonly List<ServerProfileDto> _servers = [];
        private readonly TaskCompletionSource<bool> _mergeGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledConfigManager(string rootPath, MergeBehavior mergeBehavior)
        {
            _mergeBehavior = mergeBehavior;
            ConfigPath = Path.Combine(rootPath, "config");
            SettingsPath = Path.Combine(ConfigPath, "settings.json");
            ServersPath = Path.Combine(ConfigPath, "servers.json");
            Directory.CreateDirectory(ConfigPath);
        }

        public string ConfigPath { get; }

        public string SettingsPath { get; }

        public string ServersPath { get; }

        public event Action<AppSettings>? SettingsChanged;

        public TaskCompletionSource<bool> MergeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(_settings);

        public Task SaveSettingsAsync(AppSettings settings)
        {
            SettingsChanged?.Invoke(settings);
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(
            IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public async Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            MergeStarted.TrySetResult(true);
            if (_mergeBehavior == MergeBehavior.DelayedSuccess)
            {
                await _mergeGate.Task;
            }

            if (_mergeBehavior == MergeBehavior.Throw)
            {
                throw new InvalidOperationException("Controlled settings save failure.");
            }

            mutate(_settings);
            SettingsChanged?.Invoke(_settings);
        }

        public Task<List<ServerProfileDto>> LoadServersAsync() =>
            Task.FromResult(_servers.ToList());

        public Task<TResult> MutateServersAsync<TResult>(
            Func<List<ServerProfileDto>, TResult> mutate)
        {
            TResult result = mutate(_servers);
            return Task.FromResult(result);
        }

        public Task SaveServersAsync(List<ServerProfileDto> servers)
        {
            _servers.Clear();
            _servers.AddRange(servers);
            return Task.CompletedTask;
        }

        public void ReleaseMerge() => _mergeGate.TrySetResult(true);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public int SavePromptCount { get; private set; }

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string severity = "info") =>
            Task.FromResult(true);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
        {
            SavePromptCount++;
            return Task.FromResult<bool?>(true);
        }

        public Task<string?> ShowInputAsync(
            string title,
            string prompt,
            string? defaultValue = null) =>
            Task.FromResult(defaultValue);

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(
            ServerDialogViewModel? editVm = null) =>
            Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(
            GatewayDialogViewModel? editVm = null) =>
            Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(
            ProjectDialogViewModel? editVm = null) =>
            Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(
            ScheduledTaskDialogViewModel? editVm = null) =>
            Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) =>
            Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(
            SnapshotRestoreDialogViewModel viewModel) =>
            Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(
            RdpImportDialogViewModel viewModel) =>
            Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(
            OpenSshParseResult parseResult) =>
            Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(
            PuttySessionParseResult parseResult) =>
            Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(
            KnownHostsImportPreview preview) =>
            Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(
            TrustedHostKeyDetailsDialogViewModel viewModel) =>
            Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel) =>
            Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null) =>
            Task.FromResult<CommandLibraryPickerResult?>(null);

        public Task<int?> ShowBulkEditPortAsync(
            int count,
            int? initialPort,
            CancellationToken cancellationToken) =>
            Task.FromResult(initialPort);

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken) =>
            Task.FromResult(initialUsername);

        public Task<string?> ShowBulkEditPasswordAsync(
            int count,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public void ShowError(string title, string message)
        {
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }
    }

    private sealed class StubEmbeddedSessionManager : IEmbeddedSessionManager
    {
        public Action<byte[], object?>? BroadcastCallback { get; set; }
        public Action<SessionTabViewModel>? SplitRequestedCallback { get; set; }
        public Func<bool>? IsBroadcastActive { get; set; }
        public Action<SessionTabViewModel, string, string>? ReconnectRequestedCallback { get; set; }
        public Action<SessionTabViewModel, SessionPaneModel>? ReconnectPaneRequestedCallback { get; set; }
        public Action<SessionTabViewModel, SessionPaneModel, DisconnectReason>? DisconnectRequestedCallback { get; set; }
        public Action<SessionTabViewModel>? CloseRequestedCallback { get; set; }
        public Action<string>? EditServerRequestedCallback { get; set; }
        public Func<string, string, ToolContext?, Task>? OpenToolCallback { get; set; }

        public object CreateHostControl(
            SessionTabViewModel sessionTab,
            string displayName,
            string connectionType,
            ISessionResult session,
            AppSettings? settings = null,
            string? initialRemotePath = null) =>
            throw new NotSupportedException();

        public void DisconnectSession(SessionPaneModel pane, DisconnectReason reason)
        {
        }

        public EmbeddedSshView CreateConnectingSshHostControl(
            SessionTabViewModel sessionTab,
            string displayName,
            ServerProfileDto server,
            AppSettings? settings = null) =>
            throw new NotSupportedException();

        public void AttachSshSession(
            SessionTabViewModel sessionTab,
            ISessionResult sessionResult,
            AppSettings? settings = null)
        {
        }

        public object CreateToolControl(
            SessionTabViewModel sessionTab,
            string toolId,
            ToolContext? context,
            AppSettings? settings = null) =>
            throw new NotSupportedException();

        public bool TrySendCommandToSession(SessionTabViewModel session, string command) => false;
    }

    private sealed class StubTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome>
            SetupTunnelIfNeededAsync(
                ServerProfileDto server,
                int remotePort,
                AppSettings settings,
                CancellationToken ct,
                bool preferDistinctLoopback = false) =>
            Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class StubRdpImportService : IRdpImportService
    {
        public Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<RdpImportResult> ApplyAsync(
            RdpImportPreview preview,
            RdpImportSelection selection,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubPostConnectSequenceRunner : IPostConnectSequenceRunner
    {
        public Task<PostConnectRunResult> RunAsync(
            IReadOnlyList<PostConnectStep> steps,
            Action<string> writeCallback,
            IProgress<PostConnectRunProgress>? progress,
            CancellationToken ct,
            IPostConnectStepResolver? resolver = null) =>
            throw new NotSupportedException();
    }

    private sealed class StubPostConnectStepResolver : IPostConnectStepResolver
    {
        public Task<PostConnectResolveResult> ResolveAsync(
            PostConnectStep step,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubSessionSnapshotService(string rootPath) : ISessionSnapshotService
    {
        public string SnapshotPath { get; } = Path.Combine(rootPath, "snapshot.json");

        public Task SaveAsync(
            IReadOnlyList<SessionSnapshotEntry> sessions,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SessionSnapshotFile?> LoadAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionSnapshotFile?>(null);

        public Task ClearAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubToolContextProvider : IToolContextProvider
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string? TargetHost => null;

        public bool HasTarget => false;

        public string ContextLabel => string.Empty;

        public string ContextTooltip => string.Empty;

        public string ContextBrushKey => string.Empty;

        public void SetSelectedServer(ServerItemViewModel? server)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetHost)));
        }

        public void Dispose()
        {
        }
    }

    private sealed class NullClipboardService : IClipboardService
    {
        public void SetText(string text)
        {
        }
    }

    private sealed class StubUpdateService : IUpdateService
    {
        public Task<UpdateCheckResult> CheckForUpdatesAsync(
            HeimdallVersion current,
            string owner,
            string repo,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IVerifiedUpdatePackage> DownloadVerifiedAsync(
            UpdateInfo update,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubAppVersionProvider : IAppVersionProvider
    {
        public string InformationalVersion => string.Empty;

        public HeimdallVersion? Current => null;

        public DateOnly? BuildDate => null;
    }

    private sealed class StubUpdateInstallFlow : IUpdateInstallFlow
    {
        public Task<UpdateInstallOutcome> RunAsync(
            UpdateInfo update,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubBrowserLauncher : IBrowserLauncher
    {
        public void Open(string url)
        {
        }
    }
}
