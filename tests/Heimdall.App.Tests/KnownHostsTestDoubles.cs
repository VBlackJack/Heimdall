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

using Heimdall.Core.Configuration;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;

internal sealed class InMemoryConfigManager : IConfigManager
{
    private AppSettings _settings = new();
    private List<ServerProfileDto> _servers = [];

    public string ConfigPath => "mem://config";

    public string SettingsPath => "mem://settings.json";

    public string ServersPath => "mem://servers.json";

    public event Action<AppSettings>? SettingsChanged;

    public Action? BeforeMergeTrustedHostKeys { get; set; }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(CloneSettings(_settings));

    public Task SaveSettingsAsync(AppSettings settings)
    {
        _settings = CloneSettings(settings);
        SettingsChanged?.Invoke(CloneSettings(_settings));
        return Task.CompletedTask;
    }

    public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint)
    {
        if (_settings.TrustedHostKeys.ContainsKey(hostPortKey))
        {
            return Task.FromResult(false);
        }

        _settings.TrustedHostKeys[hostPortKey] = fingerprint;
        return Task.FromResult(true);
    }

    public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries)
    {
        BeforeMergeTrustedHostKeys?.Invoke();

        var added = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            if (_settings.TrustedHostKeys.ContainsKey(entry.Key))
            {
                continue;
            }

            _settings.TrustedHostKeys[entry.Key] = entry.Value;
            added++;
        }

        return Task.FromResult(added);
    }

    public Task MergeSettingAsync(Action<AppSettings> mutate)
    {
        mutate(_settings);
        return Task.CompletedTask;
    }

    public Task<List<ServerProfileDto>> LoadServersAsync() => Task.FromResult(new List<ServerProfileDto>(_servers));

    public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate)
    {
        var servers = new List<ServerProfileDto>(_servers);
        TResult result = mutate(servers);
        _servers = servers;
        return Task.FromResult(result);
    }

    public Task SaveServersAsync(List<ServerProfileDto> servers)
    {
        _servers = new List<ServerProfileDto>(servers);
        return Task.CompletedTask;
    }

    public void SetTrustedHostKey(string key, string fingerprint)
    {
        _settings.TrustedHostKeys[key] = fingerprint;
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        return new AppSettings
        {
            TrustedHostKeys = new Dictionary<string, string>(settings.TrustedHostKeys, StringComparer.Ordinal),
            TrustedHostKeysV2 = new Dictionary<string, HostKeyEntry>(settings.TrustedHostKeysV2, StringComparer.Ordinal)
        };
    }
}
