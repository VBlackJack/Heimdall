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

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Updates;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Drives the non-modal "update available" banner. Performs a throttled startup
/// check and exposes the three banner actions. v1 only notifies and opens the
/// release page; downloading/launching an installer is a later iteration.
/// </summary>
public partial class UpdateBannerViewModel : ObservableObject
{
    private readonly IUpdateService _updateService;
    private readonly IConfigManager _configManager;
    private readonly IAppVersionProvider _appVersionProvider;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly IUpdateInstallFlow _installFlow;
    private readonly IDialogService _dialogService;
    private readonly LocalizationManager _localizer;
    private readonly IUpdateOutcomeStore _outcomeStore;

    private HeimdallVersion? _candidateVersion;
    private string _releaseUrl = string.Empty;

    // The update found by the startup check; drives the download-and-install action.
    private UpdateInfo? _availableUpdate;

    [ObservableProperty]
    private bool _isBannerVisible;

    [ObservableProperty]
    private string _bannerVersionText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadAndInstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaterCommand))]
    [NotifyCanExecuteChangedFor(nameof(SkipVersionCommand))]
    private bool _isInstalling;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _bannerStatusText = string.Empty;

    /// <summary>True when a status message should be shown under the banner text.</summary>
    public bool HasBannerStatus => !string.IsNullOrEmpty(BannerStatusText);

    partial void OnBannerStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasBannerStatus));

    /// <summary>
    /// True when the banner is reporting what a previous update attempt did, rather than
    /// offering a new version.
    /// </summary>
    /// <remarks>
    /// The banner's offer affordances - the version line, Download and install, View
    /// release, Skip this version - all hang off <see cref="IsBannerVisible"/>. Showing an
    /// outcome without this flag would leave "a new version is available:" followed by
    /// nothing, above buttons that have no version to act on.
    /// </remarks>
    [ObservableProperty]
    private bool _isOutcomeNotice;

    /// <summary>True when the banner is offering an update, which is its ordinary use.</summary>
    public bool IsUpdateOffer => !IsOutcomeNotice;

    partial void OnIsOutcomeNoticeChanged(bool value) =>
        OnPropertyChanged(nameof(IsUpdateOffer));

    public UpdateBannerViewModel(
        IUpdateService updateService,
        IConfigManager configManager,
        IAppVersionProvider appVersionProvider,
        IBrowserLauncher browserLauncher,
        IUpdateInstallFlow installFlow,
        IDialogService dialogService,
        LocalizationManager localizer,
        IUpdateOutcomeStore outcomeStore)
    {
        _updateService = updateService;
        _configManager = configManager;
        _appVersionProvider = appVersionProvider;
        _browserLauncher = browserLauncher;
        _installFlow = installFlow;
        _dialogService = dialogService;
        _localizer = localizer;
        _outcomeStore = outcomeStore;
    }

    /// <summary>
    /// Reports what the previous update attempt did, if anything, and clears the record.
    /// </summary>
    /// <remarks>
    /// A separate entry point rather than a branch inside
    /// <see cref="CheckOnStartupAsync"/>, and that is not a matter of taste. That method
    /// returns early when update checks are disabled, and again inside a throttle window
    /// whose default is a day. A relaunch minutes after a failed update lands squarely in
    /// the throttle, so a reader placed there would be silent in exactly the case it
    /// exists for.
    /// <para>
    /// It says nothing at all unless the version really did not move. Telling someone
    /// their update failed while they are looking at the new version would cost more
    /// confidence than the silence this replaces.
    /// </para>
    /// </remarks>
    public Task ReportPreviousAttemptAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PendingUpdateOutcome? pending = _outcomeStore.TryTakePending();
        UpdateRelaunchOutcome outcome = UpdateOutcomeClassifier.Classify(
            pending?.Attempt,
            pending?.Failure,
            _appVersionProvider.Current);

        if (outcome is UpdateRelaunchOutcome.None or UpdateRelaunchOutcome.Succeeded)
        {
            return Task.CompletedTask;
        }

        Core.Logging.FileLogger.Warn(
            $"[Updates] previous attempt did not apply: outcome={outcome} "
            + $"attempted={pending?.Attempt.AttemptedVersion} "
            + $"running={_appVersionProvider.Current?.ToString()} "
            + $"stage={pending?.Failure?.Stage} exitCode={pending?.Failure?.InstallerExitCode}");

        string? key = UpdateRelaunchOutcomeText.StatusKey(outcome);
        if (key is null)
        {
            return Task.CompletedTask;
        }

        BannerStatusText = _localizer.Format(
            key,
            pending?.Attempt.AttemptedVersion ?? string.Empty);
        IsOutcomeNotice = true;
        IsBannerVisible = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs a throttled background update check at startup and shows the banner
    /// when a non-skipped newer version is available. Usually a no-op (throttled).
    /// </summary>
    public async Task CheckOnStartupAsync(CancellationToken cancellationToken)
    {
        var settings = await _configManager.LoadSettingsAsync();
        if (!settings.UpdateCheckEnabled)
        {
            return;
        }

        if (IsWithinThrottleWindow(settings.UpdateLastCheckUtc, settings.UpdateCheckIntervalHours))
        {
            return;
        }

        var current = _appVersionProvider.Current;
        if (current is null)
        {
            return;
        }

        var result = await _updateService.CheckForUpdatesAsync(
            current.Value,
            settings.UpdateRepositoryOwner,
            settings.UpdateRepositoryName,
            cancellationToken);

        if (result.Status == UpdateCheckStatus.CheckFailed)
        {
            // Do not stamp UpdateLastCheckUtc on failure so an offline launch retries next time.
            return;
        }

        await PersistLastCheckAsync();

        if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Update is null)
        {
            return;
        }

        var version = result.Update.Version;
        if (string.Equals(version.ToString(), settings.UpdateSkippedVersion, StringComparison.Ordinal))
        {
            return;
        }

        _candidateVersion = version;
        _releaseUrl = result.Update.ReleaseUrl;
        _availableUpdate = result.Update;
        // Plain field: no generated notification, so bound controls must be told explicitly.
        DownloadAndInstallCommand.NotifyCanExecuteChanged();
        BannerVersionText = version.ToString();
        IsBannerVisible = true;
    }

    private bool CanDownloadAndInstall() => !IsInstalling && _availableUpdate is not null;

    /// <summary>
    /// Downloads the verified installer for the banner's update and launches the relauncher via
    /// the shared install flow; the app shuts down on success. Stays open on failure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadAndInstall), IncludeCancelCommand = true)]
    private async Task DownloadAndInstallAsync(CancellationToken cancellationToken)
    {
        var update = _availableUpdate;
        if (update is null)
        {
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["SettingsUpdateConfirmTitle"],
            _localizer.Format("SettingsUpdateConfirmMessage", update.Version.ToString()));
        if (!confirmed)
        {
            return;
        }

        IsInstalling = true;
        DownloadProgress = 0;
        BannerStatusText = _localizer.Format("SettingsUpdateStatusDownloading");
        try
        {
            var progress = new Progress<double>(p => DownloadProgress = p);
            var outcome = await _installFlow.RunAsync(update, progress, cancellationToken);
            var key = UpdateInstallOutcomeText.StatusKey(outcome);
            BannerStatusText = key is null ? string.Empty : _localizer.Format(key);
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void ViewRelease() => _browserLauncher.Open(_releaseUrl);

    private bool CanDismissBanner() => !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanDismissBanner))]
    private void Later() => IsBannerVisible = false;

    [RelayCommand(CanExecute = nameof(CanDismissBanner))]
    private async Task SkipVersionAsync()
    {
        if (_candidateVersion is { } version)
        {
            var skipped = version.ToString();
            await _configManager.MergeSettingAsync(s => s.UpdateSkippedVersion = skipped);
        }

        IsBannerVisible = false;
    }

    private async Task PersistLastCheckAsync()
    {
        var nowUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await _configManager.MergeSettingAsync(s => s.UpdateLastCheckUtc = nowUtc);
    }

    private static bool IsWithinThrottleWindow(string? lastCheckUtc, int intervalHours)
    {
        if (string.IsNullOrWhiteSpace(lastCheckUtc))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                lastCheckUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var last))
        {
            return false;
        }

        return (DateTimeOffset.UtcNow - last).TotalHours < intervalHours;
    }
}
