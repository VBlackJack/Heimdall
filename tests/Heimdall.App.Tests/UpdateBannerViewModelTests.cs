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

using System.IO;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.Updates;

namespace Heimdall.App.Tests;

public sealed class UpdateBannerViewModelTests
{
    private const string Current = "2026.061501";
    private const string Newer = "2026.061502";
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CheckOnStartup_Disabled_DoesNotCheckOrShowBanner()
    {
        var settings = BaseSettings();
        settings.UpdateCheckEnabled = false;
        var update = new StubUpdateService { Result = Available(Newer) };
        var vm = CreateViewModel(settings, update, Current);

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.False(update.WasCalled);
        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public async Task CheckOnStartup_RecentLastCheck_Throttled()
    {
        var settings = BaseSettings();
        settings.UpdateLastCheckUtc = DateTimeOffset.UtcNow.ToString("O");
        var update = new StubUpdateService { Result = Available(Newer) };
        var vm = CreateViewModel(settings, update, Current);

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.False(update.WasCalled);
        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public async Task CheckOnStartup_StaleLastCheck_RunsCheck()
    {
        var settings = BaseSettings();
        settings.UpdateLastCheckUtc = DateTimeOffset.UtcNow.AddHours(-48).ToString("O");
        var update = new StubUpdateService { Result = UpToDate() };
        var vm = CreateViewModel(settings, update, Current);

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.True(update.WasCalled);
    }

    [Fact]
    public async Task CheckOnStartup_CurrentVersionNull_DoesNotCheck()
    {
        var settings = BaseSettings();
        var update = new StubUpdateService { Result = Available(Newer) };
        var vm = CreateViewModel(settings, update, "unknown");

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.False(update.WasCalled);
        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public async Task CheckOnStartup_UpdateAvailable_ShowsBanner()
    {
        var settings = BaseSettings();
        var update = new StubUpdateService { Result = Available(Newer) };
        var vm = CreateViewModel(settings, update, Current);

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.True(vm.IsBannerVisible);
        Assert.Equal(Newer, vm.BannerVersionText);
    }

    /// <remarks>
    /// The repository used to be read from settings.json, unvalidated and shown in no
    /// interface: whoever could write that file could point the updater at their own
    /// repository, whose checksum file would vouch for their own installer.
    /// </remarks>
    [Fact]
    public async Task CheckOnStartup_UsesThePinnedRepository()
    {
        var update = new StubUpdateService { Result = UpToDate() };
        var vm = CreateViewModel(BaseSettings(), update, Current);

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.Equal(UpdateSource.RepositoryOwner, update.Owner);
        Assert.Equal(UpdateSource.RepositoryName, update.Repo);
    }

    /// <remarks>
    /// Compared as text, a skipped "v2026.061502" was offered again as "2026.061502":
    /// the type's equality is numeric and the comparison was not.
    /// </remarks>
    [Fact]
    public async Task CheckOnStartup_SkippedVersionSpelledWithALeadingV_StillSkipped()
    {
        var settings = BaseSettings();
        settings.UpdateSkippedVersion = "v" + Newer;
        var update = new StubUpdateService { Result = Available(Newer) };
        var vm = CreateViewModel(settings, update, Current);

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public async Task CheckOnStartup_SkippedVersion_DoesNotShowBanner()
    {
        var settings = BaseSettings();
        settings.UpdateSkippedVersion = Newer;
        var update = new StubUpdateService { Result = Available(Newer) };
        var vm = CreateViewModel(settings, update, Current);

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public async Task CheckOnStartup_UpToDate_NoBannerButPersistsLastCheck()
    {
        var settings = BaseSettings();
        var update = new StubUpdateService { Result = UpToDate() };
        var vm = CreateViewModel(settings, update, Current);

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.False(vm.IsBannerVisible);
        Assert.False(string.IsNullOrEmpty(settings.UpdateLastCheckUtc));
    }

    [Fact]
    public async Task CheckOnStartup_CheckFailed_DoesNotPersistLastCheck()
    {
        var settings = BaseSettings();
        var update = new StubUpdateService { Result = new UpdateCheckResult(UpdateCheckStatus.CheckFailed, null) };
        var vm = CreateViewModel(settings, update, Current);

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.False(vm.IsBannerVisible);
        Assert.Null(settings.UpdateLastCheckUtc);
    }

    [Fact]
    public async Task CheckOnStartup_UpdateNotInstallable_HidesBannerButPersistsLastCheck()
    {
        var settings = BaseSettings();
        var update = new StubUpdateService { Result = NotInstallable(Newer) };
        var vm = CreateViewModel(settings, update, Current);

        await vm.CheckOnStartupAsync(CancellationToken.None);

        Assert.True(update.WasCalled);
        Assert.False(vm.IsBannerVisible);
        Assert.False(string.IsNullOrEmpty(settings.UpdateLastCheckUtc));
    }

    [Fact]
    public async Task SkipVersion_PersistsSkippedVersionAndHidesBanner()
    {
        var settings = BaseSettings();
        var update = new StubUpdateService { Result = Available(Newer) };
        var vm = CreateViewModel(settings, update, Current);
        await vm.CheckOnStartupAsync(CancellationToken.None);

        await vm.SkipVersionCommand.ExecuteAsync(null);

        Assert.False(vm.IsBannerVisible);
        Assert.Equal(Newer, settings.UpdateSkippedVersion);
    }

    [Fact]
    public async Task Later_HidesBannerWithoutPersisting()
    {
        var settings = BaseSettings();
        var update = new StubUpdateService { Result = Available(Newer) };
        var vm = CreateViewModel(settings, update, Current);
        await vm.CheckOnStartupAsync(CancellationToken.None);

        vm.LaterCommand.Execute(null);

        Assert.False(vm.IsBannerVisible);
        Assert.Null(settings.UpdateSkippedVersion);
    }

    [Fact]
    public async Task ViewRelease_OpensReleaseUrl()
    {
        var settings = BaseSettings();
        var update = new StubUpdateService { Result = Available(Newer) };
        var browser = new StubBrowserLauncher();
        var vm = new UpdateBannerViewModel(
            update, new StubConfigManager(settings), new AppVersionProvider(Current), browser,
            new FakeUpdateInstallFlow(), new ConfirmingDialogService(false), new LocalizationManager(), new FakeUpdateOutcomeStore());
        await vm.CheckOnStartupAsync(CancellationToken.None);

        vm.ViewReleaseCommand.Execute(null);

        Assert.Equal($"https://github.com/VBlackJack/Heimdall/releases/tag/v{Newer}", browser.OpenedUrl);
    }

    [Fact]
    public void DownloadAndInstall_NoAvailableUpdate_CommandCannotExecute()
    {
        var vm = CreateViewModel(BaseSettings(), new StubUpdateService { Result = UpToDate() }, Current);

        Assert.False(vm.DownloadAndInstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task DownloadAndInstall_CanExecuteReflectsUpdateAndInstallingState()
    {
        var vm = CreateViewModel(BaseSettings(), new StubUpdateService { Result = Available(Newer) }, Current);
        Assert.False(vm.DownloadAndInstallCommand.CanExecute(null));

        await vm.CheckOnStartupAsync(CancellationToken.None);
        Assert.True(vm.DownloadAndInstallCommand.CanExecute(null));

        vm.IsInstalling = true;
        Assert.False(vm.DownloadAndInstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task DownloadAndInstall_WhileFlowActive_DisablesDismissActionsUntilCancelled()
    {
        AppSettings settings = BaseSettings();
        StubUpdateService update = new StubUpdateService { Result = Available(Newer) };
        FakeUpdateInstallFlow flow = new FakeUpdateInstallFlow { WaitForCancellation = true };
        UpdateBannerViewModel viewModel = CreateViewModel(
            settings,
            update,
            Current,
            installFlow: flow,
            dialogService: new ConfirmingDialogService(true));
        await viewModel.CheckOnStartupAsync(CancellationToken.None);

        Task installTask = viewModel.DownloadAndInstallCommand.ExecuteAsync(null);
        await flow.RunStarted.Task.WaitAsync(CompletionTimeout);

        bool laterCanExecute = viewModel.LaterCommand.CanExecute(null);
        bool skipCanExecute = viewModel.SkipVersionCommand.CanExecute(null);
        Assert.False(
            laterCanExecute || skipCanExecute,
            $"Dismiss actions must be disabled while installing. Later={laterCanExecute}, Skip={skipCanExecute}");
        Assert.True(viewModel.DownloadAndInstallCancelCommand.CanExecute(null));
        Assert.True(viewModel.IsBannerVisible);
        Assert.Null(settings.UpdateSkippedVersion);

        viewModel.DownloadAndInstallCancelCommand.Execute(null);
        await installTask.WaitAsync(CompletionTimeout);

        Assert.False(viewModel.IsInstalling);
        Assert.True(viewModel.IsBannerVisible);
        Assert.True(viewModel.LaterCommand.CanExecute(null));
        Assert.True(viewModel.SkipVersionCommand.CanExecute(null));
        Assert.Null(settings.UpdateSkippedVersion);
    }

    [Fact]
    public async Task DownloadAndInstall_DeclinedConfirm_DoesNotRunFlow()
    {
        var flow = new FakeUpdateInstallFlow();
        var vm = CreateViewModel(
            BaseSettings(), new StubUpdateService { Result = Available(Newer) }, Current,
            installFlow: flow, dialogService: new ConfirmingDialogService(false));
        await vm.CheckOnStartupAsync(CancellationToken.None);

        await vm.DownloadAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(0, flow.RunCallCount);
        Assert.False(vm.IsInstalling);
    }

    [Fact]
    public async Task DownloadAndInstall_OutcomeStarted_RunsFlowAndClearsStatus()
    {
        var localizer = await CreateLocalizerAsync();
        var flow = new FakeUpdateInstallFlow { Outcome = UpdateInstallOutcome.Started };
        var vm = CreateViewModel(
            BaseSettings(), new StubUpdateService { Result = Available(Newer) }, Current,
            installFlow: flow, dialogService: new ConfirmingDialogService(true), localizer: localizer);
        await vm.CheckOnStartupAsync(CancellationToken.None);

        await vm.DownloadAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(1, flow.RunCallCount);
        Assert.Equal(string.Empty, vm.BannerStatusText);
        Assert.False(vm.HasBannerStatus);
        Assert.False(vm.IsInstalling);
    }

    [Fact]
    public async Task DownloadAndInstall_OutcomeInstallLaunchFailed_ShowsInstallFailedStatus()
    {
        var localizer = await CreateLocalizerAsync();
        var flow = new FakeUpdateInstallFlow { Outcome = UpdateInstallOutcome.InstallLaunchFailed };
        var vm = CreateViewModel(
            BaseSettings(), new StubUpdateService { Result = Available(Newer) }, Current,
            installFlow: flow, dialogService: new ConfirmingDialogService(true), localizer: localizer);
        await vm.CheckOnStartupAsync(CancellationToken.None);

        await vm.DownloadAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(1, flow.RunCallCount);
        Assert.Equal(localizer.Format("SettingsUpdateStatusInstallFailed"), vm.BannerStatusText);
        Assert.True(vm.HasBannerStatus);
        Assert.False(vm.IsInstalling);
    }

    [Fact]
    public async Task ReportPreviousAttempt_VersionDidNotMove_ShowsAnOutcomeNoticeRatherThanAnOffer()
    {
        var store = new FakeUpdateOutcomeStore
        {
            Pending = new UpdateAttemptRecord(
                UpdateAttemptRecord.CurrentSchemaVersion,
                Newer,
                DateTimeOffset.UtcNow),
        };
        var localizer = await CreateLocalizerAsync();
        var vm = CreateViewModel(
            BaseSettings(),
            new StubUpdateService { Result = UpToDate() },
            Current,
            localizer: localizer,
            outcomeStore: store);

        await vm.ReportPreviousAttemptAsync(CancellationToken.None);

        Assert.True(vm.IsBannerVisible);

        // The banner's offer affordances hang off IsBannerVisible. Without this flag the
        // user would read "a new version is available:" followed by nothing, above
        // buttons with no version to act on.
        Assert.True(vm.IsOutcomeNotice);
        Assert.False(vm.IsUpdateOffer);
        Assert.Equal(
            localizer.Format("UpdateBannerOutcomeNotApplied", Newer),
            vm.BannerStatusText);
    }

    [Fact]
    public async Task ReportPreviousAttempt_VersionAdvanced_SaysNothing()
    {
        var store = new FakeUpdateOutcomeStore
        {
            Pending = new UpdateAttemptRecord(
                UpdateAttemptRecord.CurrentSchemaVersion,
                Newer,
                DateTimeOffset.UtcNow),
        };
        var vm = CreateViewModel(
            BaseSettings(),
            new StubUpdateService { Result = UpToDate() },
            Newer,
            outcomeStore: store);

        await vm.ReportPreviousAttemptAsync(CancellationToken.None);

        // The direction that would cost the most confidence: announcing a failure to
        // someone who is demonstrably running the new version.
        Assert.False(vm.IsBannerVisible);
        Assert.Equal(string.Empty, vm.BannerStatusText);

        // Consumed all the same, so it cannot resurface at some later launch.
        Assert.Contains(FakeUpdateOutcomeStore.TakeCall, store.Calls);
    }

    [Fact]
    public async Task ReportPreviousAttempt_NothingPending_SaysNothing()
    {
        var store = new FakeUpdateOutcomeStore();
        var vm = CreateViewModel(
            BaseSettings(),
            new StubUpdateService { Result = UpToDate() },
            Current,
            outcomeStore: store);

        await vm.ReportPreviousAttemptAsync(CancellationToken.None);

        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public async Task ReportPreviousAttempt_RunsEvenWhenTheStartupCheckWouldReturnEarly()
    {
        var store = new FakeUpdateOutcomeStore
        {
            Pending = new UpdateAttemptRecord(
                UpdateAttemptRecord.CurrentSchemaVersion,
                Newer,
                DateTimeOffset.UtcNow),
        };
        var localizer = await CreateLocalizerAsync();

        // Both conditions that make CheckOnStartupAsync return early. A reader placed
        // inside it would be silent in exactly the case it exists for: a relaunch
        // minutes after a failed update falls squarely inside the throttle window.
        var settings = BaseSettings();
        settings.UpdateCheckEnabled = false;
        settings.UpdateLastCheckUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        var vm = CreateViewModel(
            settings,
            new StubUpdateService { Result = UpToDate() },
            Current,
            localizer: localizer,
            outcomeStore: store);

        await vm.ReportPreviousAttemptAsync(CancellationToken.None);

        Assert.True(vm.IsBannerVisible);
        Assert.True(vm.IsOutcomeNotice);
    }

    private static UpdateBannerViewModel CreateViewModel(
        AppSettings settings,
        StubUpdateService update,
        string informationalVersion,
        IUpdateInstallFlow? installFlow = null,
        IDialogService? dialogService = null,
        LocalizationManager? localizer = null,
        IUpdateOutcomeStore? outcomeStore = null)
        => new(
            update,
            new StubConfigManager(settings),
            new AppVersionProvider(informationalVersion),
            new StubBrowserLauncher(),
            installFlow ?? new FakeUpdateInstallFlow(),
            dialogService ?? new ConfirmingDialogService(true),
            localizer ?? new LocalizationManager(),
            outcomeStore ?? new FakeUpdateOutcomeStore());

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private static AppSettings BaseSettings() => new()
    {
        UpdateCheckEnabled = true,
        UpdateCheckIntervalHours = 24,
        UpdateLastCheckUtc = null,
        UpdateSkippedVersion = null,
    };

    private static UpdateCheckResult UpToDate()
        => new(UpdateCheckStatus.UpToDate, null);

    private static UpdateCheckResult NotInstallable(string version)
    {
        var release = new ReleaseRef(
            HeimdallVersion.Parse(version),
            $"v{version}",
            $"https://github.com/VBlackJack/Heimdall/releases/tag/v{version}");
        return new UpdateCheckResult(UpdateCheckStatus.UpdateNotInstallable, null, release);
    }

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

    private sealed class StubUpdateService : IUpdateService
    {
        public UpdateCheckResult Result { get; set; } = new(UpdateCheckStatus.UpToDate, null);

        public bool WasCalled { get; private set; }

        public string? Owner { get; private set; }

        public string? Repo { get; private set; }

        public Task<UpdateCheckResult> CheckForUpdatesAsync(HeimdallVersion current, string owner, string repo, CancellationToken cancellationToken)
        {
            WasCalled = true;
            Owner = owner;
            Repo = repo;
            return Task.FromResult(Result);
        }

        public Task<IVerifiedUpdatePackage> DownloadVerifiedAsync(
            UpdateInfo update,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeUpdateInstallFlow : IUpdateInstallFlow
    {
        public UpdateInstallOutcome Outcome { get; set; } = UpdateInstallOutcome.Started;

        public int RunCallCount { get; private set; }

        public bool WaitForCancellation { get; set; }

        public TaskCompletionSource RunStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UpdateInstallOutcome> RunAsync(
            UpdateInfo update,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            RunCallCount++;
            RunStarted.TrySetResult();
            if (!WaitForCancellation)
            {
                return Outcome;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return UpdateInstallOutcome.Cancelled;
            }

            return Outcome;
        }
    }

    private sealed class ConfirmingDialogService(bool confirmResult) : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
            => Task.FromResult(confirmResult);

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

    private sealed class StubBrowserLauncher : IBrowserLauncher
    {
        public string? OpenedUrl { get; private set; }

        public void Open(string url) => OpenedUrl = url;
    }

    private sealed class StubConfigManager : IConfigManager
    {
        public StubConfigManager(AppSettings settings) => Settings = settings;

        public AppSettings Settings { get; }

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
}
