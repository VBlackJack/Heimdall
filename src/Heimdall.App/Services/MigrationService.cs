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
using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Services;

/// <summary>
/// Imports configuration from legacy Heimdall (PowerShell 5.1 version).
/// Handles settings.json, servers.json, and DPAPI credential migration.
/// DPAPI-encrypted fields are transferred as-is because encryption is
/// scoped to the same Windows user and machine.
/// </summary>
public sealed class MigrationService
{
    private readonly IConfigManager _configManager;
    private readonly LocalizationManager _localizer;

    public MigrationService(IConfigManager configManager, LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(localizer);

        _configManager = configManager;
        _localizer = localizer;
    }

    /// <summary>
    /// Detect if a legacy Heimdall installation exists at the given path.
    /// Both settings.json and servers.json must be present.
    /// </summary>
    public static bool DetectLegacyInstallation(string legacyPath)
    {
        if (string.IsNullOrWhiteSpace(legacyPath))
        {
            return false;
        }

        var settingsPath = Path.Combine(
            legacyPath,
            AppConstants.BundledConfigDirectoryName,
            "settings.json");
        var serversPath = Path.Combine(
            legacyPath,
            AppConstants.BundledConfigDirectoryName,
            "servers.json");
        return File.Exists(settingsPath) && File.Exists(serversPath);
    }

    /// <summary>
    /// Import settings and servers from a legacy Heimdall installation.
    /// </summary>
    /// <param name="legacyPath">Root directory of the legacy Heimdall installation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="MigrationResult"/> describing the outcome.</returns>
    public async Task<MigrationResult> ImportFromLegacyAsync(
        string legacyPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);

        var result = new MigrationResult();

        try
        {
            await ImportSettingsAsync(legacyPath, result, ct);
            await ImportServersAsync(legacyPath, result, ct);
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private async Task ImportSettingsAsync(
        string legacyPath, MigrationResult result, CancellationToken ct)
    {
        var legacySettingsPath = Path.Combine(
            legacyPath,
            AppConstants.BundledConfigDirectoryName,
            "settings.json");
        var legacyJson = await File.ReadAllTextAsync(legacySettingsPath, ct);
        var legacySettings = JsonSerializer.Deserialize<JsonElement>(legacyJson);

        var settings = await _configManager.LoadSettingsAsync();
        MapLegacySettings(legacySettings, settings);
        await _configManager.SaveSettingsAsync(settings);
        result.SettingsImported = true;
    }

    private async Task ImportServersAsync(
        string legacyPath, MigrationResult result, CancellationToken ct)
    {
        var legacyServersPath = Path.Combine(
            legacyPath,
            AppConstants.BundledConfigDirectoryName,
            "servers.json");
        var legacyJson = await File.ReadAllTextAsync(legacyServersPath, ct);
        var legacyServers = JsonSerializer.Deserialize<List<JsonElement>>(legacyJson);

        if (legacyServers is null || legacyServers.Count == 0)
        {
            return;
        }

        var servers = new List<ServerProfileDto>();
        foreach (var legacySrv in legacyServers)
        {
            try
            {
                var server = MapLegacyServer(legacySrv);
                servers.Add(server);
                result.ServersImported++;
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    _localizer.Format("MigrationServerFailed", ex.Message));
            }
        }

        await _configManager.MutateServersAsync(inventory =>
        {
            inventory.Clear();
            inventory.AddRange(servers);
            return servers.Count;
        });
    }

    // -- Settings mapping --------------------------------------------------

