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

using System.Diagnostics;
using System.IO;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Ssh;
using Heimdall.Sftp;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class AppStartupTests
{
    [Fact]
    public void ResolveNotesStoragePath_Uses_Default_Config_Notes_Directory()
    {
        var path = App.ResolveNotesStoragePath(new AppSettings(), @"C:\Heimdall");

        Assert.Equal(@"C:\Heimdall\config\notes", path);
    }

    [Fact]
    public void ResolveNotesStoragePath_Resolves_Relative_Notes_Directory_Against_BasePath()
    {
        var settings = new AppSettings
        {
            NotesDirectory = Path.Combine("custom", "notes"),
        };

        var path = App.ResolveNotesStoragePath(settings, @"C:\Heimdall");

        Assert.Equal(@"C:\Heimdall\custom\notes", path);
    }

    [Fact]
    public async Task PersistTrustedHostKeyAsync_Does_Not_Block_Caller_Before_Await()
    {
        var configManager = new DelayedMergeConfigManager();
        var stopwatch = Stopwatch.StartNew();

        var persistTask = App.PersistTrustedHostKeyAsync(configManager, "server:22", "sha256");

        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 150);
        Assert.False(persistTask.IsCompleted);

        configManager.ReleaseMerge();
        await persistTask;
    }

    [Fact]
    public async Task PersistTrustedHostKeyAsync_Swallows_MergeHostKey_Failures()
    {
        var configManager = new ThrowingConfigManager();

        await App.PersistTrustedHostKeyAsync(configManager, "server:22", "sha256");
    }

    [Fact]
    public async Task PersistRemovedHostKeyAsync_ReloadDoesNotRestoreLegacyOrMetadataEntry()
    {
        string rootPath = CreateTemporaryRoot();
        try
        {
            const string key = "removed.example.com:22";
            var entry = CreateHostKeyEntry(DateTimeOffset.UtcNow.AddDays(-1));
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();
            await configManager.MergeSettingAsync(settings =>
            {
                settings.TrustedHostKeys[key] = entry.Fingerprint;
                settings.TrustedHostKeysV2[key] = entry;
            });

            await App.PersistRemovedHostKeyAsync(configManager, key);

            AppSettings reloaded = await ReloadSettingsAsync(rootPath);
            Assert.DoesNotContain(key, reloaded.TrustedHostKeys);
            Assert.DoesNotContain(key, reloaded.TrustedHostKeysV2);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task HostKeyVerificationMutation_ReloadPreservesUpdatedLastSeen()
    {
        string rootPath = CreateTemporaryRoot();
        try
        {
            const string host = "ssh-refresh.example.com";
            const int port = 22;
            string key = $"{host}:{port}";
            DateTimeOffset originalLastSeen = DateTimeOffset.UtcNow.AddDays(-7);
            var original = CreateHostKeyEntry(originalLastSeen);
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();
            await App.PersistTrustedHostKeyEntryAsync(configManager, key, original);

            var store = new HostKeyStore();
            store.LoadEntriesFromConfig([(host, port, original)]);
            Task persistence = Task.CompletedTask;
            store.HostKeyEvent += (changedKey, _, trusted) =>
            {
                if (trusted && store.GetAllEntries().TryGetValue(changedKey, out var updated))
                {
                    persistence = App.PersistTrustedHostKeyEntryAsync(
                        configManager,
                        changedKey,
                        updated);
                }
            };
            var service = new HostKeyTrustService(store);

            HostKeyVerifyResult result =
                service.Verify(host, port, original.Fingerprint, "ssh-ed25519");
            await persistence;

            AppSettings reloaded = await ReloadSettingsAsync(rootPath);
            Assert.True(result.Trusted);
            Assert.True(reloaded.TrustedHostKeysV2[key].LastSeen > originalLastSeen);
            Assert.Equal("ssh-ed25519", reloaded.TrustedHostKeysV2[key].Algorithm);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task FtpsLastSeenMutation_ReloadPreservesUpdatedEntry()
    {
        string rootPath = CreateTemporaryRoot();
        try
        {
            const string host = "ftps-refresh.example.com";
            const int port = 990;
            string key = FtpsCertificateStore.MakeKey(host, port);
            DateTimeOffset originalLastSeen = DateTimeOffset.UtcNow.AddDays(-7);
            var original = new FtpsCertificateEntry(
                "SHA256:ftps",
                DateTimeOffset.UtcNow.AddDays(-30),
                originalLastSeen,
                "CN=ftps-refresh.example.com",
                "CN=Test CA",
                DateTimeOffset.UtcNow.AddDays(-30),
                DateTimeOffset.UtcNow.AddDays(30),
                FtpsCertificateSource.UserConfirmed);
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();
            await App.PersistTrustedFtpsCertificateEntryAsync(configManager, key, original);

            var store = new FtpsCertificateStore();
            store.LoadEntriesFromConfig(
            [
                new KeyValuePair<string, FtpsCertificateEntry>(key, original)
            ]);
            Task persistence = Task.CompletedTask;
            store.CertificateTrusted += (changedKey, updated) =>
            {
                persistence = App.PersistTrustedFtpsCertificateEntryAsync(
                    configManager,
                    changedKey,
                    updated);
            };

            store.RefreshLastSeen(host, port);
            await persistence;

            AppSettings reloaded = await ReloadSettingsAsync(rootPath);
            Assert.True(reloaded.TrustedFtpsCertificates[key].LastSeen > originalLastSeen);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static HostKeyEntry CreateHostKeyEntry(DateTimeOffset lastSeen)
        => new(
            "SHA256:host-key",
            DateTimeOffset.UtcNow.AddDays(-30),
            lastSeen,
            "ssh-rsa",
            HostKeySource.UserConfirmed);

    [Fact]
    public async Task PersistTrustedRdpCertificates_ReloadKeepsEveryCertificateOfTheProfile()
    {
        string rootPath = CreateTemporaryRoot();
        try
        {
            const string profileId = "profile-dc-pool";
            var stamp = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();

            await App.PersistTrustedRdpCertificatesAsync(
                configManager,
                profileId,
                [
                    new RdpCertificateEntry("SHA256:AA:BB:01", stamp),
                    new RdpCertificateEntry("SHA256:AA:BB:02", stamp.AddDays(1)),
                ]);

            // The round trip is the point of this lot. Without it the set is rebuilt from
            // nothing on every launch, so the question is asked again for every machine of
            // the pool and the feature is worth nothing.
            AppSettings reloaded = await ReloadSettingsAsync(rootPath);
            List<RdpCertificateEntry> stored = reloaded.TrustedRdpCertificates[profileId];
            Assert.Equal(2, stored.Count);
            Assert.Contains(stored, entry => entry.Thumbprint == "SHA256:AA:BB:01");
            Assert.Contains(stored, entry => entry.Thumbprint == "SHA256:AA:BB:02");

            // The stamp survives the file, so a settings screen can say since when.
            Assert.Equal(
                stamp,
                stored.Single(entry => entry.Thumbprint == "SHA256:AA:BB:01").FirstTrusted);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task PersistTrustedRdpCertificates_ForgettingTheLastOne_LeavesNoProfileKey()
    {
        string rootPath = CreateTemporaryRoot();
        try
        {
            const string profileId = "profile-to-empty";
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();
            await App.PersistTrustedRdpCertificatesAsync(
                configManager,
                profileId,
                [new RdpCertificateEntry("SHA256:AA:BB:01", DateTimeOffset.UtcNow)]);

            await App.PersistTrustedRdpCertificatesAsync(configManager, profileId, []);

            // An empty list left behind would be a profile that "has a trust set" holding
            // nothing - a distinction with no meaning that a settings screen would have to
            // render as an empty row.
            AppSettings reloaded = await ReloadSettingsAsync(rootPath);
            Assert.DoesNotContain(profileId, reloaded.TrustedRdpCertificates);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task PersistTrustedRdpCertificates_SwallowsMergeFailures()
    {
        var configManager = new ThrowingConfigManager();

        // Failing to remember a trust decision must not take the application down; the
        // user is asked again next time, which is the safe direction.
        await App.PersistTrustedRdpCertificatesAsync(
            configManager,
            "profile",
            [new RdpCertificateEntry("SHA256:AA:BB:01", DateTimeOffset.UtcNow)]);
    }

    private static string CreateTemporaryRoot()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "Heimdall-AppStartupTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        return rootPath;
    }

    private static async Task<AppSettings> ReloadSettingsAsync(string rootPath)
    {
        var reloadedManager = new ConfigManager(rootPath);
        await reloadedManager.InitializeAsync();
        return await reloadedManager.LoadSettingsAsync();
    }

    private sealed class DelayedMergeConfigManager : IConfigManager
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://settings.json";

        public string ServersPath => "mem://servers.json";

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings());

        public Task SaveSettingsAsync(AppSettings settings)
        {
            SettingsChanged?.Invoke(settings);
            return Task.CompletedTask;
        }

        public async Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint)
        {
            await _gate.Task;
            return true;
        }

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate) => Task.CompletedTask;

        public Task<List<ServerProfileDto>> LoadServersAsync() => Task.FromResult(new List<ServerProfileDto>());

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate) =>
            Task.FromResult(mutate([]));

        public Task SaveServersAsync(List<ServerProfileDto> servers) => Task.CompletedTask;

        public void ReleaseMerge()
        {
            _gate.TrySetResult(true);
        }
    }

    private sealed class ThrowingConfigManager : IConfigManager
    {
        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://settings.json";

        public string ServersPath => "mem://servers.json";

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings());

        public Task SaveSettingsAsync(AppSettings settings)
        {
            SettingsChanged?.Invoke(settings);
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            throw new InvalidOperationException("merge failed");

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate) => Task.CompletedTask;

        public Task<List<ServerProfileDto>> LoadServersAsync() => Task.FromResult(new List<ServerProfileDto>());

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate) =>
            Task.FromResult(mutate([]));

        public Task SaveServersAsync(List<ServerProfileDto> servers) => Task.CompletedTask;
    }
}
