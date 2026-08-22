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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.Updates;

namespace Heimdall.App.UiTests.ViewModels;

/// <summary>
/// Covers the banner commands as the view actually consumes them: live WPF buttons
/// bound to the same command paths as MainWindow.xaml.
/// Polling ICommand.CanExecute directly cannot see this, because a Button only refreshes
/// its enabled state when the command raises CanExecuteChanged.
///
/// Deliberately not tagged RequiresDesktop, unlike most of this project: that category runs
/// only in the informational CI lane, where a regression could never fail a build. This test
/// shows no window and injects no input - it needs the shared STA host, nothing more - so it
/// belongs in the blocking lane. Do not add the trait for consistency with its neighbours.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class UpdateBannerDownloadButtonTests
{
    private const string Current = "2026.061501";
    private const string Newer = "2026.061502";

    [StaFact]
    public void BoundButton_IsEnabled_AfterStartupCheckFindsUpdate()
    {
        WpfTestHost.Invoke(() =>
        {
            var viewModel = CreateViewModel();
            var button = new Button { DataContext = new BannerHost(viewModel) };
            button.SetBinding(
                ButtonBase.CommandProperty,
                new Binding("Update.DownloadAndInstallCommand"));

            Assert.False(button.IsEnabled);

            RunToCompletion(viewModel.CheckOnStartupAsync(CancellationToken.None));

            Assert.True(button.IsEnabled);
        });
    }

    [StaFact]
    public void BoundDismissButtons_DisableAndReenableWithInstallingState()
    {
        WpfTestHost.Invoke(() =>
        {
            UpdateBannerViewModel viewModel = CreateViewModel();
            BannerHost host = new BannerHost(viewModel);
            Button laterButton = CreateBoundButton(host, "Update.LaterCommand");
            Button skipButton = CreateBoundButton(host, "Update.SkipVersionCommand");

            RunToCompletion(viewModel.CheckOnStartupAsync(CancellationToken.None));
            Assert.True(laterButton.IsEnabled);
            Assert.True(skipButton.IsEnabled);

            viewModel.IsInstalling = true;

            Assert.False(laterButton.IsEnabled);
            Assert.False(skipButton.IsEnabled);

            viewModel.IsInstalling = false;

            Assert.True(laterButton.IsEnabled);
            Assert.True(skipButton.IsEnabled);
        });
    }

    private static Button CreateBoundButton(BannerHost host, string commandPath)
    {
        Button button = new Button { DataContext = host };
        button.SetBinding(ButtonBase.CommandProperty, new Binding(commandPath));
        return button;
    }

    /// <summary>
    /// Pumps the host dispatcher until the awaited operation finishes, so continuations
    /// resume on the UI thread the way they do under the real application dispatcher.
    /// </summary>
    private static void RunToCompletion(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static UpdateBannerViewModel CreateViewModel()
        => new(
            new StubUpdateService { Result = Available(Newer) },
            new StubConfigManager(BaseSettings()),
            new AppVersionProvider(Current),
            new StubBrowserLauncher(),
            new UnusedUpdateInstallFlow(),
            new UnusedDialogService(),
            new LocalizationManager(),
            new UnusedUpdateOutcomeStore());

    private static AppSettings BaseSettings() => new()
    {
        UpdateCheckEnabled = true,
        UpdateCheckIntervalHours = 24,
        UpdateLastCheckUtc = null,
        UpdateSkippedVersion = null,
        UpdateRepositoryOwner = "VBlackJack",
        UpdateRepositoryName = "Heimdall"
    };

    private static UpdateCheckResult Available(string version)
    {
        var info = new UpdateInfo(
            HeimdallVersion.Parse(version),
            $"v{version}",
            $"https://github.com/VBlackJack/Heimdall/releases/tag/v{version}",
            "notes",
            new UpdateAsset($"Heimdall_{version}_Standard_Setup.exe", "https://example.test/setup.exe", 1),
            null);
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, info);
    }

    /// <summary>Mirrors the MainWindow data context shape so the binding path matches the view.</summary>
    private sealed class BannerHost(UpdateBannerViewModel update)
    {
        public UpdateBannerViewModel Update { get; } = update;
    }

    private sealed class StubUpdateService : IUpdateService
    {
        public UpdateCheckResult Result { get; set; } = new(UpdateCheckStatus.UpToDate, null);

        public Task<UpdateCheckResult> CheckForUpdatesAsync(HeimdallVersion current, string owner, string repo, CancellationToken cancellationToken)
            => Task.FromResult(Result);

        public Task<IVerifiedUpdatePackage> DownloadVerifiedAsync(
            UpdateInfo update,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class UnusedUpdateInstallFlow : IUpdateInstallFlow
    {
        public Task<UpdateInstallOutcome> RunAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class StubBrowserLauncher : IBrowserLauncher
    {
        public void Open(string url)
        {
        }
    }

    private sealed class StubConfigManager(AppSettings settings) : IConfigManager
    {
        public AppSettings Settings { get; } = settings;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            mutate(Settings);
            return Task.CompletedTask;
        }

        public string ConfigPath => throw new NotSupportedException();

        public string SettingsPath => throw new NotSupportedException();

        public string ServersPath => throw new NotSupportedException();

        public event Action<AppSettings>? SettingsChanged { add { } remove { } }

        public Task InitializeAsync() => throw new NotSupportedException();

        public Task SaveSettingsAsync(AppSettings settings) => throw new NotSupportedException();

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) => throw new NotSupportedException();

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) => throw new NotSupportedException();

        public Task<List<ServerProfileDto>> LoadServersAsync() => throw new NotSupportedException();

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate) =>
            throw new NotSupportedException();

        public Task SaveServersAsync(List<ServerProfileDto> servers) => throw new NotSupportedException();
    }

    /// <summary>An outcome store these tests never consult.</summary>
    private sealed class UnusedUpdateOutcomeStore : IUpdateOutcomeStore
    {
        public void WriteAttempt(string attemptedVersion)
        {
        }

        public void Clear()
        {
        }

        public UpdateAttemptRecord? TryTakePending() => null;
    }


    private sealed class UnusedDialogService : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
            => throw new NotSupportedException();

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => throw new NotSupportedException();

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => throw new NotSupportedException();

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
            => throw new NotSupportedException();

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
            => throw new NotSupportedException();

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
            => throw new NotSupportedException();

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => throw new NotSupportedException();

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void ShowError(string title, string message)
            => throw new NotSupportedException();

        public void ShowInfo(string title, string message)
            => throw new NotSupportedException();

        public void ShowWarning(string title, string message)
            => throw new NotSupportedException();
    }
}
