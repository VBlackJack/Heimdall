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
using FluentAssertions;
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
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Core.Updates;
using Heimdall.Ssh;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

public sealed class CommandPaletteDangerousSnippetGuardTests
{
    [Fact]
    public async Task SendSnippetExampleCommand_DangerousDeclined_DoesNotSend()
    {
        using var harness = await PaletteHarness.CreateAsync(confirmResult: false);
        var (action, example) = CreateDangerousSnippet();
        harness.Main.Connection.ActiveSession = new SessionTabViewModel { Title = "SSH session" };
        harness.Main.CommandPalette.OpenSnippetDetail(action);

        await harness.Main.CommandPalette.SendSnippetExampleCommand.ExecuteAsync(example);

        harness.Dialog.ConfirmCallCount.Should().Be(1);
        harness.EmbeddedSessionManager.SendCallCount.Should().Be(0);
        harness.Order.Should().Equal("confirm");
    }

    [Fact]
    public async Task SendSnippetExampleCommand_DangerousConfirmed_ConfirmsBeforeSending()
    {
        using var harness = await PaletteHarness.CreateAsync(confirmResult: true);
        var (action, example) = CreateDangerousSnippet();
        harness.Main.Connection.ActiveSession = new SessionTabViewModel { Title = "SSH session" };
        harness.Main.CommandPalette.OpenSnippetDetail(action);

        await harness.Main.CommandPalette.SendSnippetExampleCommand.ExecuteAsync(example);

        harness.Dialog.ConfirmCallCount.Should().Be(1);
        harness.EmbeddedSessionManager.SendCallCount.Should().Be(1);
        harness.EmbeddedSessionManager.LastCommand.Should().Be(example.Command);
        harness.Order.Should().Equal("confirm", "send");
    }

    private static (ActionModel Action, CommandExample Example) CreateDangerousSnippet()
    {
        var example = new CommandExample
        {
            Command = "Remove-Item C:\\Temp\\demo -Recurse -Force",
            Description = "Delete a directory tree",
            Platform = Platform.Windows
        };

        var action = new ActionModel
        {
            Id = "dangerous-delete-demo",
            Title = "Delete demo directory",
            Category = "Filesystem",
            Platform = Platform.Windows,
            Level = CriticalityLevel.Dangerous,
            Examples = { example }
        };

        return (action, example);
    }

    private sealed class PaletteHarness : IDisposable
    {
        private readonly string _rootPath;

        private PaletteHarness(
            string rootPath,
            MainViewModel main,
            RecordingDialogService dialog,
            RecordingEmbeddedSessionManager embeddedSessionManager,
            List<string> order)
        {
            _rootPath = rootPath;
            Main = main;
            Dialog = dialog;
            EmbeddedSessionManager = embeddedSessionManager;
            Order = order;
        }

        public MainViewModel Main { get; }

        public RecordingDialogService Dialog { get; }

        public RecordingEmbeddedSessionManager EmbeddedSessionManager { get; }

        public List<string> Order { get; }

        public static async Task<PaletteHarness> CreateAsync(bool confirmResult)
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall-palette-dangerous-tests",
                Guid.NewGuid().ToString("N"));
            ConfigManager configManager = new(rootPath);
            await configManager.InitializeAsync();

            LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
            List<string> order = [];
            RecordingDialogService dialogService = new(confirmResult, order);
            ConnectionStateMachine connectionStateMachine = new();
            ApplicationStatusMachine appStatus = new();
            TunnelManager tunnelManager = new();
            HostKeyStore hostKeyStore = new();
            RecordingEmbeddedSessionManager embeddedSessionManager = new(order);
            ToolRegistry toolRegistry = new();
            ConnectionService connectionService = new(
                configManager,
                localizer,
                new FakeTunnelService(),
                Array.Empty<IProtocolHandler>());
            SplitService splitService = new(
                configManager,
                localizer,
                connectionStateMachine,
                tunnelManager,
                embeddedSessionManager,
                connectionService,
                toolRegistry,
                dialogService, new PaneCloseArbiter());
            FakeUiDispatcher dispatcher = new();
            ConnectionViewModel connection = new(
                localizer,
                dialogService,
                splitService,
                new PaneCloseArbiter(),
                new SessionWindowService(static (_, _) => { }));
            ServerListViewModel serverList = new(
                configManager,
                localizer,
                dispatcher,
                connectionStateMachine,
                connectionService,
                dialogService,
                new FakeRdpImportService(),
                new PuttySessionImporter(new FakePuttySessionRegistrySource(), configManager),
                new Heimdall.App.Services.Import.KnownHostsImporter(configManager, hostKeyStore));
            TrustedHostKeysSettingsViewModel trustedHostKeys = new(
                new HostKeyTrustService(new HostKeyStore()),
                () => new KnownHostsImportReport(0, 0, []),
                () => new KnownHostsExportReport(0, 0, 0),
                localizer,
                dialogService,
                new FakeClipboardService(),
                dispatcher);
            TrustedRdpCertificatesSettingsViewModel trustedRdpCertificates = new(
                new RdpCertificateTrustStore(),
                () => Task.FromResult<IReadOnlyList<ServerProfileDto>>([]),
                localizer,
                dialogService,
                dispatcher);
            SettingsViewModel settings = new(
                configManager,
                localizer,
                dialogService,
                trustedHostKeys,
                trustedRdpCertificates,
                new PinManager(),
                new Heimdall.Core.Security.Vault.VaultLifecycleService(configManager),
                new FakeUpdateService(),
                new AppVersionProvider("2026.061501"),
                new FakeUpdateInstallFlow(),
                new FakeBrowserLauncher());

