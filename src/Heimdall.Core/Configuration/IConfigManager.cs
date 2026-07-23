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

namespace Heimdall.Core.Configuration;

/// <summary>
/// Abstraction over application configuration persistence (settings + server profiles).
/// </summary>
public interface IConfigManager
{
    /// <summary>
    /// Path to the config directory.
    /// </summary>
    string ConfigPath { get; }

    /// <summary>
    /// Path to the runtime settings file.
    /// </summary>
    string SettingsPath { get; }

    /// <summary>
    /// Path to the runtime servers file.
    /// </summary>
    string ServersPath { get; }

    /// <summary>
    /// Raised after settings are successfully saved, providing the new settings
    /// snapshot so subscribers can react to configuration changes at runtime
    /// without requiring an application restart.
    /// </summary>
    event Action<AppSettings>? SettingsChanged;

    /// <summary>
    /// Performs first-run initialization: creates directories,
    /// copies default files if runtime files are missing, and sets file/directory ACLs.
    /// ACL enforcement is fail-closed during initialization — if ACLs cannot be
    /// applied to sensitive directories, the error is logged but initialization
    /// proceeds (config may be on a non-NTFS filesystem).
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Loads and deserializes settings.json, falling back to defaults for missing properties.
    /// </summary>
    Task<AppSettings> LoadSettingsAsync();

    /// <summary>
    /// Serializes and saves settings to settings.json (UTF-8 without BOM).
    /// </summary>
    Task SaveSettingsAsync(AppSettings settings);

    /// <summary>
    /// Atomically merges a trusted host key into settings.json.
    /// The load, mutation, and save happen under the write lock so concurrent
    /// TOFU events cannot overwrite each other.
    /// </summary>
    /// <param name="hostPortKey">Host key in "host:port" or "[ipv6]:port" format.</param>
    /// <param name="fingerprint">SHA256 fingerprint of the host key.</param>
    /// <returns>True if the key was actually persisted (new entry), false if already present.</returns>
    Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint);

    /// <summary>
    /// Atomically merges a batch of trusted host keys into settings.json.
    /// Existing entries are preserved and never overwritten.
    /// </summary>
    /// <returns>The number of entries actually added to settings.</returns>
    Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries);

    /// <summary>
    /// Atomically loads settings, applies a mutation, and saves back under the write lock.
    /// Use this for any targeted property update that must not race with other settings writers.
    /// </summary>
    Task MergeSettingAsync(Action<AppSettings> mutate);

    /// <summary>
    /// Loads and deserializes the server inventory from servers.json.
    /// </summary>
    Task<List<ServerProfileDto>> LoadServersAsync();

    /// <summary>
    /// Atomically loads the server inventory, applies a synchronous mutation, persists it,
    /// and returns a caller-defined result while holding the process-local write lock.
    /// </summary>
    Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate);

    /// <summary>
    /// Serializes and saves the server inventory to servers.json (UTF-8 without BOM).
    /// </summary>
    Task SaveServersAsync(List<ServerProfileDto> servers);
}
