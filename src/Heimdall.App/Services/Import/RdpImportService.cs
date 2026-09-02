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

using System.Globalization;
using System.IO;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Services.Import;

/// <summary>
/// The automatic-rename rule both import services apply to a display-name conflict.
/// </summary>
/// <remarks>
/// The .rdp import and the profile import reach the same conflict from two entry points and have
/// to resolve it to the same name, so the rule is held here once. Two verbatim copies of it used
/// to sit in the two services, where either could be edited alone and the surfaces would disagree
/// with nothing failing.
/// </remarks>
internal static class ImportAutoRename
{
    /// <summary>Locale key holding the "{0} (Imported {1})" rename template.</summary>
    internal const string RenameSuffixKey = "DialogImportRdpRenameSuffix";

    /// <summary>
    /// Word-free rename template used when the active locale carries no rename key, or a template
    /// that drops the numeric placeholder. The search in <see cref="Build"/> can only converge on
    /// a template that varies with the suffix.
    /// </summary>
    internal const string NeutralRenameTemplate = "{0} ({1})";

    /// <summary>The name already in the inventory is the implicit first, so the search starts here.</summary>
    internal const int FirstAutoRenameSuffix = 2;

    /// <summary>
    /// Returns the first name derived from <paramref name="baseName"/> that no profile in
    /// <paramref name="inventory"/> already carries.
    /// </summary>
    internal static string Build(
        string baseName,
        IReadOnlyList<ServerProfileDto> inventory,
        LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(localizer);

        // The suffix ends up in the persisted DisplayName the user reads, so it follows the active
        // locale like the generated fallback name does.
        var template = localizer[RenameSuffixKey];
        if (string.Equals(template, RenameSuffixKey, StringComparison.Ordinal)
            || !template.Contains("{1}", StringComparison.Ordinal))
        {
            template = NeutralRenameTemplate;
        }

        var suffix = FirstAutoRenameSuffix;
        var candidate = string.Format(CultureInfo.CurrentCulture, template, baseName, suffix);
        while (inventory.Any(server => string.Equals(server.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
            candidate = string.Format(CultureInfo.CurrentCulture, template, baseName, suffix);
        }

        return candidate;
    }
}

public interface IRdpImportService
{
    Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct);

    Task<RdpImportResult> ApplyAsync(RdpImportPreview preview, RdpImportSelection selection, CancellationToken ct);
}

public sealed class RdpImportService(IConfigManager configManager, LocalizationManager localizer) : IRdpImportService
{
    private static readonly HashSet<string> GenericImportedNames = new(
    [
        "default",
        "connection",
        "remote desktop connection"
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>Value of <c>drivestoredirect</c> that already means every local drive.</summary>
    private const string AllDrivesToken = "*";

    private const int MinPortNumber = 1;

    private const int MaxPortNumber = 65535;

    private readonly IConfigManager _configManager = configManager;
    private readonly LocalizationManager _localizer = localizer;

    public async Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var normalizedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => string.Equals(Path.GetExtension(path), ".rdp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var currentServers = await _configManager.LoadServersAsync();
        var existingNameMap = currentServers
            .Where(server => !string.IsNullOrWhiteSpace(server.DisplayName))
            .GroupBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);

        var entries = new List<RdpImportPreviewEntry>();
        var filesNotFound = new List<string>();
        var filesUnreadable = new List<string>();

        foreach (var path in normalizedPaths)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                filesNotFound.Add(path);
                continue;
            }

            string content;
            try
            {
                // A .rdp file is untrusted text the user was invited to drag in. The JSON import
                // path caps its input the same way; without the cap one oversized file aborts the
                // whole batch instead of degrading like an unreadable one.
                FileInfo fileInfo = new(path);
                if (fileInfo.Length > AppConstants.MaxImportFileSizeBytes)
                {
                    filesUnreadable.Add(path);
                    Core.Logging.FileLogger.Warn(
                        $"[RdpImport] {Path.GetFileName(path)} rejected: {fileInfo.Length} bytes exceed the " +
                        $"{AppConstants.MaxImportFileSizeBytes}-byte import limit.");
                    continue;
                }

                content = await File.ReadAllTextAsync(path, ct);
            }
            catch (UnauthorizedAccessException)
            {
                filesUnreadable.Add(path);
                continue;
            }
            catch (IOException)
            {
                filesUnreadable.Add(path);
                continue;
            }

            var schema = RdpFileParser.Parse(content);
            var proposedName = DeriveProposedName(path, schema);
            var candidate = CreateCandidate(proposedName);
            var skippedMappings = new List<string>();

            var parseErrorMessage = TryMapSchema(schema, candidate, skippedMappings);
            var hasParseError = parseErrorMessage is not null;

            entries.Add(new RdpImportPreviewEntry
            {
                SourceFilePath = path,
                ProposedName = proposedName,
                Candidate = candidate,
                HasPasswordBlob = schema.HasPasswordBlob,
                HasParseError = hasParseError,
                ParseErrorMessage = parseErrorMessage,
                UnknownKeyCount = schema.UnknownKeys.Count,
                SkippedMappings = skippedMappings
            });
        }

