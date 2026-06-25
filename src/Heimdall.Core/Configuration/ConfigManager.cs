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

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heimdall.Core.Ssh;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Manages application configuration files (settings.json, servers.json).
/// Handles first-run initialization, loading, saving, and file ACL protection.
/// </summary>
public sealed class ConfigManager : IConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _basePath;
    private readonly string _configPath;
    private readonly string _settingsPath;
    private readonly string _serversPath;
    private readonly string _settingsDefaultPath;
    private readonly string _serversDefaultPath;
    private readonly string _logsPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Initializes a new ConfigManager rooted at the given application base path.
    /// </summary>
    /// <param name="basePath">Root directory of the application (where config/ lives).</param>
    public ConfigManager(string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        _basePath = basePath;
        _configPath = Path.Combine(basePath, "config");
        _settingsPath = Path.Combine(_configPath, "settings.json");
        _serversPath = Path.Combine(_configPath, "servers.json");
        _settingsDefaultPath = Path.Combine(_configPath, "settings.default.json");
        _serversDefaultPath = Path.Combine(_configPath, "servers.default.json");
        _logsPath = Path.Combine(basePath, "logs");
    }

    /// <summary>
    /// Path to the config directory.
    /// </summary>
    public string ConfigPath => _configPath;

    /// <summary>
    /// Path to the runtime settings file.
    /// </summary>
    public string SettingsPath => _settingsPath;

    /// <summary>
    /// Path to the runtime servers file.
    /// </summary>
    public string ServersPath => _serversPath;

    /// <summary>
    /// Raised after settings are successfully saved, providing the new settings
    /// snapshot so subscribers can react to configuration changes at runtime
    /// without requiring an application restart.
    /// </summary>
    public event Action<AppSettings>? SettingsChanged;

    /// <summary>
    /// The most recently loaded or persisted settings snapshot, or <c>null</c> before the first
    /// load. Lets composition-root factories read the current configuration synchronously without
    /// re-reading the settings file or blocking on the async load path.
    /// </summary>
    public AppSettings? CurrentSettings { get; private set; }

    /// <summary>
    /// Performs first-run initialization: creates directories,
    /// copies default files if runtime files are missing, and sets file/directory ACLs.
    /// ACL enforcement is fail-closed during initialization — if ACLs cannot be
    /// applied to sensitive directories, the error is logged but initialization
    /// proceeds (config may be on a non-NTFS filesystem).
    /// </summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_configPath);
        Directory.CreateDirectory(_logsPath);

        // Apply directory-level ACLs (inheritable to new files)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Security.AclEnforcer.SetDirectoryAcl(_configPath);
            }
            catch (Exception ex)
            {
                Logging.FileLogger.Warn($"Failed to set ACL on config directory: {ex.Message}");
            }

            try
            {
                Security.AclEnforcer.SetDirectoryAcl(_logsPath);
            }
            catch (Exception ex)
            {
                Logging.FileLogger.Warn($"Failed to set ACL on logs directory: {ex.Message}");
            }
        }

        if (!File.Exists(_settingsPath))
        {
            if (File.Exists(_settingsDefaultPath))
            {
                var defaultContent = await File.ReadAllTextAsync(_settingsDefaultPath, Utf8NoBom)
                    .ConfigureAwait(false);
                await WriteTextAsync(_settingsPath, defaultContent).ConfigureAwait(false);
            }
            else
            {
                var defaults = new AppSettings();
                await SaveSettingsAsync(defaults).ConfigureAwait(false);
            }
        }

        if (!File.Exists(_serversPath))
        {
            if (File.Exists(_serversDefaultPath))
            {
                var defaultContent = await File.ReadAllTextAsync(_serversDefaultPath, Utf8NoBom);
                await WriteTextAsync(_serversPath, defaultContent);
            }
            else
            {
                await SaveServersAsync(new List<ServerProfileDto>());
            }
        }

        ApplyFileAcl(_settingsPath);
        ApplyFileAcl(_serversPath);
    }

    /// <summary>
    /// Loads and deserializes settings.json, falling back to defaults for missing properties.
    /// </summary>
    public async Task<AppSettings> LoadSettingsAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            var defaults = new AppSettings();
            CurrentSettings = defaults;
            return defaults;
        }

        var json = await File.ReadAllTextAsync(_settingsPath, Utf8NoBom)
            .ConfigureAwait(false);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions) ?? new AppSettings();
        NormalizeTrustedHostKeys(settings);
        CurrentSettings = settings;
        return settings;
    }

    /// <summary>
    /// Serializes and saves settings to settings.json (UTF-8 without BOM).
    /// </summary>
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            NormalizeTrustedHostKeys(settings);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await WriteTextAsync(_settingsPath, json).ConfigureAwait(false);
            ApplyFileAcl(_settingsPath);
        }
        finally
        {
            _writeLock.Release();
        }

        CurrentSettings = settings;
        SettingsChanged?.Invoke(settings);
    }

    /// <summary>
    /// Atomically merges a trusted host key into settings.json.
    /// The load, mutation, and save happen under the write lock so concurrent
    /// TOFU events cannot overwrite each other.
    /// </summary>
    /// <param name="hostPortKey">Host key in "host:port" or "[ipv6]:port" format.</param>
    /// <param name="fingerprint">SHA256 fingerprint of the host key.</param>
    /// <returns>True if the key was actually persisted (new entry), false if already present.</returns>
    public async Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPortKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var settings = await LoadSettingsInternalAsync().ConfigureAwait(false);
            if (settings.TrustedHostKeysV2.ContainsKey(hostPortKey))
            {
                return false;
            }

            var entry = CreateUserConfirmedHostKeyEntry(fingerprint);
            settings.TrustedHostKeysV2[hostPortKey] = entry;
            settings.TrustedHostKeys.TryAdd(hostPortKey, fingerprint);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await WriteTextAsync(_settingsPath, json).ConfigureAwait(false);
            ApplyFileAcl(_settingsPath);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Atomically merges a batch of trusted host keys into settings.json.
    /// Existing entries are preserved and never overwritten.
    /// </summary>
    public async Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var settings = await LoadSettingsInternalAsync().ConfigureAwait(false);
            var added = 0;
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                if (settings.TrustedHostKeysV2.ContainsKey(entry.Key))
                {
                    continue;
                }

                settings.TrustedHostKeysV2[entry.Key] = CreateUserConfirmedHostKeyEntry(entry.Value);
                settings.TrustedHostKeys.TryAdd(entry.Key, entry.Value);
                added++;
            }

            if (added > 0)
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                await WriteTextAsync(_settingsPath, json).ConfigureAwait(false);
                ApplyFileAcl(_settingsPath);
            }

            return added;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Atomically loads settings, applies a mutation, and saves back under the write lock.
    /// Use this for any targeted property update that must not race with other settings writers.
    /// </summary>
    public async Task MergeSettingAsync(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        AppSettings settings;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            settings = await LoadSettingsInternalAsync().ConfigureAwait(false);
            mutate(settings);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await WriteTextAsync(_settingsPath, json).ConfigureAwait(false);
            ApplyFileAcl(_settingsPath);
        }
        finally
        {
            _writeLock.Release();
        }

        CurrentSettings = settings;
        SettingsChanged?.Invoke(settings);
    }

    /// <summary>
    /// Internal settings load that does NOT acquire the write lock (caller must hold it).
    /// </summary>
    private async Task<AppSettings> LoadSettingsInternalAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        var json = await File.ReadAllTextAsync(_settingsPath, Utf8NoBom).ConfigureAwait(false);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions) ?? new AppSettings();
        NormalizeTrustedHostKeys(settings);
        return settings;
    }

    private static void NormalizeTrustedHostKeys(AppSettings settings)
    {
        if (settings.TrustedHostKeysV2.Count == 0 && settings.TrustedHostKeys.Count > 0)
        {
            foreach (var (key, fingerprint) in settings.TrustedHostKeys)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(fingerprint))
                {
                    settings.TrustedHostKeysV2[key] = new HostKeyEntry(
                        fingerprint,
                        DateTimeOffset.MinValue,
                        DateTimeOffset.MinValue,
                        "unknown",
                        HostKeySource.Unknown);
                }
            }
        }

        foreach (var (key, entry) in settings.TrustedHostKeysV2)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(entry.Fingerprint))
            {
                settings.TrustedHostKeys.TryAdd(key, entry.Fingerprint);
            }
        }
    }

    private static HostKeyEntry CreateUserConfirmedHostKeyEntry(string fingerprint)
    {
        var now = DateTimeOffset.UtcNow;
        return new HostKeyEntry(
            fingerprint,
            now,
            now,
            "unknown",
            HostKeySource.UserConfirmed);
    }

    /// <summary>
    /// Loads and deserializes the server inventory from servers.json.
    /// </summary>
    public async Task<List<ServerProfileDto>> LoadServersAsync()
    {
        if (!File.Exists(_serversPath))
        {
            return new List<ServerProfileDto>();
        }

        var json = await File.ReadAllTextAsync(_serversPath, Utf8NoBom)
            .ConfigureAwait(false);
        var servers = JsonSerializer.Deserialize<List<ServerProfileDto>>(json, ReadOptions);

        if (servers is null)
        {
            return new List<ServerProfileDto>();
        }

        foreach (var server in servers)
        {
            PostConnectMigration.Migrate(server);
            RdpResolutionProfileMigration.Migrate(server);
        }

        return servers;
    }

    /// <summary>
    /// Serializes and saves the server inventory to servers.json (UTF-8 without BOM).
    /// </summary>
    public async Task SaveServersAsync(List<ServerProfileDto> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var server in servers)
            {
                PostConnectMigration.PrepareForSave(server);
                RdpResolutionProfileMigration.PrepareForSave(server);
            }

            var json = JsonSerializer.Serialize(servers, JsonOptions);
            await WriteTextAsync(_serversPath, json).ConfigureAwait(false);
            ApplyFileAcl(_serversPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes text content to a file using UTF-8 without BOM encoding.
    /// Ensures the parent directory exists.
    /// </summary>
    private static async Task WriteTextAsync(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, Utf8NoBom);
    }

    /// <summary>
    /// Restricts file access to the current user, Administrators, and SYSTEM.
    /// Fails silently if ACLs cannot be applied (non-NTFS, insufficient privileges).
    /// </summary>
    private static void ApplyFileAcl(string filePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            var security = fileInfo.GetAccessControl();

            // Remove inherited rules
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var existingRules = security.GetAccessRules(
                includeExplicit: true, includeInherited: true,
                typeof(SecurityIdentifier));

            foreach (FileSystemAccessRule rule in existingRules)
            {
                security.RemoveAccessRule(rule);
            }

            // Grant access: current user, Administrators, SYSTEM
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser, FileSystemRights.FullControl,
                    AccessControlType.Allow));
            }

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));

            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Logging.FileLogger.Warn($"ACL application skipped (non-NTFS or restricted): {ex.Message}");
        }
    }
}