    private static void MapLegacySettings(JsonElement legacy, AppSettings target)
    {
        // Display
        MapInt(legacy, "DefaultResolutionWidth", v => target.DefaultResolutionWidth = v);
        MapInt(legacy, "DefaultResolutionHeight", v => target.DefaultResolutionHeight = v);
        MapBool(legacy, "FullScreen", v => target.FullScreen = v);
        MapBool(legacy, "AdminMode", v => target.AdminMode = v);
        MapString(legacy, "DefaultLocale", v => target.DefaultLocale = v);
        MapString(legacy, "DefaultTheme", v => target.DefaultTheme = v);

        // Tool paths (legacy Plink, kept for import compatibility)
        MapString(legacy, "PlinkPath", v => target.PlinkPath = v);
        MapNullableString(legacy, "PuttyPath", v => target.PuttyPath = v);
        MapNullableString(legacy, "PsftpPath", v => target.PsftpPath = v);

        // Tunnels
        MapInt(legacy, "TunnelEstablishmentDelayMs", v => target.TunnelEstablishmentDelayMs = v);
        MapInt(legacy, "TunnelRetryDelayMs", v => target.TunnelRetryDelayMs = v);
        MapInt(legacy, "ProcessKillTimeoutMs", v => target.ProcessKillTimeoutMs = v);

        // Logging
        MapBool(legacy, "EnableLogging", v => target.EnableLogging = v);
        MapString(legacy, "LogFilePath", v => target.LogFilePath = v);

        // Security (DPAPI-scoped, migrates on same machine/user)
        MapNullableString(legacy, "PinHash", v => target.PinHash = v);
        MapNullableString(legacy, "PinSalt", v => target.PinSalt = v);
        MapNullableString(legacy, "HmacKey", v => target.HmacKey = v);
        MapNullableString(legacy, "LastDpapiUser", v => target.LastDpapiUser = v);
        MapBool(legacy, "RequireCredentialGuard", v => target.RequireCredentialGuard = v);
        MapBool(legacy, "EnableEventLog", v => target.EnableEventLog = v);

        if (legacy.TryGetProperty("HmacKeyCreatedAt", out var hmacDate)
            && hmacDate.ValueKind == JsonValueKind.String)
        {
            if (DateTime.TryParse(hmacDate.GetString(), out var parsed))
            {
                target.HmacKeyCreatedAt = parsed;
            }
        }

        // SSH defaults
        MapString(legacy, "SshDefaultMode", v => target.SshDefaultMode = v);
        MapInt(legacy, "AntiIdleIntervalSeconds", v => target.AntiIdleIntervalSeconds = v);
        MapInt(legacy, "SshTmoutResetIntervalSeconds", v => target.SshTmoutResetIntervalSeconds = v);

        // RDP defaults
        MapString(legacy, "RdpDefaultMode", v => target.RdpDefaultMode = v);
        MapBool(legacy, "RdpDefaultRedirectClipboard", v => target.RdpDefaultRedirectClipboard = v);
        MapBool(legacy, "RdpDefaultRedirectDrives", v => target.RdpDefaultRedirectDrives = v);
        MapBool(legacy, "RdpDefaultRedirectPrinters", v => target.RdpDefaultRedirectPrinters = v);
        MapBool(legacy, "RdpDefaultRedirectComPorts", v => target.RdpDefaultRedirectComPorts = v);
        MapBool(legacy, "RdpDefaultRedirectSmartCards", v => target.RdpDefaultRedirectSmartCards = v);
        MapBool(legacy, "RdpDefaultRedirectWebcam", v => target.RdpDefaultRedirectWebcam = v);
        MapBool(legacy, "RdpDefaultRedirectUsb", v => target.RdpDefaultRedirectUsb = v);
        MapInt(legacy, "RdpDefaultAudioMode", v => target.RdpDefaultAudioMode = v);
        MapBool(legacy, "RdpDefaultAudioCapture", v => target.RdpDefaultAudioCapture = v);
        MapBool(legacy, "RdpDefaultMultiMonitor", v => target.RdpDefaultMultiMonitor = v);
        MapBool(legacy, "RdpDefaultDynamicResolution", v => target.RdpDefaultDynamicResolution = v);
        MapBool(legacy, "RdpDefaultNla", v => target.RdpDefaultNla = v);
        MapInt(legacy, "RdpDefaultColorDepth", v => target.RdpDefaultColorDepth = v);
        MapBool(legacy, "RdpDefaultBitmapCaching", v => target.RdpDefaultBitmapCaching = v);
        MapBool(legacy, "RdpDefaultCompression", v => target.RdpDefaultCompression = v);
        MapBool(legacy, "RdpDefaultAutoReconnect", v => target.RdpDefaultAutoReconnect = v);

        // Session
        MapInt(legacy, "MaxEmbeddedSessions", v => target.MaxEmbeddedSessions = v);
        MapInt(legacy, "EmbeddedRdpTimeoutMs", v => target.EmbeddedRdpTimeoutMs = v);
        MapInt(legacy, "EmbeddedIdleTimeoutMs", v => target.EmbeddedIdleTimeoutMs = v);
        MapBool(legacy, "SftpBrowserEnabled", v => target.SftpBrowserEnabled = v);
        MapBool(legacy, "SftpAutoOpenOnSsh", v => target.SftpAutoOpenOnSsh = v);
        MapBool(legacy, "SftpFollowSshDirectory", v => target.SftpFollowSshDirectory = v);
        MapString(legacy, "ExternalEditorPath", v => target.ExternalEditorPath = v);
        MapBool(legacy, "PreventSleepDuringSession", v => target.PreventSleepDuringSession = v);
        MapBool(legacy, "SessionLoggingEnabled", v => target.SessionLoggingEnabled = v);
        MapString(legacy, "SessionLogDirectory", v => target.SessionLogDirectory = v);

        // UI state
        MapBool(legacy, "SidebarCollapsed", v => target.SidebarCollapsed = v);
        MapInt(legacy, "SidebarWidth", v => target.SidebarWidth = v);
        MapNullableString(legacy, "TunnelGridColumnWidths", v => target.TunnelGridColumnWidths = v);

        if (legacy.TryGetProperty("TreeExpandedNodes", out var nodes)
            && nodes.ValueKind == JsonValueKind.Array)
        {
            target.TreeExpandedNodes = nodes.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.String)
                .Select(n => n.GetString()!)
                .ToList();
        }