        var proposedNameCounts = entries
            .Where(entry => !entry.HasParseError)
            .GroupBy(entry => entry.ProposedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var finalEntries = entries
            .Select(entry =>
            {
                var hasExistingConflict = existingNameMap.TryGetValue(entry.ProposedName, out var conflictingName);
                var hasBatchConflict = proposedNameCounts.TryGetValue(entry.ProposedName, out var count) && count > 1;
                return new RdpImportPreviewEntry
                {
                    SourceFilePath = entry.SourceFilePath,
                    ProposedName = entry.ProposedName,
                    Candidate = entry.Candidate,
                    HasPasswordBlob = entry.HasPasswordBlob,
                    HasParseError = entry.HasParseError,
                    ParseErrorMessage = entry.ParseErrorMessage,
                    HasNameConflict = !entry.HasParseError && (hasExistingConflict || hasBatchConflict),
                    ConflictingExistingName = hasExistingConflict ? conflictingName : hasBatchConflict ? entry.ProposedName : null,
                    UnknownKeyCount = entry.UnknownKeyCount,
                    SkippedMappings = entry.SkippedMappings
                };
            })
            .ToList();

        return new RdpImportPreview
        {
            Entries = finalEntries,
            FilesNotFound = filesNotFound,
            FilesUnreadable = filesUnreadable
        };
    }

    public async Task<RdpImportResult> ApplyAsync(RdpImportPreview preview, RdpImportSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(selection);

        var previewMap = preview.Entries.ToDictionary(entry => entry.SourceFilePath, StringComparer.OrdinalIgnoreCase);
        return await _configManager.MutateServersAsync(inventory =>
        {
            var warnings = new List<string>();
            var importedCount = 0;
            var replacedCount = 0;
            var renamedCount = 0;
            var skippedCount = 0;
            var passwordsIgnoredCount = 0;

            foreach (RdpImportSelectionEntry selectionEntry in selection.Entries)
            {
                ct.ThrowIfCancellationRequested();

                if (!selectionEntry.IsSelected ||
                    !previewMap.TryGetValue(selectionEntry.SourceFilePath, out RdpImportPreviewEntry? previewEntry))
                {
                    continue;
                }

                if (previewEntry.HasParseError)
                {
                    skippedCount++;
                    warnings.Add(previewEntry.ParseErrorMessage ?? previewEntry.SourceFilePath);
                    continue;
                }

                if (previewEntry.HasPasswordBlob)
                {
                    passwordsIgnoredCount++;
                }

                var currentName = previewEntry.Candidate.DisplayName;
                var existingIndex = inventory.FindIndex(server =>
                    string.Equals(server.DisplayName, currentName, StringComparison.OrdinalIgnoreCase));

                if (existingIndex >= 0)
                {
                    // Conflicts are computed once, before the batch runs. A row the preview showed
                    // as conflict-free carries the Skip default that was never displayed for it, so
                    // a name claimed by an earlier entry of the same batch must not drop the file.
                    RdpConflictResolution resolution = previewEntry.HasNameConflict
                        ? selectionEntry.ConflictResolution
                        : RdpConflictResolution.AutoRename;

                    switch (resolution)
                    {
                        case RdpConflictResolution.Skip:
                            skippedCount++;
                            continue;

                        case RdpConflictResolution.Replace:
                            inventory[existingIndex] = ReplaceExisting(inventory[existingIndex], previewEntry.Candidate);
                            replacedCount++;
                            LogImport(previewEntry, "replaced");
                            continue;

                        case RdpConflictResolution.AutoRename:
                            {
                                var renamed = CloneCandidate(previewEntry.Candidate);
                                renamed.DisplayName = ImportAutoRename.Build(renamed.DisplayName, inventory, _localizer);
                                inventory.Add(renamed);
                                importedCount++;
                                renamedCount++;
                                LogImport(previewEntry, $"renamed to '{renamed.DisplayName}'");
                                continue;
                            }
                    }
                }

                var candidate = CloneCandidate(previewEntry.Candidate);
                inventory.Add(candidate);
                importedCount++;
                LogImport(previewEntry, "imported");
            }

            return new RdpImportResult
            {
                ImportedCount = importedCount,
                ReplacedCount = replacedCount,
                RenamedCount = renamedCount,
                SkippedCount = skippedCount,
                PasswordsIgnoredCount = passwordsIgnoredCount,
                Warnings = warnings
            };
        });
    }

