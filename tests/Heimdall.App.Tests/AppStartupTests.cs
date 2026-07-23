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
using Heimdall.Core.Configuration;

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
