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
using System.Text.Json.Serialization;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Utilities;

namespace Heimdall.App.Services.Import;

/// <summary>
/// Shared profile import workflow for entry points that already have one or more file paths.
/// </summary>
public sealed class ProfileImportService(
    IConfigManager configManager,
    LocalizationManager localizer,
    IDialogService dialogService,
    IRdpImportService rdpImportService,
    long maxImportFileSizeBytes = AppConstants.MaxImportFileSizeBytes) : IProfileImportService
{
    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static readonly IReadOnlySet<string> SupportedConnectionTypes =
        ConnectionTypeCatalog.CanonicalTypes;

    private readonly IConfigManager _configManager = configManager;
    private readonly LocalizationManager _localizer = localizer;
    private readonly IDialogService _dialogService = dialogService;
    private readonly IRdpImportService _rdpImportService = rdpImportService;
    private readonly long _maxImportFileSizeBytes = maxImportFileSizeBytes;

    public Task<ProfileImportResult> ImportFromPathAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return ImportFromPathsAsync([path], ct);
    }

    public async Task<ProfileImportResult> ImportFromPathsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedPaths.Length == 0)
        {
            return ProfileImportResult.NoChanges();
        }

        var extensions = normalizedPaths
            .Select(path => Path.GetExtension(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (extensions.Length == 1 && string.Equals(extensions[0], ".rdp", StringComparison.OrdinalIgnoreCase))
        {
            return await ImportRdpFilesAsync(normalizedPaths, ct);
        }

        if (normalizedPaths.Length == 1 &&
            string.Equals(extensions[0], ".json", StringComparison.OrdinalIgnoreCase))
        {
            return await ImportJsonProfilesAsync(normalizedPaths[0], ct);
        }

        var extension = extensions.Length == 1 && !string.IsNullOrWhiteSpace(extensions[0])
            ? extensions[0]
            : string.Join(", ", extensions.Where(ext => !string.IsNullOrWhiteSpace(ext)));

        return ProfileImportResult.Failure(
            _localizer.Format("ImportErrorUnknownExtension", string.IsNullOrWhiteSpace(extension) ? "<none>" : extension));
    }

    private async Task<ProfileImportResult> ImportRdpFilesAsync(string[] paths, CancellationToken ct)
    {
        var preview = await _rdpImportService.PreviewAsync(paths, ct);
        if (preview.Entries.Count == 0)
        {
            _dialogService.ShowWarning(
                _localizer["DialogImportRdpTitle"],
                _localizer["WarningImportRdpNoValidFiles"]);
            return ProfileImportResult.NoChanges();
        }

        var dialogVm = new RdpImportDialogViewModel(_localizer, preview);
        var selection = await _dialogService.ShowRdpImportDialogAsync(dialogVm);
        if (selection is null)
        {
            return ProfileImportResult.Cancelled();
        }

        var result = await _rdpImportService.ApplyAsync(preview, selection, ct);
        var summary = _localizer.Format(
            "StatusImportRdpSummary",
            result.ImportedCount,
            result.ReplacedCount,
            result.RenamedCount,
            result.SkippedCount,
            result.PasswordsIgnoredCount);

        if (result.ImportedCount > 0 || result.ReplacedCount > 0)
        {
            _dialogService.ShowInfo(_localizer["DialogImportRdpTitle"], summary);
        }
        else
        {
            _dialogService.ShowWarning(_localizer["DialogImportRdpTitle"], summary);
        }

        return new ProfileImportResult
        {
            HasChanges = result.ImportedCount > 0 || result.ReplacedCount > 0,
            UserMessage = summary,
            ImportedCount = result.ImportedCount,
            ReplacedCount = result.ReplacedCount,
            RenamedCount = result.RenamedCount,
            SkippedCount = result.SkippedCount,
            PasswordsIgnoredCount = result.PasswordsIgnoredCount
        };
    }

    private async Task<ProfileImportResult> ImportJsonProfilesAsync(string path, CancellationToken ct)
    {
        ProfileConfigDocument importDocument;
        try
        {
            FileInfo fileInfo = new(path);
            if (fileInfo.Length > _maxImportFileSizeBytes)
            {
                return ProfileImportResult.Failure(
                    _localizer.Format("ImportErrorFileTooLarge", FileSize.Format(_maxImportFileSizeBytes)));
            }

            string json = await File.ReadAllTextAsync(path, ct);
            importDocument = ReadJsonConfigDocument(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ProfileImportResult.Failure(_localizer.Format("StatusImportFailed", ex.Message));
        }

        List<ServerProfileDto> candidates = importDocument.Servers;
        if (candidates.Count == 0)
        {
            _dialogService.ShowInfo(_localizer["ImportDialogTitle"], _localizer["ImportNoSessionsFound"]);
            return ProfileImportResult.NoChanges();
        }

        ImportedProfileSanitizer.Sanitize(candidates);

        var preview = await BuildJsonPreviewAsync(path, candidates, ct);
        var dialogVm = new RdpImportDialogViewModel(
            _localizer,
            preview,
            RdpImportDialogTextOptions.ProfileImport);
        var selection = await _dialogService.ShowRdpImportDialogAsync(dialogVm);
        if (selection is null)
        {
            return ProfileImportResult.Cancelled();
        }

        var result = await ApplyJsonSelectionAsync(preview, selection, importDocument.Gateways, ct);
        var summary = _localizer.Format(
            "StatusImportProfileSummary",
            result.ImportedCount,
            result.ReplacedCount,
            result.RenamedCount,
            result.SkippedCount);
        var fullSummary = BuildJsonImportSummary(summary, result);

        if ((result.ImportedCount > 0 || result.ReplacedCount > 0) && result.GatewayOrphanCount == 0)
        {
            _dialogService.ShowInfo(_localizer["ImportDialogTitle"], fullSummary);
        }
        else
        {
            _dialogService.ShowWarning(_localizer["ImportDialogTitle"], fullSummary);
        }

        return new ProfileImportResult
        {
            HasChanges = result.ImportedCount > 0 || result.ReplacedCount > 0,
            UserMessage = fullSummary,
            ImportedCount = result.ImportedCount,
            ReplacedCount = result.ReplacedCount,
            RenamedCount = result.RenamedCount,
            SkippedCount = result.SkippedCount,
            GatewayCreatedCount = result.GatewayCreatedCount,
            GatewayMergedCount = result.GatewayMergedCount,
            GatewayOrphanCount = result.GatewayOrphanCount
        };
    }

    private async Task<RdpImportPreview> BuildJsonPreviewAsync(
        string path,
        IReadOnlyList<ServerProfileDto> candidates,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<ServerProfileDto> currentServers = await _configManager.LoadServersAsync();
        Dictionary<string, ServerProfileDto> existingById = currentServers
            .Where(server => !string.IsNullOrWhiteSpace(server.Id))
            .ToDictionary(server => server.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> existingNameMap = currentServers
            .Where(server => !string.IsNullOrWhiteSpace(server.DisplayName))
            .GroupBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> candidateNameCounts = new(StringComparer.OrdinalIgnoreCase);
        List<ServerProfileDto> previewCandidates = new(candidates.Count);
        List<IReadOnlyList<string>> validationErrors = new(candidates.Count);

        for (int index = 0; index < candidates.Count; index++)
        {
            ServerProfileDto candidate = CloneProfile(candidates[index]);
            candidate.ConnectionType = CanonicalizeConnectionType(candidate.ConnectionType);
            string proposedName = string.IsNullOrWhiteSpace(candidate.DisplayName)
                ? _localizer["DialogImportProfileFallbackName"]
                : candidate.DisplayName.Trim();
            candidate.DisplayName = proposedName;
            IReadOnlyList<string> errors = ImportedProfileValidator.Validate(candidate, SupportedConnectionTypes);
            previewCandidates.Add(candidate);
            validationErrors.Add(errors);

            if (errors.Count == 0 && !string.IsNullOrWhiteSpace(proposedName))
            {
                if (candidateNameCounts.TryGetValue(proposedName, out int candidateNameCount))
                {
                    candidateNameCounts[proposedName] = candidateNameCount + 1;
                }
                else
                {
                    candidateNameCounts[proposedName] = 1;
                }
            }
        }

        List<RdpImportPreviewEntry> entries = new(candidates.Count);
        for (int index = 0; index < previewCandidates.Count; index++)
        {
            ServerProfileDto candidate = previewCandidates[index];
            IReadOnlyList<string> errors = validationErrors[index];
            bool hasParseError = errors.Count > 0;
            string? parseErrorMessage = hasParseError ? string.Join("; ", errors) : null;
            string proposedName = candidate.DisplayName;

            ServerProfileDto? idConflict = null;
            bool hasExistingIdConflict = !hasParseError &&
                !string.IsNullOrWhiteSpace(candidate.Id) &&
                existingById.TryGetValue(candidate.Id, out idConflict);
            string? conflictingName = null;
            bool hasExistingNameConflict = !hasParseError &&
                existingNameMap.TryGetValue(proposedName, out conflictingName);
            int count = 0;
            bool hasBatchConflict = !hasParseError &&
                candidateNameCounts.TryGetValue(proposedName, out count) &&
                count > 1;
            string? conflictDisplayName = hasExistingIdConflict
                ? idConflict!.DisplayName
                : hasExistingNameConflict
                    ? conflictingName
                    : hasBatchConflict
                        ? proposedName
                        : null;

            entries.Add(new RdpImportPreviewEntry
            {
                SourceFilePath = $"{path}#{index}",
                ProposedName = proposedName,
                Candidate = candidate,
                HasParseError = hasParseError,
                ParseErrorMessage = parseErrorMessage,
                HasNameConflict = !hasParseError &&
                    (hasExistingIdConflict || hasExistingNameConflict || hasBatchConflict),
                ConflictingExistingName = conflictDisplayName
            });
        }

        return new RdpImportPreview
        {
            Entries = entries,
            FilesNotFound = [],
            FilesUnreadable = []
        };
    }

    private async Task<ProfileImportResult> ApplyJsonSelectionAsync(
        RdpImportPreview preview,
        RdpImportSelection selection,
        IReadOnlyList<SshGatewayDto> importedGateways,
        CancellationToken ct)
    {
        var previewMap = preview.Entries.ToDictionary(entry => entry.SourceFilePath, StringComparer.OrdinalIgnoreCase);
        AppSettings settings = await _configManager.LoadSettingsAsync();
        var mutationResult = await _configManager.MutateServersAsync(inventory =>
        {
            List<ServerProfileDto> appliedServers = [];
            var importedCount = 0;
            var replacedCount = 0;
            var renamedCount = 0;
            var skippedCount = 0;

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
                    continue;
                }

                var candidate = CloneProfile(previewEntry.Candidate);
                var existingIndex = FindExistingProfileIndex(inventory, candidate);
                if (existingIndex >= 0)
                {
                    switch (selectionEntry.ConflictResolution)
                    {
                        case RdpConflictResolution.Skip:
                            skippedCount++;
                            continue;

                        case RdpConflictResolution.Replace:
                            // Through the same reservation as every other branch. The identifier
                            // kept here is the EXISTING profile's, and an existing profile CAN be
                            // in the quick-connect namespace - one imported before the reservation
                            // existed stays there, since the startup migration that moved such
                            // profiles was withdrawn for doing more harm than it repaired. So this
                            // branch checks rather than assuming, and a replace of such a profile
                            // gives it an identifier outside the namespace.
                            candidate.Id = BuildUniqueId(
                                inventory[existingIndex].Id,
                                inventory.Where((_, index) => index != existingIndex).ToList());
                            inventory[existingIndex] = candidate;
                            appliedServers.Add(candidate);
                            replacedCount++;
                            continue;

                        case RdpConflictResolution.AutoRename:
                            candidate.Id = BuildUniqueId(candidate.Id, inventory);
                            candidate.DisplayName = ImportAutoRename.Build(candidate.DisplayName, inventory, _localizer);
                            inventory.Add(candidate);
                            appliedServers.Add(candidate);
                            importedCount++;
                            renamedCount++;
                            continue;
                    }
                }

                candidate.Id = BuildUniqueId(candidate.Id, inventory);
                inventory.Add(candidate);
                appliedServers.Add(candidate);
                importedCount++;
            }

            GatewayImportReconciliationResult gatewayReconciliation =
                new([], new Dictionary<string, string>(), [], 0);
            if (importedCount > 0 || replacedCount > 0)
            {
                gatewayReconciliation = GatewayImportReconciler.Reconcile(
                    settings.SshGateways,
                    importedGateways,
                    appliedServers);
                ApplyGatewayIdMap(appliedServers, gatewayReconciliation.GatewayIdMap);
            }

            return (
                Result: new ProfileImportResult
                {
                    HasChanges = importedCount > 0 || replacedCount > 0,
                    ImportedCount = importedCount,
                    ReplacedCount = replacedCount,
                    RenamedCount = renamedCount,
                    SkippedCount = skippedCount,
                    GatewayCreatedCount = gatewayReconciliation.CreatedCount,
                    GatewayMergedCount = gatewayReconciliation.MergedCount,
                    GatewayOrphanCount = gatewayReconciliation.OrphanReferences.Count
                },
                GatewaysToAdd: gatewayReconciliation.GatewaysToAdd);
        });

        if (mutationResult.GatewaysToAdd.Count > 0)
        {
            // Only the gateways this import created are written, and through a locked
            // read-modify-write. Saving the whole snapshot taken before the inventory
            // mutation erased everything another surface had persisted during that window -
            // since BL-0094 that includes a gateway the user just created from the Add menu.
            //
            // The snapshot is still what the reconciler compared against, so a gateway
            // created concurrently is not considered for MERGING and can end up duplicated
            // rather than reused. That is a lesser defect than losing it, and closing it
            // would mean reconciling under the write lock: CommitMigrationAsync offers
            // exactly that, and was deliberately not used here because its contract obliges
            // a caller that cannot obtain the capability to refuse the whole import.
            await _configManager.MergeSettingAsync(
                settings => settings.SshGateways.AddRange(mutationResult.GatewaysToAdd));
        }

        return mutationResult.Result;
    }

    private static void ApplyGatewayIdMap(
        IEnumerable<ServerProfileDto> servers,
        IReadOnlyDictionary<string, string> gatewayIdMap)
    {
        foreach (ServerProfileDto server in servers)
        {
            if (!string.IsNullOrWhiteSpace(server.SshGatewayId) &&
                gatewayIdMap.TryGetValue(server.SshGatewayId, out string? mappedGatewayId))
            {
                server.SshGatewayId = mappedGatewayId;
            }
        }
    }

    private string BuildJsonImportSummary(string profileSummary, ProfileImportResult result)
    {
        if (result.GatewayCreatedCount == 0 &&
            result.GatewayMergedCount == 0 &&
            result.GatewayOrphanCount == 0)
        {
            return profileSummary;
        }

        List<string> lines =
        [
            profileSummary,
            _localizer.Format(
                "StatusImportProfileGatewaySummary",
                result.GatewayCreatedCount,
                result.GatewayMergedCount,
                result.GatewayOrphanCount)
        ];

        if (result.GatewayCreatedCount > 0)
        {
            lines.Add(_localizer["StatusImportProfileGatewaySecretsExcluded"]);
        }

        if (result.GatewayOrphanCount > 0)
        {
            lines.Add(_localizer["StatusImportProfileGatewayOrphansAction"]);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int FindExistingProfileIndex(IReadOnlyList<ServerProfileDto> inventory, ServerProfileDto candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Id))
        {
            for (var index = 0; index < inventory.Count; index++)
            {
                if (string.Equals(inventory[index].Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        for (var index = 0; index < inventory.Count; index++)
        {
            if (string.Equals(inventory[index].DisplayName, candidate.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The identifier an imported profile may keep, or a fresh one when it may not.</summary>
    /// <remarks>
    /// <para>An import preserves the identifier its file carried, which is deliberate: it is what
    /// lets a profile keep its trust and its history across an export and a re-import.</para>
    /// <para><b>Except in the palette's destination namespace, which no saved profile may
    /// occupy.</b> The palette mints <c>adhoc-rdp-&lt;host&gt;</c> for a destination typed by hand
    /// and keys that destination's certificate approval on it, and it decides whether an entry is
    /// a saved profile or a typed destination by testing the same prefix. A profile importable
    /// into that namespace therefore shared both: approving a certificate for the imported profile
    /// let a quick connect to the matching host connect on it with no question, and the palette
    /// offered the profile the actions of a typed destination.</para>
    /// <para>Reminted rather than refused, so an import of an otherwise valid profile still
    /// succeeds - the identifier is not something the user chose to see.</para>
    /// </remarks>
    private static string BuildUniqueId(string? candidateId, IReadOnlyList<ServerProfileDto> inventory)
    {
        if (!string.IsNullOrWhiteSpace(candidateId) &&
            !AdHocProfileIds.IsAdHoc(candidateId) &&
            inventory.All(server => !string.Equals(server.Id, candidateId, StringComparison.OrdinalIgnoreCase)))
        {
            return candidateId;
        }

        if (AdHocProfileIds.IsAdHoc(candidateId))
        {
            Core.Logging.FileLogger.Warn(
                $"Imported profile carried the identifier '{candidateId}', which belongs to the "
                + "quick-connect namespace; a fresh identifier was assigned so it cannot share a "
                + "certificate approval with a destination typed by hand.");
        }

        return Guid.NewGuid().ToString();
    }

    internal static ProfileConfigDocument ReadJsonConfigDocument(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => new ProfileConfigDocument
            {
                SchemaVersion = 1,
                Servers = JsonSerializer.Deserialize<List<ServerProfileDto>>(json, ProfileJsonOptions) ?? [],
                Gateways = []
            },
            JsonValueKind.Object => SanitizeGatewaySecrets(
                JsonSerializer.Deserialize<ProfileConfigDocument>(json, ProfileJsonOptions) ?? new ProfileConfigDocument()),
            _ => throw new JsonException("Expected a profile array or config object.")
        };
    }

    private static ProfileConfigDocument SanitizeGatewaySecrets(ProfileConfigDocument document)
    {
        document.Servers ??= [];
        document.Gateways ??= [];

        foreach (SshGatewayDto gateway in document.Gateways)
        {
            gateway.SshPasswordEncrypted = null;
            gateway.SshKeyPassphraseEncrypted = null;
        }

        return document;
    }

    /// <summary>
    /// Defensive copy of a parsed candidate, so preview and commit each work on their own instance.
    /// </summary>
    /// <remarks>
    /// This used to be a JSON round-trip, which silently rewrote the imported profile: the presence
    /// flags are not serialized, so deserializing raised three of them unconditionally. An imported
    /// SSH profile carrying a key path and a password but no passphrase field lost
    /// <see cref="ServerProfileDto.UsesLegacySshCredentialMapping"/> the moment it was cloned, and
    /// the legacy RDP resolution migration was skipped on the copy. The copy now reproduces what the
    /// imported document actually declared.
    /// </remarks>
    private static ServerProfileDto CloneProfile(ServerProfileDto source)
        => source.CloneFaithfully();

    private static string CanonicalizeConnectionType(string connectionType)
    {
        return ConnectionTypeCatalog.Canonicalize(connectionType);
    }
}