    private static ServerProfileDto CreateCandidate(string proposedName) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = proposedName,
            Origin = ProfileOrigin.ImportRdp,
            ConnectionType = "RDP",
            RemotePort = Heimdall.Core.Models.DefaultPorts.Rdp
        };

    private string? TryMapSchema(
        RdpFileSchema schema,
        ServerProfileDto candidate,
        ICollection<string> skippedMappings)
    {
        var address = !string.IsNullOrWhiteSpace(schema.FullAddress)
            ? schema.FullAddress
            : schema.AlternateFullAddress;

        if (!TrySplitHostAndPort(address, out var host, out var port, out var portOutOfRange))
        {
            return _localizer["WarningImportRdpInvalidAddress"];
        }

        candidate.RemoteServer = host;
        candidate.RemotePort = port;

        if (portOutOfRange)
        {
            skippedMappings.Add("full address port");
            Core.Logging.FileLogger.Info(
                "[RdpImport] Port in 'full address' is outside 1-65535; the default RDP port is used.");
        }

        if (!string.IsNullOrWhiteSpace(schema.Username))
        {
            candidate.RdpUsername = schema.Username;
        }

        // Every assignment below writes a per-profile RDP setting, which the connect-time resolver
        // only reads once the profile stops following the application defaults.
        var carriesPerProfileSettings = false;

        if (schema.AudioMode.HasValue)
        {
            candidate.RdpAudioMode = MapAudioMode(schema.AudioMode.Value);
            carriesPerProfileSettings = true;
        }

        if (schema.RedirectClipboard.HasValue)
        {
            candidate.RdpRedirectClipboard = schema.RedirectClipboard.Value;
            carriesPerProfileSettings = true;
        }

        if (schema.RedirectPrinters.HasValue)
        {
            candidate.RdpRedirectPrinters = schema.RedirectPrinters.Value;
            carriesPerProfileSettings = true;
        }

        if (schema.RedirectSmartCards.HasValue)
        {
            candidate.RdpRedirectSmartCards = schema.RedirectSmartCards.Value;
            carriesPerProfileSettings = true;
        }

        if (schema.DrivesToRedirect is not null)
        {
            candidate.RdpRedirectDrives = !string.IsNullOrWhiteSpace(schema.DrivesToRedirect);
            carriesPerProfileSettings = true;

            // The profile carries a single all-drives flag, so a scoped list such as "C:;" is
            // committed as "redirect every local drive". Disclose the widening instead of hiding it.
            if (candidate.RdpRedirectDrives
                && !string.Equals(schema.DrivesToRedirect.Trim(), AllDrivesToken, StringComparison.Ordinal))
            {
                skippedMappings.Add("drivestoredirect");
                Core.Logging.FileLogger.Info(
                    "[RdpImport] 'drivestoredirect' names specific drives; the profile can only store " +
                    "an all-drives flag.");
            }
        }

        if (schema.UseMultiMon.HasValue)
        {
            candidate.RdpMultiMonitor = schema.UseMultiMon.Value;
            carriesPerProfileSettings = true;
        }

        if (schema.SessionBpp.HasValue)
        {
            candidate.RdpColorDepth = schema.SessionBpp.Value;
            carriesPerProfileSettings = true;
        }

        // NLA state lives in enablecredsspsupport and nowhere else. authentication level describes
        // server authentication only, so deriving NLA from it silently disabled NLA on any profile
        // exported with NLA enabled and server authentication not required, which writes level 0.
        // Values outside each field's documented range leave the candidate default untouched rather
        // than weakening it implicitly.
        if (schema.EnableCredSspSupport is int credSspSupport && credSspSupport is 0 or 1)
        {
            candidate.RdpNla = credSspSupport == 1;
            carriesPerProfileSettings = true;
        }

        if (schema.AuthenticationLevel is int authenticationLevel && authenticationLevel is 0 or 1 or 2)
        {
            candidate.RdpStrictServerAuthentication = authenticationLevel == 1;
            carriesPerProfileSettings = true;
        }

        // An absent gatewayusagemethod is not a request to route the session: mstsc treats it as
        // no gateway, and fabricating one here would let a crafted file put a third party in the
        // path of the session and of its credential exchange.
        if (!string.IsNullOrWhiteSpace(schema.GatewayHostname)
            && schema.GatewayUsageMethod is int gatewayUsageMethod
            && gatewayUsageMethod != 0)
        {
            candidate.RdpGateway = schema.GatewayHostname;
        }

        if (schema.ScreenModeId.HasValue)
        {
            skippedMappings.Add("screen mode id");
            Core.Logging.FileLogger.Info("[RdpImport] Skipped mapping for key 'screen mode id' (no target field).");
        }

        if (schema.DesktopWidth.HasValue || schema.DesktopHeight.HasValue)
        {
            skippedMappings.Add("desktop size");
            Core.Logging.FileLogger.Info("[RdpImport] Skipped mapping for desktopwidth/desktopheight (no target fields).");
        }

        if (carriesPerProfileSettings)
        {
            // Without this the resolver answers from the application defaults and every value
            // mapped above is inert. A file carrying only an address keeps following the defaults.
            candidate.RdpUseGlobalDefaults = false;
        }

        return null;
    }

