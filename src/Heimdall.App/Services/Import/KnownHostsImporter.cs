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
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Services.Import;

/// <summary>
/// Reads, classifies, and imports trusted SSH host fingerprints from known_hosts.
/// </summary>
public sealed class KnownHostsImporter
{
    private readonly IConfigManager _config;
    private readonly IHostKeyTrustService _hostKeyTrustService;

    [ActivatorUtilitiesConstructor]
    public KnownHostsImporter(IConfigManager config, IHostKeyTrustService hostKeyTrustService)
    {
        _config = config;
        _hostKeyTrustService = hostKeyTrustService;
    }

    public KnownHostsImporter(IConfigManager config, HostKeyStore hostKeyStore)
        : this(config, new HostKeyTrustService(hostKeyStore))
    {
    }

    public async Task<KnownHostsParseResult> ParseFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return await Task.Run(() => ParseFileStreaming(filePath), ct).ConfigureAwait(false);
    }

    private static KnownHostsParseResult ParseFileStreaming(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > KnownHostsParser.MaxFileSizeBytes)
            {
                FileLogger.Warn(
                    $"known_hosts import refused: file '{filePath}' exceeds {KnownHostsParser.MaxFileSizeBytes} bytes ({fileInfo.Length} bytes).");

                return new KnownHostsParseResult(
                    Entries: [],
                    Diagnostics:
                    [
                        new KnownHostsImportDiagnostic(
                            KnownHostsDiagnosticLevel.Warning,
                            SourceLineNumber: 0,
                            Code: KnownHostsDiagnosticCode.FileTooLarge,
                            Context: $"{fileInfo.Length} bytes")
                    ]);
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            var parsed = KnownHostsParser.Parse(content);
            var policyEntries = KnownHostsParser.ParsePolicyEntries(content);
            return parsed with { PolicyEntries = policyEntries };
        }
        catch (IOException ex)
        {
            FileLogger.Warn($"known_hosts import skipped: I/O error reading '{filePath}': {ex.Message}");
            return EmptyResultWithReadError(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            FileLogger.Warn($"known_hosts import skipped: access denied to '{filePath}': {ex.Message}");
            return EmptyResultWithReadError(ex);
        }
        catch (DecoderFallbackException ex)
        {
            FileLogger.Warn($"known_hosts import skipped: decoding error in '{filePath}': {ex.Message}");
            return EmptyResultWithReadError(ex);
        }
    }

    private static KnownHostsParseResult EmptyResultWithReadError(Exception ex)
    {
        return new KnownHostsParseResult(
            Entries: [],
            Diagnostics:
            [
                new KnownHostsImportDiagnostic(
                    KnownHostsDiagnosticLevel.Warning,
                    SourceLineNumber: 0,
                    Code: KnownHostsDiagnosticCode.FileReadError,
                    Context: ex.Message)
            ]);
    }

    public async Task<KnownHostsImportPreview> BuildPreviewAsync(
        KnownHostsParseResult parseResult,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        ct.ThrowIfCancellationRequested();
        var settings = await _config.LoadSettingsAsync().ConfigureAwait(false);
        var diagnostics = parseResult.Diagnostics.ToList();
        var rows = new List<KnownHostsPreviewRow>();

        var indexedCandidates = parseResult.Entries
            .Select((entry, index) => new IndexedCandidate(
                index,
                new KnownHostsImportCandidate
                {
                    Host = entry.Host,
                    Port = entry.Port,
                    Fingerprint = HostKeyFormats.ComputeSha256Fingerprint(entry.Base64Key),
                    Algorithm = entry.KeyType,
                    PublicKeyBase64 = Convert.ToBase64String(entry.Base64Key),
                    SourceLineNumber = entry.SourceLineNumber
                }))
            .ToList();

        foreach (var group in indexedCandidates.GroupBy(
                     candidate => (
                         HostPort: HostKeyFormats.MakeKey(candidate.Candidate.Host, candidate.Candidate.Port),
                         Algorithm: KnownHostsParser.CanonicalizeKeyType(candidate.Candidate.Algorithm)),
                     EqualityComparer<(string HostPort, string Algorithm)>.Default))
        {
            var orderedGroup = group.OrderBy(candidate => candidate.Index).ToList();
            var distinctFingerprints = orderedGroup
                .Select(candidate => candidate.Candidate.Fingerprint)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (distinctFingerprints.Count == 1)
            {
                var primary = orderedGroup[0].Candidate;
                foreach (var duplicate in orderedGroup.Skip(1))
                {
                    diagnostics.Add(new KnownHostsImportDiagnostic(
                        KnownHostsDiagnosticLevel.Info,
                        duplicate.Candidate.SourceLineNumber,
                        KnownHostsDiagnosticCode.DuplicateFingerprintInSourceMerged));
                }

                var storedFingerprint = ResolveStoredFingerprint(settings, primary.Host, primary.Port, primary.Algorithm);
                if (storedFingerprint is not null)
                {
                    rows.Add(new KnownHostsPreviewRow(
                        primary,
                        string.Equals(storedFingerprint, primary.Fingerprint, StringComparison.Ordinal)
                            ? KnownHostsCandidateStatus.Existing
                            : KnownHostsCandidateStatus.Conflict,
                        storedFingerprint));
                }
                else
                {
                    rows.Add(new KnownHostsPreviewRow(primary, KnownHostsCandidateStatus.New, null));
                }

                continue;
            }

            diagnostics.Add(new KnownHostsImportDiagnostic(
                KnownHostsDiagnosticLevel.Warning,
                orderedGroup[0].Candidate.SourceLineNumber,
                KnownHostsDiagnosticCode.IntraFileFingerprintConflict,
                $"{distinctFingerprints.Count} distinct fingerprints for {group.Key.HostPort}"));

            foreach (var item in orderedGroup)
            {
                rows.Add(new KnownHostsPreviewRow(
                    item.Candidate,
                    KnownHostsCandidateStatus.Conflict,
                    null));
            }
        }

        return new KnownHostsImportPreview(rows, diagnostics, parseResult.PolicyEntries);
    }

    public Task<KnownHostsImportOutcome> ImportSelectedAsync(
        IReadOnlyList<KnownHostsImportCandidate> selected,
        CancellationToken ct = default)
        => ImportSelectedAsync(selected, policyEntries: null, ct);

    public async Task<KnownHostsImportOutcome> ImportSelectedAsync(
        IReadOnlyList<KnownHostsImportCandidate> selected,
        KnownHostsPolicyEntries? policyEntries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);

        ct.ThrowIfCancellationRequested();
        var settings = await _config.LoadSettingsAsync().ConfigureAwait(false);
        var selectedGroups = selected
            .GroupBy(
                candidate => (
                    HostPort: HostKeyFormats.MakeKey(candidate.Host, candidate.Port),
                    Algorithm: KnownHostsParser.CanonicalizeKeyType(candidate.Algorithm)),
                EqualityComparer<(string HostPort, string Algorithm)>.Default)
            .ToList();

        var toPersistPrimary = new List<(string Key, KnownHostsImportCandidate Candidate)>();
        var toPersistAdditional = new List<(string CompositeKey, KnownHostsImportCandidate Candidate)>();
        var claimedPrimaryHosts = new HashSet<string>(StringComparer.Ordinal);
        var skippedExisting = 0;
        var skippedConflict = 0;

        foreach (var group in selectedGroups)
        {
            var candidates = group.ToList();
            var distinctFingerprints = candidates
                .Select(candidate => candidate.Fingerprint)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (distinctFingerprints.Count > 1)
            {
                skippedConflict += candidates.Count;
                continue;
            }

            var candidate = candidates[0];
            var hostPortKey = group.Key.HostPort;
            var canonicalAlgo = group.Key.Algorithm;
            var storedFingerprint = ResolveStoredFingerprint(settings, candidate.Host, candidate.Port, canonicalAlgo);
            if (storedFingerprint is not null)
            {
                if (string.Equals(storedFingerprint, candidate.Fingerprint, StringComparison.Ordinal))
                {
                    skippedExisting++;
                }
                else
                {
                    skippedConflict++;
                }

                continue;
            }

            bool hostAlreadyHasPrimary = claimedPrimaryHosts.Contains(hostPortKey)
                || settings.TrustedHostKeys.ContainsKey(hostPortKey)
                || _hostKeyTrustService.GetEntry(candidate.Host, candidate.Port) is not null;

            if (hostAlreadyHasPrimary && !string.IsNullOrWhiteSpace(canonicalAlgo))
            {
                toPersistAdditional.Add(($"{hostPortKey}|{canonicalAlgo}", candidate));
            }
            else
            {
                claimedPrimaryHosts.Add(hostPortKey);
                toPersistPrimary.Add((hostPortKey, candidate));
            }
        }

        var imported = 0;
        if (toPersistPrimary.Count > 0)
        {
            imported = await _config.MergeTrustedHostKeysAsync(
                    toPersistPrimary.Select(item => new KeyValuePair<string, string>(item.Key, item.Candidate.Fingerprint)))
                .ConfigureAwait(false);

            var refreshedSettings = await _config.LoadSettingsAsync().ConfigureAwait(false);
            var importedEntries = new List<(string Key, KnownHostsImportCandidate Candidate, DateTimeOffset ImportedAt)>();
            foreach (var item in toPersistPrimary)
            {
                if (refreshedSettings.TrustedHostKeys.TryGetValue(item.Key, out var storedFingerprint))
                {
                    if (string.Equals(storedFingerprint, item.Candidate.Fingerprint, StringComparison.Ordinal))
                    {
                        var importedAt = DateTimeOffset.UtcNow;
                        _hostKeyTrustService.Import(
                            item.Candidate.Host,
                            item.Candidate.Port,
                            item.Candidate.Fingerprint,
                            item.Candidate.Algorithm,
                            importedAt,
                            item.Candidate.PublicKeyBase64);
                        importedEntries.Add((item.Key, item.Candidate, importedAt));
                    }
                    else
                    {
                        skippedConflict++;
                    }
                }
            }

            if (importedEntries.Count > 0)
            {
                await _config.MergeSettingAsync(s =>
                {
                    foreach (var item in importedEntries)
                    {
                        s.TrustedHostKeysV2[item.Key] = new HostKeyEntry(
                            item.Candidate.Fingerprint,
                            item.ImportedAt,
                            item.ImportedAt,
                            item.Candidate.Algorithm,
                            HostKeySource.ImportedKnownHosts)
                        {
                            PublicKeyBase64 = item.Candidate.PublicKeyBase64
                        };
                        s.TrustedHostKeys[item.Key] = item.Candidate.Fingerprint;
                    }
                }).ConfigureAwait(false);
            }
        }

        if (toPersistAdditional.Count > 0)
        {
            var importedAt = DateTimeOffset.UtcNow;
            var additionalToSave = new List<(string CompositeKey, HostKeyEntry Entry)>();
            foreach (var item in toPersistAdditional)
            {
                var canonicalAlgo = KnownHostsParser.CanonicalizeKeyType(item.Candidate.Algorithm);
                _hostKeyTrustService.TrustAdditionalAlgorithm(
                    item.Candidate.Host,
                    item.Candidate.Port,
                    item.Candidate.Fingerprint,
                    canonicalAlgo,
                    HostKeySource.ImportedKnownHosts,
                    item.Candidate.PublicKeyBase64);
                additionalToSave.Add((
                    item.CompositeKey,
                    new HostKeyEntry(
                        item.Candidate.Fingerprint,
                        importedAt,
                        importedAt,
                        canonicalAlgo,
                        HostKeySource.ImportedKnownHosts)
                    {
                        PublicKeyBase64 = item.Candidate.PublicKeyBase64
                    }));
                imported++;
            }

            await _config.MergeSettingAsync(s =>
            {
                foreach (var (compositeKey, entry) in additionalToSave)
                {
                    s.TrustedHostKeysAdditional[compositeKey] = entry;
                }
            }).ConfigureAwait(false);
        }

        if (policyEntries is not null
            && (policyEntries.CertAuthorities.Count > 0 || policyEntries.RevokedKeys.Count > 0))
        {
            foreach (var ca in policyEntries.CertAuthorities)
            {
                _hostKeyTrustService.AddCertAuthority(ca.HostPatterns, ca.CaFingerprint);
            }

            foreach (var revoked in policyEntries.RevokedKeys)
            {
                _hostKeyTrustService.AddRevokedKey(revoked.HostPatterns, revoked.Fingerprint);
            }

            var caRules = _hostKeyTrustService.GetCertAuthorities()
                .Select(static r => new KnownHostsPolicyRuleDto(r.HostPatterns, r.Fingerprint))
                .ToList();
            var revokedRules = _hostKeyTrustService.GetRevokedKeys()
                .Select(static r => new KnownHostsPolicyRuleDto(r.HostPatterns, r.Fingerprint))
                .ToList();

            await _config.MergeSettingAsync(s =>
            {
                s.TrustedCertAuthorities = caRules;
                s.RevokedHostKeys = revokedRules;
            }).ConfigureAwait(false);
        }

        return new KnownHostsImportOutcome(imported, skippedExisting, skippedConflict);
    }

    /// <summary>
    /// Resolves the fingerprint currently trusted for a host. The settings dictionary
    /// only answers an exact <c>host:port</c> lookup; the trust service also matches
    /// hashed known_hosts entries, which would otherwise let a plain import shadow a
    /// hashed one without a conflict.
    /// </summary>
    private string? ResolveStoredFingerprint(
        AppSettings settings,
        string host,
        int port,
        string? algorithm = null)
    {
        var hostPortKey = HostKeyFormats.MakeKey(host, port);
        var canonicalAlgo = KnownHostsParser.CanonicalizeKeyType(algorithm);

        if (!string.IsNullOrWhiteSpace(canonicalAlgo))
        {
            if (settings.TrustedHostKeysV2.TryGetValue(hostPortKey, out var v2Entry))
            {
                var storedAlgo = KnownHostsParser.CanonicalizeKeyType(v2Entry.Algorithm);
                if (string.Equals(storedAlgo, canonicalAlgo, StringComparison.Ordinal)
                    || string.Equals(storedAlgo, "unknown", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(storedAlgo))
                {
                    return v2Entry.Fingerprint;
                }

                if (settings.TrustedHostKeysAdditional.TryGetValue($"{hostPortKey}|{canonicalAlgo}", out var addEntry))
                {
                    return addEntry.Fingerprint;
                }

                return _hostKeyTrustService.GetEntry(host, port, canonicalAlgo)?.Fingerprint;
            }
        }

        if (settings.TrustedHostKeys.TryGetValue(hostPortKey, out var storedFingerprint))
        {
            return storedFingerprint;
        }

        return string.IsNullOrWhiteSpace(canonicalAlgo)
            ? _hostKeyTrustService.GetEntry(host, port)?.Fingerprint
            : _hostKeyTrustService.GetEntry(host, port, canonicalAlgo)?.Fingerprint;
    }

    private sealed record IndexedCandidate(int Index, KnownHostsImportCandidate Candidate);
}

public enum KnownHostsCandidateStatus
{
    New,
    Existing,
    Conflict
}

public sealed record KnownHostsPreviewRow(
    KnownHostsImportCandidate Candidate,
    KnownHostsCandidateStatus Status,
    string? ExistingFingerprint);

public sealed record KnownHostsImportPreview(
    IReadOnlyList<KnownHostsPreviewRow> Rows,
    IReadOnlyList<KnownHostsImportDiagnostic> Diagnostics,
    KnownHostsPolicyEntries? PolicyEntries = null);

public sealed record KnownHostsImportOutcome(
    int Imported,
    int SkippedExisting,
    int SkippedConflict);
