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
using System.Text;
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
    private const int MaxWarningIdentityLength = 64;

    private readonly IConfigManager _configManager;
    private readonly IConfigTransactionalWriter? _transactionalWriter;

    public MigrationService(IConfigManager configManager, LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(localizer);

        // Retain the constructor dependency for source compatibility; migration result
        // localization now belongs to MigrationPresentationPolicy.
        _configManager = configManager;

        // Asked for, never demanded here: a configuration manager that cannot commit both files as one
        // unit makes the import refuse, and that refusal belongs in the migration result the caller
        // already handles, not in a constructor throw that would take down composition instead.
        _transactionalWriter = configManager as IConfigTransactionalWriter;
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

        if (_transactionalWriter is null)
        {
            // Refuse before touching anything. Falling back to two independent writes is exactly the
            // partial-state failure this path exists to prevent.
            result.Error = "Configuration manager cannot commit settings and servers as one unit.";
            return result;
        }

        try
        {
            JsonElement legacySettings = await ReadLegacySettingsAsync(legacyPath, ct);
            List<ServerProfileDto> servers = await PrepareLegacyServersAsync(
                legacyPath,
                result,
                ct);

            // One commit. The settings mutation is applied to state loaded inside the write lock, so a
            // trust decision persisted since this migration started is not overwritten by a stale
            // snapshot. The inventory is replaced unconditionally: an empty legacy inventory must empty
            // a populated target, and it is the resulting target state that decides whether a physical
            // write is needed.
            await _transactionalWriter.CommitMigrationAsync(
                settings => MapLegacySettings(legacySettings, settings),
                inventory =>
                {
                    inventory.Clear();
                    inventory.AddRange(servers);
                });

            // Both files are durable and the runtime state is published: only now is any part of this
            // migration true.
            result.SettingsImported = true;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Reads and parses the legacy settings document, without touching current configuration.
    /// </summary>
    /// <remarks>
    /// This deliberately no longer loads and maps the current settings. Doing that here produced a
    /// snapshot taken outside the write lock, which a concurrent write persisted afterwards would then
    /// be silently overwritten by. The mapping now runs inside the transaction, on freshly loaded state.
    /// </remarks>
    private static async Task<JsonElement> ReadLegacySettingsAsync(
        string legacyPath, CancellationToken ct)
    {
        string legacySettingsPath = Path.Combine(
            legacyPath,
            AppConstants.BundledConfigDirectoryName,
            "settings.json");
        string legacyJson = await File.ReadAllTextAsync(legacySettingsPath, ct);
        return JsonSerializer.Deserialize<JsonElement>(legacyJson);
    }

    private async Task<List<ServerProfileDto>> PrepareLegacyServersAsync(
        string legacyPath, MigrationResult result, CancellationToken ct)
    {
        string legacyServersPath = Path.Combine(
            legacyPath,
            AppConstants.BundledConfigDirectoryName,
            "servers.json");
        string legacyJson = await File.ReadAllTextAsync(legacyServersPath, ct);
        List<JsonElement>? legacyServers = JsonSerializer.Deserialize<List<JsonElement>>(legacyJson);

        if (legacyServers is null || legacyServers.Count == 0)
        {
            return [];
        }

        List<ServerProfileDto> servers = [];
        foreach (JsonElement legacyServer in legacyServers)
        {
            result.ServersExamined++;
            int profileIndex = result.ServersExamined;
            string? profileIdentity = GetSafeProfileIdentity(legacyServer);

            try
            {
                ServerProfileDto server = MapLegacyServer(legacyServer);
                servers.Add(server);
                result.ServersImported++;
            }
            catch (Exception ex)
            {
                result.Warnings.Add(new MigrationWarning(
                    profileIndex,
                    profileIdentity,
                    ClassifyWarningReason(ex)));
            }
        }

        return servers;
    }

    private static string? GetSafeProfileIdentity(JsonElement legacyServer)
    {
        if (legacyServer.ValueKind != JsonValueKind.Object
            || !legacyServer.TryGetProperty("DisplayName", out JsonElement displayNameElement)
            || displayNameElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? displayName = displayNameElement.GetString();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        StringBuilder safeIdentity = new(MaxWarningIdentityLength);
        foreach (Rune rune in displayName.Trim().EnumerateRunes())
        {
            bool replaceWithSpace = Rune.IsControl(rune) || Rune.IsWhiteSpace(rune);
            if (replaceWithSpace)
            {
                if (safeIdentity.Length > 0 && safeIdentity[^1] != ' ')
                {
                    safeIdentity.Append(' ');
                }

                continue;
            }

            string runeText = rune.ToString();
            if (safeIdentity.Length + runeText.Length > MaxWarningIdentityLength)
            {
                break;
            }

            safeIdentity.Append(runeText);
        }

        string identity = safeIdentity.ToString().TrimEnd();
        return identity.Length == 0 ? null : identity;
    }

    private static MigrationWarningReason ClassifyWarningReason(Exception exception)
    {
        return exception is FormatException
            or InvalidOperationException
            or JsonException
            or OverflowException
            ? MigrationWarningReason.InvalidLegacyField
            : MigrationWarningReason.UnexpectedMappingFailure;
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
        MapBool(legacy, "RdpDefaultHardwareAcceleration", v => target.RdpDefaultHardwareAcceleration = v);
        MapBool(legacy, "RdpDefaultAutoReconnect", v => target.RdpDefaultAutoReconnect = v);

        // Session
        MapInt(legacy, "MaxEmbeddedSessions", v => target.MaxEmbeddedSessions = v);
        MapInt(legacy, "EmbeddedRdpTimeoutMs", v => target.RdpConnectWatchdogTimeoutMs = v);
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

        // The ordinary import turns a quick-connect identifier away in
        // ProfileImportService.BuildUniqueId; this mapper never passes through it, so the
        // legacy file stayed a second door into that namespace until this check.
        if (AdHocProfileIds.IsAdHoc(dto.Id))
        {
            Core.Logging.FileLogger.Warn(AdHocProfileIds.DescribeRemint(dto.Id));
            dto.Id = Guid.NewGuid().ToString();
        }

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
            v => dto.SshPasswordEncrypted = v);

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
        MapBool(legacy, "RdpHardwareAcceleration", v => dto.RdpHardwareAcceleration = v);
        MapBool(legacy, "RdpAutoReconnect", v => dto.RdpAutoReconnect = v);
        MapNullableString(legacy, "RdpGateway", v => dto.RdpGateway = v);

        // Metadata
        MapNullableString(legacy, "Environment", v => dto.Environment = v);
        MapNullableString(legacy, "MacAddress", v => dto.MacAddress = v);

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

    /// <summary>Number of legacy server entries examined during migration.</summary>
    public int ServersExamined { get; set; }

    /// <summary>Number of examined server entries skipped during migration.</summary>
    public int ServersSkipped => Warnings.Count;

    /// <summary>Structured non-fatal warnings for individual items that failed to import.</summary>
    public List<MigrationWarning> Warnings { get; set; } = new();
}

/// <summary>
/// Classifies a non-fatal legacy profile mapping failure without retaining exception details.
/// </summary>
public enum MigrationWarningReason
{
    /// <summary>The legacy profile contains a field value that cannot be mapped safely.</summary>
    InvalidLegacyField,

    /// <summary>The legacy profile failed for an unexpected mapping reason.</summary>
    UnexpectedMappingFailure
}

/// <summary>
/// Identifies a skipped legacy profile without retaining raw JSON or exception messages.
/// </summary>
/// <param name="Index">One-based position of the profile in the legacy inventory.</param>
/// <param name="Identity">Bounded and sanitized display name, or null when unavailable.</param>
/// <param name="Reason">Safe classification of the mapping failure.</param>
public sealed record MigrationWarning(
    int Index,
    string? Identity,
    MigrationWarningReason Reason);
