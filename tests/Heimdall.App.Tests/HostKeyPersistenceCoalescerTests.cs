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
using Heimdall.Core.Configuration;
using Heimdall.Core.Ssh;
using Microsoft.Extensions.Time.Testing;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins finding B-12 of the SSH audit of 2026-09-06 on the App side: a known_hosts
/// sync raised one persistence event per line and each event was a full settings
/// write. Changes within a quiet window are written once, last change per key winning.
/// </summary>
public sealed class HostKeyPersistenceCoalescerTests
{
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task ManyChangesWithinTheQuietWindow_AreWrittenInOneMerge()
    {
        CountingConfigManager config = new();
        FakeTimeProvider clock = new();
        using HostKeyPersistenceCoalescer coalescer = new(config, clock, QuietWindow);

        for (int i = 0; i < 500; i++)
        {
            coalescer.Upsert($"host-{i}:22", Entry($"SHA256:{i}"));
        }

        Assert.Equal(0, config.MergeCount);
        clock.Advance(QuietWindow);
        await coalescer.LastFlush;

        Assert.Equal(1, config.MergeCount);
        AppSettings settings = await config.LoadSettingsAsync();
        Assert.Equal(500, settings.TrustedHostKeysV2.Count);
        Assert.Equal("SHA256:7", settings.TrustedHostKeys["host-7:22"]);
    }

    [Fact]
    public async Task EveryChangeRestartsTheQuietWindow()
    {
        CountingConfigManager config = new();
        FakeTimeProvider clock = new();
        using HostKeyPersistenceCoalescer coalescer = new(config, clock, QuietWindow);

        coalescer.Upsert("a:22", Entry("SHA256:a"));
        clock.Advance(QuietWindow / 2);
        coalescer.Upsert("b:22", Entry("SHA256:b"));
        clock.Advance(QuietWindow / 2);

        Assert.Equal(0, config.MergeCount);
        clock.Advance(QuietWindow / 2);
        await coalescer.LastFlush;

        Assert.Equal(1, config.MergeCount);
        Assert.Equal(2, (await config.LoadSettingsAsync()).TrustedHostKeysV2.Count);
    }

    [Fact]
    public async Task ARemovalAfterAnUpsertOfTheSameKey_Wins()
    {
        CountingConfigManager config = new();
        config.SetTrustedHostKey("gone:22", "SHA256:old");
        FakeTimeProvider clock = new();
        using HostKeyPersistenceCoalescer coalescer = new(config, clock, QuietWindow);

        coalescer.Upsert("gone:22", Entry("SHA256:new"));
        coalescer.Remove("gone:22");
        coalescer.UpsertFingerprint("legacy:22", "SHA256:legacy");
        clock.Advance(QuietWindow);
        await coalescer.LastFlush;

        AppSettings settings = await config.LoadSettingsAsync();
        Assert.False(settings.TrustedHostKeys.ContainsKey("gone:22"));
        Assert.False(settings.TrustedHostKeysV2.ContainsKey("gone:22"));
        Assert.Equal("SHA256:legacy", settings.TrustedHostKeys["legacy:22"]);
    }

    [Fact]
    public async Task FlushAsync_WritesWithoutWaitingForTheQuietWindow()
    {
        CountingConfigManager config = new();
        FakeTimeProvider clock = new();
        using HostKeyPersistenceCoalescer coalescer = new(config, clock, QuietWindow);

        coalescer.Upsert("now:22", Entry("SHA256:now"));
        await coalescer.FlushAsync();

        Assert.Equal(1, config.MergeCount);
        Assert.True((await config.LoadSettingsAsync()).TrustedHostKeysV2.ContainsKey("now:22"));
    }

    [Fact]
    public async Task ALegacyFingerprint_NeverOverwritesAnExistingEntry()
    {
        CountingConfigManager config = new();
        config.SetTrustedHostKey("kept:22", "SHA256:kept");
        FakeTimeProvider clock = new();
        using HostKeyPersistenceCoalescer coalescer = new(config, clock, QuietWindow);

        coalescer.UpsertFingerprint("kept:22", "SHA256:other");
        await coalescer.FlushAsync();

        Assert.Equal("SHA256:kept", (await config.LoadSettingsAsync()).TrustedHostKeys["kept:22"]);
    }

    private static HostKeyEntry Entry(string fingerprint) =>
        new(fingerprint, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "ssh-ed25519", HostKeySource.ImportedKnownHosts);

    /// <summary>Counts the merges an in-memory config manager receives.</summary>
    private sealed class CountingConfigManager : IConfigManager
    {
        private readonly InMemoryConfigManager _inner = new();
        private int _mergeCount;

        public int MergeCount => Volatile.Read(ref _mergeCount);

        public string ConfigPath => _inner.ConfigPath;

        public string SettingsPath => _inner.SettingsPath;

        public string ServersPath => _inner.ServersPath;

        public event Action<AppSettings>? SettingsChanged
        {
            add => _inner.SettingsChanged += value;
            remove => _inner.SettingsChanged -= value;
        }

        public void SetTrustedHostKey(string key, string fingerprint) => _inner.SetTrustedHostKey(key, fingerprint);

        public Task InitializeAsync() => _inner.InitializeAsync();

        public Task<AppSettings> LoadSettingsAsync() => _inner.LoadSettingsAsync();

        public Task SaveSettingsAsync(AppSettings settings) => _inner.SaveSettingsAsync(settings);

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) => _inner.MergeHostKeyAsync(hostPortKey, fingerprint);

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) => _inner.MergeTrustedHostKeysAsync(entries);

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            Interlocked.Increment(ref _mergeCount);
            return _inner.MergeSettingAsync(mutate);
        }

        public Task<List<ServerProfileDto>> LoadServersAsync() => _inner.LoadServersAsync();

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate) => _inner.MutateServersAsync(mutate);

        public Task SaveServersAsync(List<ServerProfileDto> servers) => _inner.SaveServersAsync(servers);
    }
}
