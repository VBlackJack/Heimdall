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

using System.Collections.Concurrent;
using System.IO;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed partial class ServerListSelectionTests
{
    /// <summary>
    /// Failure backstop for an expand-state persistence signal to arrive. The waits
    /// it bounds are already event-driven, so this value is not a synchronisation
    /// point: it only has to be generous enough that a saturated thread pool cannot
    /// exhaust it before the background merge is scheduled at all. Its cost is paid
    /// only when the test genuinely fails.
    /// </summary>
    private static readonly TimeSpan ExpandStatePersistBackstop = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ExpandStatePersist_InFlightMutationUsesCapturedSnapshots()
    {
        var configManager = new RecordingConfigManager(blockFirstMerge: true);
        await using var fixture = await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("alpha", "Alpha", "alpha"),
            CreateServer("beta", "Beta", "beta"));

        fixture.FolderByPath("alpha").IsExpanded = true;
        await configManager.FirstMergeStarted.WaitAsync(ExpandStatePersistBackstop);

        fixture.FolderByPath("alpha").IsExpanded = false;
        fixture.FolderByPath("beta").IsExpanded = true;
        configManager.ReleaseFirstMerge();

        await configManager.WaitForMergeCountAsync(2);

        Assert.Collection(
            configManager.PersistedSnapshots,
            snapshot => Assert.Equal(["alpha"], snapshot),
            snapshot => Assert.Equal(["beta"], snapshot));
        Assert.Equal(["beta"], configManager.Settings.TreeExpandedNodes);
    }

    [Fact]
    public async Task ExpandStatePersist_UsesAtomicMergeAndPreservesUnrelatedSetting()
    {
        var configManager = new RecordingConfigManager();
        configManager.Settings.DefaultTheme = "Vesper";
        await using var fixture = await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("alpha", "Alpha", "alpha"));

        fixture.FolderByPath("alpha").IsExpanded = true;
        await configManager.WaitForMergeCountAsync(1);

        Assert.Equal(0, configManager.LoadSettingsCallCount);
        Assert.Equal(0, configManager.SaveSettingsCallCount);
        Assert.Equal("Vesper", configManager.Settings.DefaultTheme);
        Assert.Equal(["alpha"], configManager.Settings.TreeExpandedNodes);
    }

    [Fact]
    public async Task ExpandStatePersist_RapidTogglesAreDebouncedToLatestSnapshot()
    {
        var configManager = new RecordingConfigManager();
        await using var fixture = await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("alpha", "Alpha", "alpha"),
            CreateServer("beta", "Beta", "beta"));

        fixture.FolderByPath("alpha").IsExpanded = true;
        fixture.FolderByPath("alpha").IsExpanded = false;
        fixture.FolderByPath("beta").IsExpanded = true;

        await configManager.WaitForMergeCountAsync(1);
        await Task.Delay(750);

        Assert.Equal(1, configManager.MergeSettingCallCount);
        Assert.Equal(["beta"], Assert.Single(configManager.PersistedSnapshots));
    }

    [Fact]
    public async Task LoadServers_RestoresExpandedNodesWithoutSchedulingPersistence()
    {
        var configManager = new RecordingConfigManager();
        await using var fixture = await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        fixture.LoadServers(
            new AppSettings { TreeExpandedNodes = ["alpha"] },
            CreateServer("alpha", "Alpha", "alpha"),
            CreateServer("beta", "Beta", "beta"));

        Assert.True(fixture.FolderByPath("alpha").IsExpanded);
        Assert.False(fixture.FolderByPath("beta").IsExpanded);
        Assert.Equal(0, configManager.MergeSettingCallCount);
    }

    [Fact]
    public async Task ExpandStateCloseFlush_PendingDebouncePersistsLatestSnapshotExactlyOnce()
    {
        RecordingConfigManager configManager = new();
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("alpha", "Alpha", "alpha"),
            CreateServer("beta", "Beta", "beta"));

        fixture.FolderByPath("alpha").IsExpanded = true;
        fixture.FolderByPath("alpha").IsExpanded = false;
        fixture.FolderByPath("beta").IsExpanded = true;

        await fixture.ViewModel.FlushExpandStateForCloseAsync();
        await Task.Delay(750);

        Assert.Equal(1, configManager.MergeSettingCallCount);
        Assert.Equal(["beta"], Assert.Single(configManager.PersistedSnapshots));
    }

    [Fact]
    public async Task ExpandStateCloseFlush_InFlightSaveIsDrainedWithoutDuplicateWrite()
    {
        RecordingConfigManager configManager = new(blockFirstMerge: true);
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("alpha", "Alpha", "alpha"));

        fixture.FolderByPath("alpha").IsExpanded = true;
        await configManager.FirstMergeStarted.WaitAsync(ExpandStatePersistBackstop);

        Task flush = fixture.ViewModel.FlushExpandStateForCloseAsync();

        Assert.False(flush.IsCompleted);
        configManager.ReleaseFirstMerge();
        await flush;
        Assert.Equal(1, configManager.MergeSettingCallCount);
        Assert.Equal(["alpha"], Assert.Single(configManager.PersistedSnapshots));
    }

    [Fact]
    public async Task ExpandStateCloseFlush_NoPendingState_DoesNotWrite()
    {
        RecordingConfigManager configManager = new();
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("alpha", "Alpha", "alpha"));

        await fixture.ViewModel.FlushExpandStateForCloseAsync();

        Assert.Equal(0, configManager.MergeSettingCallCount);
        Assert.Empty(configManager.PersistedSnapshots);
    }

    [Fact]
    public async Task ExpandStateCloseFlush_PersistenceFailureIsBestEffort()
    {
        RecordingConfigManager configManager = new(throwOnMerge: true);
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("alpha", "Alpha", "alpha"));
        fixture.FolderByPath("alpha").IsExpanded = true;

        await fixture.ViewModel.FlushExpandStateForCloseAsync();

        Assert.Equal(1, configManager.MergeSettingCallCount);
        Assert.Empty(configManager.PersistedSnapshots);
    }

    [Fact]
    public async Task ExpandStateCloseFlush_FirstMergeFails_LatestPendingSnapshotStillPersists()
    {
        RecordingConfigManager configManager = new(failFirstMerge: true);
        await using ServerListSelectionFixture fixture =
            await ServerListSelectionFixture.CreateAsync(configManager: configManager);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("alpha", "Alpha", "alpha"),
            CreateServer("beta", "Beta", "beta"));

        fixture.FolderByPath("alpha").IsExpanded = true;
        await configManager.FirstMergeStarted.WaitAsync(ExpandStatePersistBackstop);
        fixture.FolderByPath("alpha").IsExpanded = false;
        fixture.FolderByPath("beta").IsExpanded = true;

        await fixture.ViewModel.FlushExpandStateForCloseAsync();

        Assert.Equal(2, configManager.MergeSettingCallCount);
        Assert.Equal(["beta"], Assert.Single(configManager.PersistedSnapshots));
        Assert.Equal(["beta"], configManager.Settings.TreeExpandedNodes);
    }

    private sealed class RecordingConfigManager(
        bool blockFirstMerge = false,
        bool throwOnMerge = false,
        bool failFirstMerge = false) : IConfigManager
    {
        private readonly SemaphoreSlim _mergeLock = new(1, 1);
        private readonly TaskCompletionSource _firstMergeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstMergeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string[]> _persistedSnapshots = new();
        private readonly TaskCompletionSource _secondMergeCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadSettingsCallCount;
        private int _mergeSettingCallCount;
        private int _saveSettingsCallCount;

        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://settings.json";

        public string ServersPath => "mem://servers.json";

        public AppSettings Settings { get; private set; } = new();

        public int LoadSettingsCallCount => Volatile.Read(ref _loadSettingsCallCount);

        public int MergeSettingCallCount => Volatile.Read(ref _mergeSettingCallCount);

        public int SaveSettingsCallCount => Volatile.Read(ref _saveSettingsCallCount);

        public IReadOnlyList<string[]> PersistedSnapshots => [.. _persistedSnapshots];

        public Task FirstMergeStarted => _firstMergeStarted.Task;

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync()
        {
            Interlocked.Increment(ref _loadSettingsCallCount);
            return Task.FromResult(Settings);
        }

        public Task SaveSettingsAsync(AppSettings settings)
        {
            Interlocked.Increment(ref _saveSettingsCallCount);
            Settings = settings;
            SettingsChanged?.Invoke(Settings);
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public async Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            int callNumber = Interlocked.Increment(ref _mergeSettingCallCount);
            await _mergeLock.WaitAsync();
            try
            {
                if (callNumber == 1)
                {
                    _firstMergeStarted.TrySetResult();
                    if (blockFirstMerge)
                    {
                        await _firstMergeRelease.Task;
                    }
                }

                if (throwOnMerge || (failFirstMerge && callNumber == 1))
                {
                    throw new IOException("Simulated expand-state persistence failure.");
                }

                mutate(Settings);
                _persistedSnapshots.Enqueue([.. Settings.TreeExpandedNodes]);
                SettingsChanged?.Invoke(Settings);
                if (callNumber == 2)
                {
                    _secondMergeCompleted.TrySetResult();
                }
            }
            finally
            {
                _mergeLock.Release();
            }
        }

        public Task<List<ServerProfileDto>> LoadServersAsync() =>
            Task.FromResult<List<ServerProfileDto>>([]);

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate) =>
            Task.FromResult(mutate([]));

        public Task SaveServersAsync(List<ServerProfileDto> servers) => Task.CompletedTask;

        public void ReleaseFirstMerge() => _firstMergeRelease.TrySetResult();

        public async Task WaitForMergeCountAsync(int expectedCount)
        {
            if (expectedCount == 1)
            {
                await _firstMergeStarted.Task.WaitAsync(ExpandStatePersistBackstop);

                // Bounded so an absent snapshot fails the test instead of hanging
                // it. An unbounded poll here would stall the whole CI job rather
                // than reporting one failure.
                long deadline = Environment.TickCount64 + (long)ExpandStatePersistBackstop.TotalMilliseconds;
                while (_persistedSnapshots.IsEmpty)
                {
                    Assert.True(
                        Environment.TickCount64 < deadline,
                        "No expand-state snapshot was persisted before the backstop elapsed.");
                    await Task.Delay(10);
                }

                return;
            }

            Assert.Equal(2, expectedCount);
            await _secondMergeCompleted.Task.WaitAsync(ExpandStatePersistBackstop);
        }
    }
}
