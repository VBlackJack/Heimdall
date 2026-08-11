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

    private readonly string _installPath;
    private readonly string _dataPath;
    private readonly string _configPath;
    private readonly string _settingsPath;
    private readonly string _serversPath;
    private readonly string _settingsDefaultPath;
    private readonly string _serversDefaultPath;
    private readonly string _logsPath;
    // Process-local serialization only. TODO: add cross-process locking and revision/CAS
    // before supporting multiple Heimdall instances that share one configuration directory.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _settingsPublicationLock = new();
    private readonly Func<Task> _beforeSettingsLoadPublishAsync;
    private long _nextSettingsRevision;
    private PublishedSettingsSnapshot? _publishedSettings;

    /// <summary>
    /// Initializes a ConfigManager with the legacy co-located install and writable layout.
    /// This overload is retained for compatibility with isolated consumers and tests.
    /// </summary>
    /// <param name="basePath">Root directory containing the bundled config directory.</param>
    public ConfigManager(string basePath)
        : this(
            basePath,
            Path.Combine(basePath, AppConstants.BundledConfigDirectoryName))
    {
    }

    /// <summary>
    /// Initializes a ConfigManager with separate read-only install and writable data roots.
    /// </summary>
    /// <param name="installPath">Application install root containing bundled defaults.</param>
    /// <param name="dataPath">User-writable application data root.</param>
    public ConfigManager(string installPath, string dataPath)
        : this(installPath, dataPath, beforeSettingsLoadPublishAsync: null)
    {
    }

    internal ConfigManager(
        string installPath,
        string dataPath,
        Func<Task>? beforeSettingsLoadPublishAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);

        _installPath = installPath;
        _dataPath = dataPath;
        _configPath = dataPath;
        _settingsPath = Path.Combine(_configPath, "settings.json");
        _serversPath = Path.Combine(_configPath, "servers.json");

        string bundledConfigPath =
            Path.Combine(installPath, AppConstants.BundledConfigDirectoryName);
        _settingsDefaultPath = Path.Combine(bundledConfigPath, "settings.default.json");
        _serversDefaultPath = Path.Combine(bundledConfigPath, "servers.default.json");
        _logsPath = ApplicationDataPathResolver.GetLogsDirectory(dataPath);
        _beforeSettingsLoadPublishAsync =
            beforeSettingsLoadPublishAsync ?? (static () => Task.CompletedTask);
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
    public AppSettings? CurrentSettings
    {
        get
        {
            lock (_settingsPublicationLock)
            {
                return _publishedSettings is null
                    ? null
                    : CloneSettings(_publishedSettings.Settings);
            }
        }
    }

    internal long CurrentSettingsRevision
    {
        get
        {
            lock (_settingsPublicationLock)
            {
                return _publishedSettings?.Revision ?? 0;
            }
        }
    }

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

        MigrateLegacyData();

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

    private void MigrateLegacyData()
    {
        string legacyConfigPath =
            Path.Combine(_installPath, AppConstants.BundledConfigDirectoryName);

        string[] legacyRuntimeFiles =
        [
            "settings.json",
            "servers.json",
            "password-presets.json",
            "network-kb.json",
            "session-snapshot.json",
            "split-layouts.json"
        ];

        foreach (string fileName in legacyRuntimeFiles)
        {
            CopyFileIfMissing(
                Path.Combine(legacyConfigPath, fileName),
                Path.Combine(_dataPath, fileName));
        }

        CopyDirectoryContentsWithoutOverwrite(
            Path.Combine(legacyConfigPath, AppConstants.NetworkScansDirectoryName),
            ApplicationDataPathResolver.GetNetworkScansDirectory(_dataPath));

        CopyDirectoryContentsWithoutOverwrite(
            Path.Combine(legacyConfigPath, AppConstants.NotesDirectoryName),
            Path.Combine(
                _dataPath,
                AppConstants.BundledConfigDirectoryName,
                AppConstants.NotesDirectoryName));

        CopyDirectoryContentsWithoutOverwrite(
            Path.Combine(_installPath, AppConstants.MacrosDirectoryName),
            ApplicationDataPathResolver.GetMacrosDirectory(_dataPath));

        CopyDirectoryContentsWithoutOverwrite(
            Path.Combine(_installPath, AppConstants.LogsDirectoryName),
            _logsPath);
    }

    private static void CopyFileIfMissing(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath) || File.Exists(destinationPath))
        {
            return;
        }

        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    private static void CopyDirectoryContentsWithoutOverwrite(
        string sourceDirectory,
        string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        foreach (string sourcePath in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            CopyFileIfMissing(sourcePath, Path.Combine(destinationDirectory, relativePath));
        }
    }

    /// <summary>
    /// Loads and deserializes settings.json, falling back to defaults for missing properties.
    /// </summary>
    public async Task<AppSettings> LoadSettingsAsync()
    {
        long revision = ReserveSettingsRevision();
        AppSettings settings;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            settings = await LoadSettingsInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        await _beforeSettingsLoadPublishAsync().ConfigureAwait(false);
        TryPublishSettingsSnapshot(revision, settings);
        return settings;
    }

    /// <summary>
    /// Serializes and saves settings to settings.json (UTF-8 without BOM).
    /// </summary>
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        AppSettings settingsToPublish;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            AppSettings currentSettings = await LoadSettingsInternalAsync(
                requireSupportedSchemaForWrite: true).ConfigureAwait(false);
            AppSettings settingsToSave = CloneSettings(settings);
            MergeExtensionData(currentSettings.ExtensionData, settingsToSave.ExtensionData);
            settingsToSave.SchemaVersion = AppSettings.CurrentSchemaVersion;
            NormalizeTrustedHostKeys(settingsToSave);
            ValidateSettingsWriteInvariants(settingsToSave);
            var json = JsonSerializer.Serialize(settingsToSave, JsonOptions);
            await WriteTextAsync(_settingsPath, json).ConfigureAwait(false);
            TryPublishSettingsSnapshot(ReserveSettingsRevision(), settingsToSave);
            settingsToPublish = CloneSettings(settingsToSave);
        }
        finally
        {
            _writeLock.Release();
        }

        SettingsChanged?.Invoke(settingsToPublish);
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
            var settings = await LoadSettingsInternalAsync(
                requireSupportedSchemaForWrite: true).ConfigureAwait(false);
            if (settings.TrustedHostKeysV2.ContainsKey(hostPortKey))
            {
                return false;
            }

            var entry = CreateUserConfirmedHostKeyEntry(fingerprint);
            settings.TrustedHostKeysV2[hostPortKey] = entry;
            settings.TrustedHostKeys.TryAdd(hostPortKey, fingerprint);
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            ValidateSettingsWriteInvariants(settings);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await WriteTextAsync(_settingsPath, json).ConfigureAwait(false);
            TryPublishSettingsSnapshot(ReserveSettingsRevision(), settings);
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
            var settings = await LoadSettingsInternalAsync(
                requireSupportedSchemaForWrite: true).ConfigureAwait(false);
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
                settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
                ValidateSettingsWriteInvariants(settings);
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                await WriteTextAsync(_settingsPath, json).ConfigureAwait(false);
                TryPublishSettingsSnapshot(ReserveSettingsRevision(), settings);
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
            settings = await LoadSettingsInternalAsync(
                requireSupportedSchemaForWrite: true).ConfigureAwait(false);
            mutate(settings);
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            NormalizeTrustedHostKeys(settings);
            ValidateSettingsWriteInvariants(settings);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await WriteTextAsync(_settingsPath, json).ConfigureAwait(false);
            TryPublishSettingsSnapshot(ReserveSettingsRevision(), settings);
            settings = CloneSettings(settings);
        }
        finally
        {
            _writeLock.Release();
        }

        SettingsChanged?.Invoke(settings);
    }

    /// <summary>
    /// Internal settings load that does NOT acquire the write lock (caller must hold it).
    /// </summary>
    private async Task<AppSettings> LoadSettingsInternalAsync(
        bool requireSupportedSchemaForWrite = false)
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        var json = await File.ReadAllTextAsync(_settingsPath, Utf8NoBom).ConfigureAwait(false);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions) ?? new AppSettings();
        ValidateSchemaVersion(
            "settings",
            _settingsPath,
            settings.SchemaVersion,
            AppSettings.CurrentSchemaVersion,
            requireSupportedSchemaForWrite);
        NormalizeTrustedHostKeys(settings);
        List<ValidationDiagnostic> diagnostics = [.. SchemaValidator.DiagnoseSettingsLoad(settings).Diagnostics];
        for (int index = 0; index < settings.SshGateways.Count; index++)
        {
            foreach (ValidationDiagnostic diagnostic in
                SchemaValidator.DiagnoseGatewayLoad(settings.SshGateways[index]).Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Message = $"SshGateways[{index}].{diagnostic.Message}"
                });
            }
        }

        LogValidationDiagnostics("settings.json", diagnostics);
        return settings;
    }

    private long ReserveSettingsRevision() => Interlocked.Increment(ref _nextSettingsRevision);

    private bool TryPublishSettingsSnapshot(long revision, AppSettings settings)
    {
        AppSettings immutableSnapshot = CloneSettings(settings);
        lock (_settingsPublicationLock)
        {
            if (_publishedSettings is not null && revision <= _publishedSettings.Revision)
            {
                return false;
            }

            _publishedSettings = new PublishedSettingsSnapshot(revision, immutableSnapshot);
            return true;
        }
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, ReadOptions)
            ?? throw new JsonException("Failed to clone application settings.");
    }

    private static void MergeExtensionData(
        IReadOnlyDictionary<string, JsonElement> source,
        IDictionary<string, JsonElement> destination)
    {
        foreach ((string key, JsonElement value) in source)
        {
            if (!destination.ContainsKey(key))
            {
                destination[key] = value.Clone();
            }
        }
    }

    private static void ValidateSchemaVersion(
        string documentName,
        string documentPath,
        int foundVersion,
        int supportedVersion,
        bool requireSupportedSchemaForWrite)
    {
        if (foundVersion <= supportedVersion)
        {
            return;
        }

        Logging.FileLogger.Warn(
            $"{documentName} schema version {foundVersion} is newer than supported version " +
            $"{supportedVersion}. The document is read-only and will not be overwritten: " +
            documentPath);

        if (requireSupportedSchemaForWrite)
        {
            throw new ConfigurationSchemaVersionException(
                documentName,
                documentPath,
                foundVersion,
                supportedVersion);
        }
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
        ServerInventoryDocument document =
            await LoadServerInventoryInternalAsync().ConfigureAwait(false);
        return document.Servers;
    }

    /// <summary>
    /// Atomically loads, mutates, and persists the server inventory under the process-local
    /// write lock. The synchronous delegate must not call back into this manager.
    /// </summary>
    public async Task<TResult> MutateServersAsync<TResult>(
        Func<List<ServerProfileDto>, TResult> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ServerInventoryDocument document = await LoadServerInventoryInternalAsync(
                requireSupportedSchemaForWrite: true).ConfigureAwait(false);
            List<ServerProfileDto> servers = document.Servers;
            Dictionary<string, CredentialReferenceSnapshot> credentialReferences =
                CaptureCredentialReferences(servers);
            Dictionary<string, IReadOnlyDictionary<string, JsonElement>> extensionData =
                CaptureServerExtensionData(servers);
            string originalJson = JsonSerializer.Serialize(servers, JsonOptions);
            TResult result = mutate(servers);
            FreezeCredentialReferencesAcrossRenames(credentialReferences, servers);
            PreserveServerExtensionData(extensionData, servers);
            string mutatedJson = JsonSerializer.Serialize(servers, JsonOptions);
            if (!string.Equals(originalJson, mutatedJson, StringComparison.Ordinal))
            {
                await SaveServerInventoryInternalAsync(document).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Loads the server inventory without acquiring the write lock.
    /// </summary>
    private async Task<ServerInventoryDocument> LoadServerInventoryInternalAsync(
        bool requireSupportedSchemaForWrite = false)
    {
        if (!File.Exists(_serversPath))
        {
            return new ServerInventoryDocument();
        }

        var json = await File.ReadAllTextAsync(_serversPath, Utf8NoBom)
            .ConfigureAwait(false);
        using JsonDocument parsed = JsonDocument.Parse(json);
        ServerInventoryDocument document;
        if (parsed.RootElement.ValueKind == JsonValueKind.Array)
        {
            document = new ServerInventoryDocument
            {
                Servers = JsonSerializer.Deserialize<List<ServerProfileDto>>(json, ReadOptions) ?? []
            };
        }
        else if (parsed.RootElement.ValueKind == JsonValueKind.Object)
        {
            document = JsonSerializer.Deserialize<ServerInventoryDocument>(json, ReadOptions)
                ?? new ServerInventoryDocument();
            document.Servers ??= [];
        }
        else
        {
            throw new JsonException(
                "servers.json must contain either a legacy array or a versioned object document.");
        }

        ValidateSchemaVersion(
            "server inventory",
            _serversPath,
            document.SchemaVersion,
            ServerInventoryDocument.CurrentSchemaVersion,
            requireSupportedSchemaForWrite);

        foreach (var server in document.Servers)
        {
            PostConnectMigration.Migrate(server);
            RdpResolutionProfileMigration.Migrate(server);
        }

        List<ValidationDiagnostic> diagnostics = [];
        for (int index = 0; index < document.Servers.Count; index++)
        {
            foreach (ValidationDiagnostic diagnostic in
                SchemaValidator.DiagnoseServerLoad(document.Servers[index]).Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Message = $"Servers[{index}].{diagnostic.Message}"
                });
            }
        }

        LogValidationDiagnostics("servers.json", diagnostics);

        return document;
    }

    private static void ValidateSettingsWriteInvariants(AppSettings settings)
    {
        List<string> errors = [.. SchemaValidator.ValidateSettingsWriteInvariants(settings).Errors];
        for (int index = 0; index < settings.SshGateways.Count; index++)
        {
            foreach (string error in SchemaValidator
                .ValidateGatewayWriteInvariants(settings.SshGateways[index]).Errors)
            {
                errors.Add($"SshGateways[{index}].{error}");
            }
        }

        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException("settings.json", errors);
        }
    }

    private static void LogValidationDiagnostics(
        string documentName,
        IReadOnlyCollection<ValidationDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        string detail = string.Join(
            "; ",
            diagnostics.Select(diagnostic =>
                $"[{diagnostic.Severity}] {diagnostic.Message}"));
        Logging.FileLogger.Warn(
            $"Configuration diagnostics for {documentName}: {detail}");
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
            ServerInventoryDocument currentDocument = await LoadServerInventoryInternalAsync(
                requireSupportedSchemaForWrite: true).ConfigureAwait(false);
            List<ServerProfileDto> currentServers = currentDocument.Servers;
            Dictionary<string, CredentialReferenceSnapshot> credentialReferences =
                CaptureCredentialReferences(currentServers);
            Dictionary<string, IReadOnlyDictionary<string, JsonElement>> extensionData =
                CaptureServerExtensionData(currentServers);
            FreezeCredentialReferencesAcrossRenames(credentialReferences, servers);
            PreserveServerExtensionData(extensionData, servers);
            currentDocument.Servers = servers;
            await SaveServerInventoryInternalAsync(currentDocument).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Persists the server inventory without acquiring the write lock.
    /// </summary>
    private async Task SaveServerInventoryInternalAsync(ServerInventoryDocument document)
    {
        document.SchemaVersion = ServerInventoryDocument.CurrentSchemaVersion;
        foreach (var server in document.Servers)
        {
            PostConnectMigration.PrepareForSave(server);
            RdpResolutionProfileMigration.PrepareForSave(server);
        }

        var json = JsonSerializer.Serialize(document, JsonOptions);
        await WriteTextAsync(_serversPath, json).ConfigureAwait(false);
    }

    private static Dictionary<string, CredentialReferenceSnapshot> CaptureCredentialReferences(
        IEnumerable<ServerProfileDto> servers)
    {
        var snapshots = new Dictionary<string, CredentialReferenceSnapshot>(StringComparer.Ordinal);
        foreach (ServerProfileDto server in servers)
        {
            if (!string.IsNullOrEmpty(server.Id))
            {
                snapshots.TryAdd(
                    server.Id,
                    new CredentialReferenceSnapshot(server.DisplayName, server.VaultEntryName));
            }
        }

        return snapshots;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
        CaptureServerExtensionData(IEnumerable<ServerProfileDto> servers)
    {
        var snapshots =
            new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (ServerProfileDto server in servers)
        {
            if (string.IsNullOrEmpty(server.Id))
            {
                continue;
            }

            var extensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            MergeExtensionData(server.ExtensionData, extensionData);
            snapshots.TryAdd(server.Id, extensionData);
        }

        return snapshots;
    }

    private static void PreserveServerExtensionData(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> previousProfiles,
        IEnumerable<ServerProfileDto> updatedProfiles)
    {
        foreach (ServerProfileDto updatedProfile in updatedProfiles)
        {
            if (previousProfiles.TryGetValue(
                    updatedProfile.Id,
                    out IReadOnlyDictionary<string, JsonElement>? extensionData))
            {
                MergeExtensionData(extensionData, updatedProfile.ExtensionData);
            }
        }
    }

    private static void FreezeCredentialReferencesAcrossRenames(
        IReadOnlyDictionary<string, CredentialReferenceSnapshot> previousProfiles,
        IEnumerable<ServerProfileDto> updatedProfiles)
    {
        foreach (ServerProfileDto updatedProfile in updatedProfiles)
        {
            if (!previousProfiles.TryGetValue(updatedProfile.Id, out CredentialReferenceSnapshot previousProfile)
                || string.Equals(previousProfile.DisplayName, updatedProfile.DisplayName, StringComparison.Ordinal)
                || !string.IsNullOrWhiteSpace(previousProfile.VaultEntryName)
                || !string.IsNullOrWhiteSpace(updatedProfile.VaultEntryName))
            {
                continue;
            }

            updatedProfile.VaultEntryName = previousProfile.DisplayName;
        }
    }

    private readonly record struct CredentialReferenceSnapshot(
        string DisplayName,
        string? VaultEntryName);

    private sealed record PublishedSettingsSnapshot(
        long Revision,
        AppSettings Settings);

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

        if (OperatingSystem.IsWindows())
        {
            // Atomic write-temp-then-rename with the restrictive ACL applied at
            // temp-create: a crash mid-write can never truncate the target, and the
            // final file always carries the restrictive ACL (no separate post-write
            // ApplyFileAcl needed for these paths).
            await Security.SecureFileWriter.WriteAllTextAtomicAsync(path, content).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(path, content, Utf8NoBom).ConfigureAwait(false);
        }
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

/// <summary>
/// Raised when a settings write would persist a configuration that violates
/// a security-critical invariant.
/// </summary>
public sealed class ConfigurationValidationException : InvalidOperationException
{
    public ConfigurationValidationException(
        string documentName,
        IReadOnlyCollection<string> errors)
        : base($"{documentName} cannot be saved: {string.Join("; ", errors)}")
    {
        DocumentName = documentName;
        Errors = errors.ToArray();
    }

    public string DocumentName { get; }

    public IReadOnlyList<string> Errors { get; }
}