    private static int MapAudioMode(int value) => value switch
    {
        0 => 1, // local playback in .rdp
        1 => 2, // remote playback in .rdp
        _ => 0  // disabled
    };

    private static bool TrySplitHostAndPort(
        string? fullAddress,
        out string host,
        out int port,
        out bool portOutOfRange)
    {
        host = string.Empty;
        port = Heimdall.Core.Models.DefaultPorts.Rdp;
        portOutOfRange = false;

        if (string.IsNullOrWhiteSpace(fullAddress))
        {
            return false;
        }

        var trimmed = fullAddress.Trim();
        if (trimmed.StartsWith('['))
        {
            var closingBracket = trimmed.IndexOf(']');
            if (closingBracket > 0)
            {
                host = trimmed[..(closingBracket + 1)];
                if (closingBracket + 2 < trimmed.Length &&
                    trimmed[closingBracket + 1] == ':' &&
                    int.TryParse(trimmed[(closingBracket + 2)..], out var parsedPort))
                {
                    // Same contract as the plain host:port branch below: a port outside the
                    // protocol range is reported and the default is kept, never written through.
                    if (IsValidPort(parsedPort))
                    {
                        port = parsedPort;
                    }
                    else
                    {
                        portOutOfRange = true;
                    }
                }

                return true;
            }
        }

        var colonCount = trimmed.Count(ch => ch == ':');
        if (colonCount == 1)
        {
            var separator = trimmed.LastIndexOf(':');
            var hostPart = trimmed[..separator].Trim();
            var portPart = trimmed[(separator + 1)..].Trim();

            if (!string.IsNullOrWhiteSpace(hostPart))
            {
                host = hostPart;
                if (int.TryParse(portPart, out var parsedPort))
                {
                    if (IsValidPort(parsedPort))
                    {
                        port = parsedPort;
                    }
                    else
                    {
                        portOutOfRange = true;
                    }
                }

                return true;
            }
        }

        host = trimmed;
        return true;
    }

    private static bool IsValidPort(int port) => port is >= MinPortNumber and <= MaxPortNumber;

