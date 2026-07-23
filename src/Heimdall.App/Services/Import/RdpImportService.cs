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
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Services.Import;

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
                    switch (selectionEntry.ConflictResolution)
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
                                renamed.DisplayName = BuildAutoRename(renamed.DisplayName, inventory);
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

        if (!TrySplitHostAndPort(address, out var host, out var port))
        {
            return _localizer["WarningImportRdpInvalidAddress"];
        }

        candidate.RemoteServer = host;
        candidate.RemotePort = port;

        if (!string.IsNullOrWhiteSpace(schema.Username))
        {
            candidate.RdpUsername = schema.Username;
        }

        if (schema.AudioMode.HasValue)
        {
            candidate.RdpAudioMode = MapAudioMode(schema.AudioMode.Value);
        }

        if (schema.RedirectClipboard.HasValue)
        {
            candidate.RdpRedirectClipboard = schema.RedirectClipboard.Value;
        }

        if (schema.RedirectPrinters.HasValue)
        {
            candidate.RdpRedirectPrinters = schema.RedirectPrinters.Value;
        }

        if (schema.RedirectSmartCards.HasValue)
        {
            candidate.RdpRedirectSmartCards = schema.RedirectSmartCards.Value;
        }

        if (schema.DrivesToRedirect is not null)
        {
            candidate.RdpRedirectDrives = !string.IsNullOrWhiteSpace(schema.DrivesToRedirect);
        }

        if (schema.UseMultiMon.HasValue)
        {
            candidate.RdpMultiMonitor = schema.UseMultiMon.Value;
        }

        if (schema.SessionBpp.HasValue)
        {
            candidate.RdpColorDepth = schema.SessionBpp.Value;
        }

        if (schema.AuthenticationLevel.HasValue)
        {
            candidate.RdpNla = schema.AuthenticationLevel.Value > 0;
            candidate.RdpStrictServerAuthentication = schema.AuthenticationLevel.Value == 1;
        }

        if (!string.IsNullOrWhiteSpace(schema.GatewayHostname)
            && schema.GatewayUsageMethod.GetValueOrDefault(1) != 0)
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

        return null;
    }

    private static int MapAudioMode(int value) => value switch
    {
        0 => 1, // local playback in .rdp
        1 => 2, // remote playback in .rdp
        _ => 0  // disabled
    };

    private static bool TrySplitHostAndPort(string? fullAddress, out string host, out int port)
    {
        host = string.Empty;
        port = Heimdall.Core.Models.DefaultPorts.Rdp;

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
                    port = parsedPort;
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
                if (int.TryParse(portPart, out var parsedPort) && parsedPort is > 0 and <= 65535)
                {
                    port = parsedPort;
                }

                return true;
            }
        }

        host = trimmed;
        return true;
    }

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
            SshGatewayId = null,
            RdpUsername = candidate.RdpUsername,
            RdpPasswordEncrypted = null,
            UseDirectConnection = false,
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
            RdpAutoReconnect = candidate.RdpAutoReconnect,
            RdpPerformanceFlags = candidate.RdpPerformanceFlags,
            RdpDisableUdp = candidate.RdpDisableUdp,
            RdpGateway = candidate.RdpGateway,
            Environment = existing.Environment,
            MacAddress = existing.MacAddress
        };
    }

    private static string BuildAutoRename(string baseName, IReadOnlyList<ServerProfileDto> inventory)
    {
        var suffix = 2;
        var candidate = $"{baseName} (Imported {suffix})";
        while (inventory.Any(server => string.Equals(server.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
            candidate = $"{baseName} (Imported {suffix})";
        }

        return candidate;
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
