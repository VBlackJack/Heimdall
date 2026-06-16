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

using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Updates;

namespace Heimdall.App.Tests;

public sealed class UpdateBannerViewModelTests
{
    private const string Current = "2026.061501";
    private const string Newer = "2026.061502";

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
        var vm = new UpdateBannerViewModel(update, new StubConfigManager(settings), new AppVersionProvider(Current), browser);
        await vm.CheckOnStartupAsync(CancellationToken.None);

        vm.ViewReleaseCommand.Execute(null);

        Assert.Equal($"https://github.com/VBlackJack/Heimdall/releases/tag/v{Newer}", browser.OpenedUrl);
    }

    private static UpdateBannerViewModel CreateViewModel(AppSettings settings, StubUpdateService update, string informationalVersion)
        => new(update, new StubConfigManager(settings), new AppVersionProvider(informationalVersion), new StubBrowserLauncher());

    private static AppSettings BaseSettings() => new()
    {
        UpdateCheckEnabled = true,
        UpdateCheckIntervalHours = 24,
        UpdateLastCheckUtc = null,
        UpdateSkippedVersion = null,
        UpdateRepositoryOwner = "VBlackJack",
        UpdateRepositoryName = "Heimdall"
    };

    private static UpdateCheckResult UpToDate()
        => new(UpdateCheckStatus.UpToDate, null);

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

        public Task<UpdateCheckResult> CheckForUpdatesAsync(HeimdallVersion current, string owner, string repo, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(Result);
        }

        public Task<string> DownloadVerifiedAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken)
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

        public Task SaveServersAsync(List<ServerProfileDto> servers) => throw new NotSupportedException();
    }
}