    private string DeriveProposedName(string filePath, RdpFileSchema schema)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath)?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(fileName) &&
            !GenericImportedNames.Contains(fileName))
        {
            return fileName;
        }

        if (!string.IsNullOrWhiteSpace(schema.AlternateFullAddress))
        {
            return schema.AlternateFullAddress.Trim();
        }

        if (!string.IsNullOrWhiteSpace(schema.FullAddress))
        {
            return schema.FullAddress.Trim();
        }

        return _localizer["DialogImportRdpFallbackName"];
    }

    private static ServerProfileDto ReplaceExisting(ServerProfileDto existing, ServerProfileDto candidate)
    {
        return new ServerProfileDto
        {
            Id = existing.Id,
            DisplayName = candidate.DisplayName,
            Origin = ProfileOrigin.ImportRdp,
            RemoteServer = candidate.RemoteServer,
            RemotePort = candidate.RemotePort,
            Group = existing.Group,
            // A .rdp file describes an RDP endpoint, not how Heimdall reaches it and not which
            // vault entry holds its credential. Replace must leave those to the existing profile,
            // or a bastion-tunnelled profile silently becomes a direct connection.
            SshGatewayId = existing.SshGatewayId,
            LocalPort = existing.LocalPort,
            VaultEntryName = existing.VaultEntryName,
            RdpUsername = candidate.RdpUsername,
            RdpPasswordEncrypted = null,
            UseDirectConnection = existing.UseDirectConnection,
            ProjectId = existing.ProjectId,
            ConnectionType = "RDP",
            IsFavorite = existing.IsFavorite,
            SortOrder = existing.SortOrder,
            Tags = existing.Tags,
            RdpMode = candidate.RdpMode,
            RdpUseGlobalDefaults = candidate.RdpUseGlobalDefaults,
            RdpRedirectClipboard = candidate.RdpRedirectClipboard,
            RdpRedirectDrives = candidate.RdpRedirectDrives,
            RdpRedirectPrinters = candidate.RdpRedirectPrinters,
            RdpRedirectComPorts = candidate.RdpRedirectComPorts,
            RdpRedirectSmartCards = candidate.RdpRedirectSmartCards,
            RdpRedirectWebcam = candidate.RdpRedirectWebcam,
            RdpRedirectUsb = candidate.RdpRedirectUsb,
            RdpAudioMode = candidate.RdpAudioMode,
            RdpAudioCapture = candidate.RdpAudioCapture,
            RdpMultiMonitor = candidate.RdpMultiMonitor,
            RdpSelectedMonitorIndices = [.. candidate.RdpSelectedMonitorIndices],
            RdpDynamicResolution = candidate.RdpDynamicResolution,
            RdpNla = candidate.RdpNla,
            RdpStrictServerAuthentication = candidate.RdpStrictServerAuthentication,
            RdpColorDepth = candidate.RdpColorDepth,
            RdpBitmapCaching = candidate.RdpBitmapCaching,
            RdpCompression = candidate.RdpCompression,
            RdpHardwareAcceleration = candidate.RdpHardwareAcceleration,
            RdpAutoReconnect = candidate.RdpAutoReconnect,
            RdpPerformanceFlags = candidate.RdpPerformanceFlags,
            RdpDisableUdp = candidate.RdpDisableUdp,
            RdpGateway = candidate.RdpGateway,
            Environment = existing.Environment,
            MacAddress = existing.MacAddress
        };
    }

    private static ServerProfileDto CloneCandidate(ServerProfileDto candidate)
    {
        return new ServerProfileDto
        {
            Id = candidate.Id,
            DisplayName = candidate.DisplayName,
            Origin = ProfileOrigin.ImportRdp,
            RemoteServer = candidate.RemoteServer,
            RemotePort = candidate.RemotePort,
            ConnectionType = candidate.ConnectionType,
            RdpUsername = candidate.RdpUsername,
            RdpMode = candidate.RdpMode,
            RdpUseGlobalDefaults = candidate.RdpUseGlobalDefaults,
            RdpRedirectClipboard = candidate.RdpRedirectClipboard,
            RdpRedirectDrives = candidate.RdpRedirectDrives,
            RdpRedirectPrinters = candidate.RdpRedirectPrinters,
            RdpRedirectComPorts = candidate.RdpRedirectComPorts,
            RdpRedirectSmartCards = candidate.RdpRedirectSmartCards,
            RdpRedirectWebcam = candidate.RdpRedirectWebcam,
            RdpRedirectUsb = candidate.RdpRedirectUsb,
            RdpAudioMode = candidate.RdpAudioMode,
            RdpAudioCapture = candidate.RdpAudioCapture,
            RdpMultiMonitor = candidate.RdpMultiMonitor,
            RdpSelectedMonitorIndices = [.. candidate.RdpSelectedMonitorIndices],
            RdpDynamicResolution = candidate.RdpDynamicResolution,
            RdpNla = candidate.RdpNla,
            RdpStrictServerAuthentication = candidate.RdpStrictServerAuthentication,
            RdpColorDepth = candidate.RdpColorDepth,
            RdpBitmapCaching = candidate.RdpBitmapCaching,
            RdpCompression = candidate.RdpCompression,
            RdpHardwareAcceleration = candidate.RdpHardwareAcceleration,
            RdpAutoReconnect = candidate.RdpAutoReconnect,
            RdpPerformanceFlags = candidate.RdpPerformanceFlags,
            RdpDisableUdp = candidate.RdpDisableUdp,
            RdpGateway = candidate.RdpGateway
        };
    }

    private static void LogImport(RdpImportPreviewEntry previewEntry, string action)
    {
        Core.Logging.FileLogger.Info(
            $"[RdpImport] {Path.GetFileName(previewEntry.SourceFilePath)} {action} as '{previewEntry.Candidate.DisplayName}': " +
            $"{previewEntry.UnknownKeyCount} unknown key(s), {previewEntry.SkippedMappings.Count} skipped mapping(s), " +
            $"passwordBlob={previewEntry.HasPasswordBlob}.");
    }
}
