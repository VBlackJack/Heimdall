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

    private HeimdallVersion? _candidateVersion;
    private string _releaseUrl = string.Empty;

    [ObservableProperty]
    private bool _isBannerVisible;

    [ObservableProperty]
    private string _bannerVersionText = string.Empty;

    public UpdateBannerViewModel(
        IUpdateService updateService,
        IConfigManager configManager,
        IAppVersionProvider appVersionProvider,
        IBrowserLauncher browserLauncher)
    {
        _updateService = updateService;
        _configManager = configManager;
        _appVersionProvider = appVersionProvider;
        _browserLauncher = browserLauncher;
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
        BannerVersionText = version.ToString();
        IsBannerVisible = true;
    }

    [RelayCommand]
    private void ViewRelease() => _browserLauncher.Open(_releaseUrl);

    [RelayCommand]
    private void Later() => IsBannerVisible = false;

    [RelayCommand]
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
