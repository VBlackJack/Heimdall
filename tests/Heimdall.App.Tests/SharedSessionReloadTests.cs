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
using System.Text;
using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Microsoft.Extensions.Time.Testing;

namespace Heimdall.App.Tests;

/// <summary>
/// A shared source read again when its file changes, kept when the change cannot be taken, and
/// served from its offline copy when the file cannot be read; over temporary directories only.
/// </summary>
public sealed class SharedSessionReloadTests : IDisposable
{
    private const string SourceId = "reloadsrc1";
    private const string SourceName = "Team";

    // Anonymized: fake hosts only.
    private const string TwoSessions = """
        [Bookmarks]
        SubRep=
        ImgNum=42
        [Bookmarks_1]
        SubRep=PROD
        ImgNum=41
        srv-a01= #109#0%srv-a01.example.test%22%jdoe%%
        srv-a02= #109#0%srv-a02.example.test%22%jdoe%%
        """;

    private const string ThreeSessions = """
        [Bookmarks]
        SubRep=
        ImgNum=42
        [Bookmarks_1]
        SubRep=PROD
        ImgNum=41
        srv-a01= #109#0%srv-a01.example.test%22%jdoe%%
        srv-a02= #109#0%srv-a02.example.test%22%jdoe%%
        srv-a03= #109#0%srv-a03.example.test%22%jdoe%%
        """;

    private const string NoSession = """
        [Bookmarks]
        SubRep=
        ImgNum=42
        """;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "heimdall-shared-reload-tests",
        Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _disposables = [];

    public SharedSessionReloadTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string SourcePath => Path.Combine(_directory, "share", "team.mxtsessions");

    private string CacheDirectory => Path.Combine(_directory, "cache");

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // --- Reload of one source ---

    [Fact]
    public async Task ReloadSource_ReadsTheChangedFile_AndSaysSo()
    {
        WriteSource(TwoSessions);
        SharedSessionProvider provider = await LoadedProviderAsync();
        List<SharedSessionSourceState> reloaded = [];
        provider.SourceReloaded += reload => reloaded.Add(reload.State);

        WriteSource(ThreeSessions);
        SharedSessionSourceState? state = await provider.ReloadSourceAsync(SourceId);

        Assert.NotNull(state);
        Assert.Equal(3, state.ProfileCount);
        Assert.Null(state.Notice);
        Assert.Equal(3, provider.Profiles.Count);
        Assert.Same(state, Assert.Single(reloaded));
    }