            MainViewModel main = new(
                configManager,
                localizer,
                appStatus,
                hostKeyStore,
                dialogService,
                embeddedSessionManager,
                new HeimdallThemeService(configManager),
                new FakeSessionRestoreCoordinator(),
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
                new PaneCloseArbiter(),
                new CommandPaletteViewModelFactory(
                    localizer,
                    dialogService,
                    toolRegistry,
                    configManager,
                    embeddedSessionManager,
                    new ExternalToolLaunchService(dialogService),
                    new RecentConnectionTracker(),
                    CommandLibraryTestHelpers.CreateResolverServiceProvider()
                        .GetRequiredService<IServiceScopeFactory>()),
                new TunnelsViewModelFactory(
                    localizer,
                    tunnelManager,
                    connectionStateMachine,
                    hostKeyStore,
                    RejectingHostKeyVerifier.Instance,
                    configManager));

            return new PaletteHarness(rootPath, main, dialogService, embeddedSessionManager, order);
        }

        public void Dispose()
        {
            Main.Dispose();

            try
            {
                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    private sealed class RecordingDialogService(bool confirmResult, List<string> order) : IDialogService
    {
        public int ConfirmCallCount { get; private set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            ConfirmCallCount++;
            order.Add("confirm");
            return Task.FromResult(confirmResult);
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => Task.FromResult<bool?>(false);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => Task.FromResult(defaultValue);

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
            => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
            => Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
            => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
            => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
            => Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
            => Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
            => Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
            => Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
            => Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
            => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
            => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);

        public Task<int?> ShowBulkEditPortAsync(
            int count,
            int? initialPort,
            CancellationToken cancellationToken)
            => Task.FromResult(initialPort);

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken)
            => Task.FromResult(initialUsername);

        public Task<string?> ShowBulkEditPasswordAsync(
            int count,
            CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

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

    private sealed class RecordingEmbeddedSessionManager(List<string> order) : IEmbeddedSessionManager
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

        public int SendCallCount { get; private set; }

        public string? LastCommand { get; private set; }

        public object CreateHostControl(
            SessionTabViewModel sessionTab,
            string displayName,
            string connectionType,
            ISessionResult session,
            AppSettings? settings = null,
            string? initialRemotePath = null)
            => throw new NotSupportedException();

        public void DisconnectSession(SessionPaneModel pane, DisconnectReason reason)
            => throw new NotSupportedException();

        public EmbeddedSshView CreateConnectingSshHostControl(
            SessionTabViewModel sessionTab,
            string displayName,
            ServerProfileDto server,
            AppSettings? settings = null)
            => throw new NotSupportedException();

        public void AttachSshSession(
            SessionTabViewModel sessionTab,
            ISessionResult sessionResult,
            AppSettings? settings = null)
            => throw new NotSupportedException();

        public object CreateToolControl(
            SessionTabViewModel sessionTab,
            string toolId,
            ToolContext? context,
            AppSettings? settings = null)
            => throw new NotSupportedException();

        public bool TrySendCommandToSession(SessionTabViewModel session, string command)
        {
            SendCallCount++;
            LastCommand = command;
            order.Add("send");
            return true;
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
            => Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

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
            => Task.FromResult(new RdpImportResult());
    }

    private sealed class FakePuttySessionRegistrySource : IPuttySessionRegistrySource
    {
        public Task<IReadOnlyList<RawPuttySession>> ReadSessionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RawPuttySession>>([]);
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public void SetText(string text)
        {
        }
    }

    private sealed class FakeBrowserLauncher : IBrowserLauncher
    {
        public void Open(string url)
        {
        }
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public Task<UpdateCheckResult> CheckForUpdatesAsync(
            HeimdallVersion current,
            string owner,
            string repo,
            CancellationToken cancellationToken)
            => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.UpToDate, null));

        public Task<IVerifiedUpdatePackage> DownloadVerifiedAsync(
            UpdateInfo update,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeUpdateInstallFlow : IUpdateInstallFlow
    {
        public Task<UpdateInstallOutcome> RunAsync(
            UpdateInfo update,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeSessionRestoreCoordinator : ISessionRestoreCoordinator
    {
        public Task RestoreAsync(
            ISessionRestoreHost host,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePostConnectSequenceRunner : IPostConnectSequenceRunner
    {
        public Task<PostConnectRunResult> RunAsync(
            IReadOnlyList<PostConnectStep> steps,
            Action<string> writeCallback,
            IProgress<PostConnectRunProgress>? progress,
            CancellationToken ct,
            IPostConnectStepResolver? resolver = null)
            => Task.FromResult(new PostConnectRunResult());
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
}