        // Collections
        if (legacy.TryGetProperty("SshGateways", out var gateways)
            && gateways.ValueKind == JsonValueKind.Array)
        {
            target.SshGateways = gateways.EnumerateArray()
                .Select(MapLegacyGateway)
                .ToList();
        }

        if (legacy.TryGetProperty("Projects", out var projects)
            && projects.ValueKind == JsonValueKind.Array)
        {
            target.Projects = projects.EnumerateArray()
                .Select(MapLegacyProject)
                .ToList();
        }
    }

    // -- Server mapping ----------------------------------------------------

    private static ServerProfileDto MapLegacyServer(JsonElement legacy)
    {
        var dto = new ServerProfileDto();

        // Identity
        MapString(legacy, "Id", v => dto.Id = v);
        MapString(legacy, "DisplayName", v => dto.DisplayName = v);
        MapString(legacy, "RemoteServer", v => dto.RemoteServer = v);
        MapInt(legacy, "RemotePort", v => dto.RemotePort = v);
        MapInt(legacy, "LocalPort", v => dto.LocalPort = v);
        MapNullableString(legacy, "Group", v => dto.Group = v);
        MapNullableString(legacy, "SshGatewayId", v => dto.SshGatewayId = v);
        MapBool(legacy, "UseDirectConnection", v => dto.UseDirectConnection = v);
        MapNullableString(legacy, "ProjectId", v => dto.ProjectId = v);
        MapString(legacy, "ConnectionType", v => dto.ConnectionType = v);

        // DPAPI-encrypted credentials (migrate as-is, same user + machine = same DPAPI key)
        MapNullableString(legacy, "RdpUsername", v => dto.RdpUsername = v);
        MapNullableString(legacy, "RdpPasswordEncrypted", v => dto.RdpPasswordEncrypted = v);

        // SSH settings
        MapNullableString(legacy, "SshUsername", v => dto.SshUsername = v);
        MapInt(legacy, "SshPort", v => dto.SshPort = v);
        MapString(legacy, "SshMode", v => dto.SshMode = v);
        MapBool(legacy, "SshAgentForwarding", v => dto.SshAgentForwarding = v);
        MapNullableString(legacy, "SshKeyPath", v => dto.SshKeyPath = v);
        MapNullableString(legacy, "SshKeyPassphraseEncrypted", v => dto.SshKeyPassphraseEncrypted = v);
        MapBool(legacy, "SshCompression", v => dto.SshCompression = v);
        MapBool(legacy, "SshX11Forwarding", v => dto.SshX11Forwarding = v);
        MapNullableString(legacy, "SshPasswordEncrypted",
            v => { /* SshPasswordEncrypted not in ServerProfileDto; skip */ });

        // RDP display settings
        MapBool(legacy, "RdpAntiIdle", v => dto.RdpAntiIdle = v);
        MapString(legacy, "RdpAspectRatio", v => dto.RdpAspectRatio = v);
        MapBool(legacy, "IsFavorite", v => dto.IsFavorite = v);
        MapInt(legacy, "SortOrder", v => dto.SortOrder = v);
        MapNullableString(legacy, "Tags", v => dto.Tags = v);

        // RDP mode and device redirection
        MapString(legacy, "RdpMode", v => dto.RdpMode = v);
        MapBool(legacy, "RdpUseGlobalDefaults", v => dto.RdpUseGlobalDefaults = v);
        MapBool(legacy, "RdpRedirectClipboard", v => dto.RdpRedirectClipboard = v);
        MapBool(legacy, "RdpRedirectDrives", v => dto.RdpRedirectDrives = v);
        MapBool(legacy, "RdpRedirectPrinters", v => dto.RdpRedirectPrinters = v);
        MapBool(legacy, "RdpRedirectComPorts", v => dto.RdpRedirectComPorts = v);
        MapBool(legacy, "RdpRedirectSmartCards", v => dto.RdpRedirectSmartCards = v);
        MapBool(legacy, "RdpRedirectWebcam", v => dto.RdpRedirectWebcam = v);
        MapBool(legacy, "RdpRedirectUsb", v => dto.RdpRedirectUsb = v);
        MapInt(legacy, "RdpAudioMode", v => dto.RdpAudioMode = v);
        MapBool(legacy, "RdpAudioCapture", v => dto.RdpAudioCapture = v);
        MapBool(legacy, "RdpMultiMonitor", v => dto.RdpMultiMonitor = v);
        MapBool(legacy, "RdpDynamicResolution", v => dto.RdpDynamicResolution = v);
        MapBool(legacy, "RdpNla", v => dto.RdpNla = v);
        MapInt(legacy, "RdpColorDepth", v => dto.RdpColorDepth = v);
        MapBool(legacy, "RdpBitmapCaching", v => dto.RdpBitmapCaching = v);
        MapBool(legacy, "RdpCompression", v => dto.RdpCompression = v);
        MapBool(legacy, "RdpAutoReconnect", v => dto.RdpAutoReconnect = v);
        MapNullableString(legacy, "RdpGateway", v => dto.RdpGateway = v);

        // Metadata
        MapNullableString(legacy, "Environment", v => dto.Environment = v);
        MapNullableString(legacy, "MacAddress", v =>
        {
            /* MacAddress not in ServerProfileDto; skip gracefully */
        });

        return dto;
    }

    // -- Gateway mapping ---------------------------------------------------

    private static SshGatewayDto MapLegacyGateway(JsonElement legacy)
    {
        var dto = new SshGatewayDto();

        MapString(legacy, "Id", v => dto.Id = v);
        MapString(legacy, "Name", v => dto.Name = v);
        MapString(legacy, "Host", v => dto.Host = v);
        MapInt(legacy, "Port", v => dto.Port = v);
        MapString(legacy, "User", v => dto.User = v);
        MapNullableString(legacy, "KeyPath", v => dto.KeyPath = v);
        MapNullableString(legacy, "SshPasswordEncrypted", v => dto.SshPasswordEncrypted = v);
        MapNullableString(legacy, "SshKeyPassphraseEncrypted", v => dto.SshKeyPassphraseEncrypted = v);
        MapBool(legacy, "IsDefault", v => dto.IsDefault = v);
        MapNullableString(legacy, "ParentGatewayId", v => dto.ParentGatewayId = v);
        MapNullableString(legacy, "HostKeyFingerprint", v => dto.HostKeyFingerprint = v);

        return dto;
    }

    // -- Project mapping ---------------------------------------------------

    private static ProjectDto MapLegacyProject(JsonElement legacy)
    {
        var dto = new ProjectDto();

        MapString(legacy, "Id", v => dto.Id = v);
        MapString(legacy, "Name", v => dto.Name = v);
        MapNullableString(legacy, "Description", v => dto.Description = v);
        MapNullableString(legacy, "Color", v => dto.Color = v);

        return dto;
    }

    // -- JsonElement extraction helpers ------------------------------------

    private static void MapString(JsonElement el, string prop, Action<string> setter)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
        {
            var s = val.GetString();
            if (s is not null)
            {
                setter(s);
            }
        }
    }

    private static void MapNullableString(JsonElement el, string prop, Action<string?> setter)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            setter(val.ValueKind == JsonValueKind.String ? val.GetString() : null);
        }
    }

    private static void MapBool(JsonElement el, string prop, Action<bool> setter)
    {
        if (el.TryGetProperty(prop, out var val)
            && (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False))
        {
            setter(val.GetBoolean());
        }
    }

    private static void MapInt(JsonElement el, string prop, Action<int> setter)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
        {
            setter(val.GetInt32());
        }
    }
}

/// <summary>
/// Describes the outcome of a legacy configuration import operation.
/// </summary>
public sealed class MigrationResult
{
    /// <summary>Whether the migration completed without fatal errors.</summary>
    public bool Success { get; set; }

    /// <summary>Fatal error message if the migration failed entirely.</summary>
    public string? Error { get; set; }

    /// <summary>Whether settings.json was successfully imported.</summary>
    public bool SettingsImported { get; set; }

    /// <summary>Number of servers successfully imported.</summary>
    public int ServersImported { get; set; }

    /// <summary>Non-fatal warnings for individual items that failed to import.</summary>
    public List<string> Warnings { get; set; } = new();
}