    [Fact]
    public async Task LockedFile_IsRetried_ThenThePreviousSessionsAreKept_WithTheReason()
    {
        WriteSource(TwoSessions);
        LocalizationManager localizer = await LocalizerAsync();
        SharedSessionProvider provider = await LoadedProviderAsync(localizer, Settings(retryCount: 2, retryDelayMs: 100));
        string[] before = [.. provider.Profiles.Select(profile => profile.Id)];

        SharedSessionSourceState? state;
        using (new FileStream(SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            state = await provider.ReloadSourceAsync(SourceId);
        }

        Assert.NotNull(state);
        Assert.Equal(SharedSessionSourceStatus.Loaded, state.Status);
        Assert.Equal(2, state.ProfileCount);
        Assert.StartsWith(
            localizer.Format("SharedSessionSourceNoticeKeptPrevious", string.Empty).TrimEnd(),
            state.Notice,
            StringComparison.Ordinal);
        Assert.Equal(before, provider.Profiles.Select(profile => profile.Id));
    }

    [Fact]
    public async Task LockedFile_ReleasedWhileRetrying_IsRead()
    {
        WriteSource(TwoSessions);
        SharedSessionProvider provider = await LoadedProviderAsync(settings: Settings(retryCount: 10, retryDelayMs: 150));
        var locked = new FileStream(SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Task<SharedSessionSourceState?> reload = provider.ReloadSourceAsync(SourceId);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.False(reload.IsCompleted, "the reload gave up while the file was still locked");
        byte[] three = Encoding.UTF8.GetBytes(ThreeSessions);
        locked.SetLength(0);
        locked.Write(three);
        await locked.DisposeAsync();

        SharedSessionSourceState? state = await reload.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, state!.ProfileCount);
        Assert.Null(state.Notice);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("garbage")]
    public async Task ChangeThatLosesEverySession_KeepsThePreviousSessions(string change)
    {
        WriteSource(TwoSessions);
        LocalizationManager localizer = await LocalizerAsync();
        SharedSessionProvider provider = await LoadedProviderAsync(localizer);

        if (change == "empty")
        {
            WriteSource(NoSession);
        }
        else
        {
            File.WriteAllBytes(SourcePath, [0x00, 0xFF, 0xFE, 0x00, 0x13, 0x37, 0x00, 0x00]);
        }

        SharedSessionSourceState? state = await provider.ReloadSourceAsync(SourceId);

        Assert.Equal(2, state!.ProfileCount);
        Assert.Equal(2, provider.Profiles.Count);
        Assert.NotNull(state.Notice);
        Assert.StartsWith(
            localizer.Format("SharedSessionSourceNoticeKeptPrevious", string.Empty).TrimEnd(),
            state.Notice,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatWasAlwaysEmpty_LoadsEmpty_WithoutANotice()
    {
        WriteSource(NoSession);
        SharedSessionProvider provider = await LoadedProviderAsync();

        SharedSessionSourceState state = Assert.Single(provider.Sources);

        Assert.Equal(SharedSessionSourceStatus.Loaded, state.Status);
        Assert.Equal(0, state.ProfileCount);
        Assert.Null(state.Notice);
    }

    // --- Offline copy ---

    [Fact]
    public async Task Load_WritesTheOfflineCopy_WithTheBytesAndWhereTheyCameFrom()
    {
        WriteSource(TwoSessions);
        await LoadedProviderAsync();
        var cache = new SharedSessionSourceCache(CacheDirectory);

        SharedSessionSourceCacheEntry? copy = await cache.TryReadAsync(SourceId);

        Assert.NotNull(copy);
        Assert.Equal(File.ReadAllBytes(SourcePath), copy.Content);
        Assert.Equal(SourcePath, copy.OriginalPath);
        Assert.True(File.Exists(cache.PathFor(SourceId)));
        Assert.StartsWith(CacheDirectory, cache.PathFor(SourceId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFile_AtStartup_IsServedFromTheOfflineCopy()
    {
        WriteSource(TwoSessions);
        SharedSessionProvider first = await LoadedProviderAsync();
        string[] ids = [.. first.Profiles.Select(profile => profile.Id).Order()];
        File.Delete(SourcePath);

        // A new process: nothing in memory, only the copy on disk.
        SharedSessionProvider second = await LoadedProviderAsync();

        SharedSessionSourceState state = Assert.Single(second.Sources);
        Assert.Equal(SharedSessionSourceStatus.Loaded, state.Status);
        Assert.True(state.IsOfflineCopy);
        Assert.NotNull(state.Notice);
        Assert.Equal(ids, second.Profiles.Select(profile => profile.Id).Order());
    }

    [Fact]
    public async Task MissingFile_WithoutAnOfflineCopy_IsAnError()
    {
        SharedSessionProvider provider = await LoadedProviderAsync();

        SharedSessionSourceState state = Assert.Single(provider.Sources);

        Assert.Equal(SharedSessionSourceStatus.Error, state.Status);
        Assert.Empty(provider.Profiles);
    }

    [Fact]
    public async Task OfflineCopy_GoesThroughTheParserAndTheSanitizerAgain()
    {
        // A copy rewritten on this disk, hash and all: a key path on a file share and a session
        // type that is not SSH or RDP. The copy is trusted no more than the file it stands for.
        const string tampered = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            srv-k01= #109#0%srv-k01.example.test%22%jdoe%%\\files.example.test\share\id_rsa
            srv-t01= #98#1%srv-t01.example.test%23%%%2%%
            srv-a01= #109#0%srv-a01.example.test%22%jdoe%%
            """;
        var cache = new SharedSessionSourceCache(CacheDirectory);
        await cache.StoreAsync(SourceId, SourcePath, Encoding.UTF8.GetBytes(tampered), DateTime.UtcNow);

        SharedSessionProvider provider = await LoadedProviderAsync();

        SharedSessionSourceState state = Assert.Single(provider.Sources);
        Assert.True(state.IsOfflineCopy);
        Assert.All(provider.Profiles, profile =>
        {
            Assert.Null(profile.SshKeyPath);
            Assert.Contains(profile.ConnectionType, new[] { "SSH", "RDP" });
            Assert.StartsWith(SharedSessionIds.PrefixFor(SourceId), profile.Id, StringComparison.Ordinal);
            Assert.False(profile.ExecutionConfirmed);
        });
        Assert.DoesNotContain(provider.Profiles, profile => profile.DisplayName == "srv-t01");
    }

    [Fact]
    public async Task OfflineCopy_WhoseBytesDoNotMatchItsHash_IsIgnored()
    {
        WriteSource(TwoSessions);
        await LoadedProviderAsync();
        var cache = new SharedSessionSourceCache(CacheDirectory);
        string document = File.ReadAllText(cache.PathFor(SourceId));
        string original = Convert.ToBase64String(File.ReadAllBytes(SourcePath));
        File.WriteAllText(
            cache.PathFor(SourceId),
            document.Replace(original, Convert.ToBase64String(Encoding.UTF8.GetBytes(ThreeSessions)), StringComparison.Ordinal));
        File.Delete(SourcePath);

        SharedSessionProvider provider = await LoadedProviderAsync();

        Assert.Equal(SharedSessionSourceStatus.Error, Assert.Single(provider.Sources).Status);
    }

    [Fact]
    public async Task RemovingASource_DeletesItsOfflineCopy_DisablingItKeepsIt()
    {
        WriteSource(TwoSessions);
        SharedSessionProvider provider = await LoadedProviderAsync();
        var cache = new SharedSessionSourceCache(CacheDirectory);
        Assert.True(File.Exists(cache.PathFor(SourceId)));

        AppSettings disabled = Settings();
        disabled.SharedSessionSources[0].Enabled = false;
        await provider.LoadAsync(disabled);
        Assert.True(File.Exists(cache.PathFor(SourceId)));

        AppSettings removed = Settings();
        removed.SharedSessionSources.Clear();
        await provider.LoadAsync(removed);

        Assert.False(File.Exists(cache.PathFor(SourceId)));
    }

    // --- Watching ---

    [Fact]
    public async Task Watcher_ReportsASource_OnceItsFileIsQuiet()
    {
        var clock = new FakeTimeProvider();
        List<string> reported = [];
        var watcher = Track(new SharedSessionSourceWatcher(
            sourceId =>
            {
                reported.Add(sourceId);
                return Task.CompletedTask;
            },
            clock,
            watchFileSystem: false));
        watcher.Update(new Dictionary<string, string> { [SourceId] = SourcePath }, TimeSpan.FromSeconds(1));

        watcher.NotifyChanged(SourceId);
        clock.Advance(TimeSpan.FromMilliseconds(600));
        watcher.NotifyChanged(SourceId);
        clock.Advance(TimeSpan.FromMilliseconds(600));
        watcher.NotifyChanged(SourceId);
        clock.Advance(TimeSpan.FromMilliseconds(900));
        await Task.Yield();

        Assert.Empty(reported);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        await WaitUntilAsync(() => reported.Count == 1);

        Assert.Equal(new[] { SourceId }, reported);
    }

    [Fact]
    public async Task Watcher_FolderMissingAtStart_IsWatchedOnceItAppears_AndTheSourceIsReadAgain()
    {
        // A share mounted after the start, a sync client that has not created the folder yet.
        var clock = new FakeTimeProvider();
        List<string> reported = [];
        string later = Path.Combine(_directory, "mounted-later", "team.mxtsessions");
        var watcher = Track(new SharedSessionSourceWatcher(
            sourceId =>
            {
                lock (reported)
                {
                    reported.Add(sourceId);
                }

                return Task.CompletedTask;
            },
            clock));
        watcher.Update(new Dictionary<string, string> { [SourceId] = later }, TimeSpan.FromSeconds(1));

        Assert.Empty(watcher.WatchedPaths);
        Assert.Equal(new[] { SourceId }, watcher.PendingPaths.Keys);

        clock.Advance(SharedSessionSourceWatcher.RewatchInterval);
        Assert.Empty(watcher.WatchedPaths);

        Directory.CreateDirectory(Path.GetDirectoryName(later)!);
        File.WriteAllText(later, TwoSessions);
        clock.Advance(SharedSessionSourceWatcher.RewatchInterval);

        Assert.Equal(new[] { SourceId }, watcher.WatchedPaths.Keys);
        Assert.Empty(watcher.PendingPaths);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() =>
        {
            lock (reported)
            {
                return reported.Count >= 1;
            }
        });
    }

    [Fact]
    public async Task Watcher_AfterAWatchError_IsWatchedAgain_AndTheSourceIsReadAgain()
    {
        // A network disconnection or an overflowing buffer: the watcher reports once, then nothing.
        WriteSource(TwoSessions);
        var clock = new FakeTimeProvider();
        int reports = 0;
        var watcher = Track(new SharedSessionSourceWatcher(
            _ =>
            {
                Interlocked.Increment(ref reports);
                return Task.CompletedTask;
            },
            clock));
        watcher.Update(new Dictionary<string, string> { [SourceId] = SourcePath }, TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { SourceId }, watcher.WatchedPaths.Keys);

        watcher.OnWatchFailed(SourceId, failed: null, "The network name is no longer available.");

        Assert.Empty(watcher.WatchedPaths);
        Assert.Equal(new[] { SourceId }, watcher.PendingPaths.Keys);

        clock.Advance(SharedSessionSourceWatcher.RewatchInterval);
        Assert.Equal(new[] { SourceId }, watcher.WatchedPaths.Keys);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Volatile.Read(ref reports) >= 1);
    }

    [Fact]
    public async Task Watcher_DetectsAnAtomicSave_AndAnInPlaceWrite()
    {
        WriteSource(TwoSessions);
        int reports = 0;
        var watcher = Track(new SharedSessionSourceWatcher(_ =>
        {
            Interlocked.Increment(ref reports);
            return Task.CompletedTask;
        }));
        watcher.Update(new Dictionary<string, string> { [SourceId] = SourcePath }, TimeSpan.FromMilliseconds(250));

        // An editor or a sync client: write a temporary file next to it, rename it over.
        string temporary = SourcePath + ".tmp";
        File.WriteAllText(temporary, ThreeSessions);
        File.Move(temporary, SourcePath, overwrite: true);
        await WaitUntilAsync(() => Volatile.Read(ref reports) >= 1);

        int afterRename = Volatile.Read(ref reports);
        File.AppendAllText(SourcePath, Environment.NewLine);
        await WaitUntilAsync(() => Volatile.Read(ref reports) > afterRename);
    }

    [Fact]
    public async Task Provider_FollowsTheSources_AndSkipsAFolderThatDoesNotExist()
    {
        WriteSource(TwoSessions);
        string otherPath = Path.Combine(_directory, "share", "other.mxtsessions");
        File.WriteAllText(otherPath, TwoSessions);
        string missingFolderPath = Path.Combine(_directory, "not-synced-yet", "team.mxtsessions");
        SharedSessionProvider provider = Track(new SharedSessionProvider(
            await LocalizerAsync(),
            new SharedSessionSourceCache(CacheDirectory),
            watchSourceFiles: true));
        AppSettings settings = Settings();
        settings.SharedSessionSources.Add(new SharedSessionSourceDto { Id = "othersrc1", Name = "Other", Path = otherPath, Enabled = false });
        settings.SharedSessionSources.Add(new SharedSessionSourceDto { Id = "missingsrc1", Name = "Later", Path = missingFolderPath });

        await provider.LoadAsync(settings);

        Assert.Equal(new[] { SourceId }, provider.Watcher!.WatchedPaths.Keys);

        settings.SharedSessionSources.RemoveAt(0);
        settings.SharedSessionSources[0].Enabled = true;
        await provider.LoadAsync(settings);

        Assert.Equal(new[] { "othersrc1" }, provider.Watcher.WatchedPaths.Keys);

        provider.Dispose();

        Assert.Empty(provider.Watcher.WatchedPaths);
    }

    [Fact]
    public async Task Provider_ReloadsTheSource_WhenItsFileChanges()
    {
        WriteSource(TwoSessions);
        SharedSessionProvider provider = Track(new SharedSessionProvider(
            await LocalizerAsync(),
            new SharedSessionSourceCache(CacheDirectory),
            watchSourceFiles: true));
        AppSettings settings = Settings();
        settings.SharedSourceReloadDebounceMs = 250;
        await provider.LoadAsync(settings);
        var reloaded = new TaskCompletionSource<SharedSessionSourceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.SourceReloaded += reload => reloaded.TrySetResult(reload.State);

        WriteSource(ThreeSessions);

        SharedSessionSourceState state = await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(3, state.ProfileCount);
        Assert.Equal(3, provider.Profiles.Count);
    }

    [Fact]
    public async Task Provider_ApplyReloadDebounce_ReachesTheWatch_WithoutReadingAFile()
    {
        WriteSource(TwoSessions);
        SharedSessionProvider provider = Track(new SharedSessionProvider(
            await LocalizerAsync(),
            new SharedSessionSourceCache(CacheDirectory),
            watchSourceFiles: true));
        int reads = 0;
        Func<string, CancellationToken, Task<byte[]?>> realReader = provider.SourceFileReader;
        provider.SourceFileReader = (path, token) =>
        {
            Interlocked.Increment(ref reads);
            return realReader(path, token);
        };
        AppSettings settings = Settings();
        settings.SharedSourceReloadDebounceMs = 1500;
        await provider.LoadAsync(settings);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), provider.Watcher!.Debounce);
        int readsAfterLoad = Volatile.Read(ref reads);

        settings.SharedSourceReloadDebounceMs = 4000;
        provider.ApplyReloadDebounce(settings);

        Assert.Equal(TimeSpan.FromMilliseconds(4000), provider.Watcher.Debounce);
        Assert.Equal(readsAfterLoad, Volatile.Read(ref reads));
    }

    [Fact]
    public async Task Provider_ADebounceAppliedWhileALoadIsUnderway_IsNotUndoneByThatLoad()
    {
        // The settings card saves the sources (a load starts, reading a share) and then only the
        // debounce: the load, getting to its watches last, must not bring back its own value.
        WriteSource(TwoSessions);
        SharedSessionProvider provider = Track(new SharedSessionProvider(
            await LocalizerAsync(),
            new SharedSessionSourceCache(CacheDirectory),
            watchSourceFiles: true));
        await provider.LoadAsync(Settings());
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, CancellationToken, Task<byte[]?>> realReader = provider.SourceFileReader;
        provider.SourceFileReader = async (path, token) =>
        {
            reading.TrySetResult();
            await release.Task;
            return await realReader(path, token);
        };
        AppSettings loaded = Settings();
        loaded.SharedSourceReloadDebounceMs = 1000;
        Task load = provider.LoadAsync(loaded);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(15));

        AppSettings applied = Settings();
        applied.SharedSourceReloadDebounceMs = 5000;
        provider.ApplyReloadDebounce(applied);
        release.SetResult();
        await load.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromMilliseconds(5000), provider.Watcher!.Debounce);
    }

    // --- Helpers ---

    private void WriteSource(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SourcePath)!);
        File.WriteAllText(SourcePath, content);
    }

    private async Task<SharedSessionProvider> LoadedProviderAsync(
        LocalizationManager? localizer = null,
        AppSettings? settings = null)
    {
        SharedSessionProvider provider = Track(new SharedSessionProvider(
            localizer ?? await LocalizerAsync(),
            new SharedSessionSourceCache(CacheDirectory)));
        await provider.LoadAsync(settings ?? Settings());
        return provider;
    }

    private AppSettings Settings(int retryCount = 0, int retryDelayMs = 100)
    {
        var settings = new AppSettings
        {
            SharedSourceReloadRetryCount = retryCount,
            SharedSourceReloadRetryDelayMs = retryDelayMs,
        };
        settings.SharedSessionSources.Add(new SharedSessionSourceDto
        {
            Id = SourceId,
            Name = SourceName,
            Path = SourcePath,
            Enabled = true,
        });
        return settings;
    }

    private T Track<T>(T disposable)
        where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private static async Task<LocalizationManager> LocalizerAsync()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition was not met in time");
            await Task.Delay(25);
        }
    }
}
