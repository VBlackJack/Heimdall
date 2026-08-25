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

namespace Heimdall.App.Tests;

/// <summary>
/// Forwards every call to a real <see cref="IConfigManager"/> and lets a test run code at a
/// chosen seam.
/// </summary>
/// <remarks>
/// Injecting the concurrent write is the only way to make a load-then-save window observable
/// without racing a real thread against it: the hook fires at a named point rather than at a
/// hoped-for moment, so the test measures the window rather than the scheduler. The hook runs
/// AFTER the inner call has returned, so the configuration write lock is already released and
/// a hook that writes cannot deadlock against the call it follows.
/// </remarks>
internal sealed class InterceptingConfigManager(IConfigManager inner) : IConfigManager
{
    private readonly IConfigManager _inner = inner;

    /// <summary>
    /// Runs once the settings snapshot has been handed to the caller - the instant a
    /// load-then-save window opens.
    /// </summary>
    public Action? AfterLoadSettings { get; set; }

    public string ConfigPath => _inner.ConfigPath;

    public string SettingsPath => _inner.SettingsPath;

    public string ServersPath => _inner.ServersPath;

    public event Action<AppSettings>? SettingsChanged
    {
        add => _inner.SettingsChanged += value;
        remove => _inner.SettingsChanged -= value;
    }

    public Task InitializeAsync() => _inner.InitializeAsync();

    public async Task<AppSettings> LoadSettingsAsync()
    {
        AppSettings settings = await _inner.LoadSettingsAsync();
        AfterLoadSettings?.Invoke();
        return settings;
    }

    public Task SaveSettingsAsync(AppSettings settings) => _inner.SaveSettingsAsync(settings);

    public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint)
        => _inner.MergeHostKeyAsync(hostPortKey, fingerprint);

    public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries)
        => _inner.MergeTrustedHostKeysAsync(entries);

    public Task MergeSettingAsync(Action<AppSettings> mutate) => _inner.MergeSettingAsync(mutate);

    public Task<List<ServerProfileDto>> LoadServersAsync() => _inner.LoadServersAsync();

    public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate)
        => _inner.MutateServersAsync(mutate);

    public Task SaveServersAsync(List<ServerProfileDto> servers) => _inner.SaveServersAsync(servers);
}
