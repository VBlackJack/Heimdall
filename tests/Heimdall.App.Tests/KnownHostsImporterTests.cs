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

using Heimdall.App.Services.Import;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

public sealed class KnownHostsImporterTests
{
    [Fact]
    public async Task BuildPreview_NewEntry_WhenNotInStore()
    {
        var importer = CreateImporter(out _, out _);

        var preview = await importer.BuildPreviewAsync(new KnownHostsParseResult(
            [CreateRawEntry("host", 22, [0x01, 0x02, 0x03])],
            []));

        var row = Assert.Single(preview.Rows);
        Assert.Equal(KnownHostsCandidateStatus.New, row.Status);
    }

    [Fact]
    public async Task BuildPreview_ExistingEntry_WhenFingerprintMatchesStore()
    {
        var importer = CreateImporter(out var config, out _);
        var fingerprint = HostKeyFormats.ComputeSha256Fingerprint([0x01, 0x02, 0x03]);
        config.SetTrustedHostKey(HostKeyFormats.MakeKey("host", 22), fingerprint);

        var preview = await importer.BuildPreviewAsync(new KnownHostsParseResult(
            [CreateRawEntry("host", 22, [0x01, 0x02, 0x03])],
            []));

        var row = Assert.Single(preview.Rows);
        Assert.Equal(KnownHostsCandidateStatus.Existing, row.Status);
        Assert.Equal(fingerprint, row.ExistingFingerprint);
    }

    [Fact]
    public async Task BuildPreview_ConflictVsStore_WhenDifferentFingerprint()
    {
        var importer = CreateImporter(out var config, out _);
        config.SetTrustedHostKey(
            HostKeyFormats.MakeKey("host", 22),
            HostKeyFormats.ComputeSha256Fingerprint([0x09, 0x09, 0x09]));

        var preview = await importer.BuildPreviewAsync(new KnownHostsParseResult(
            [CreateRawEntry("host", 22, [0x01, 0x02, 0x03])],
            []));

        var row = Assert.Single(preview.Rows);
        Assert.Equal(KnownHostsCandidateStatus.Conflict, row.Status);
        Assert.NotNull(row.ExistingFingerprint);
    }

    [Fact]
    public async Task BuildPreview_IntraFileConflict_AllRowsConflict()
    {
        var importer = CreateImporter(out _, out _);

        var preview = await importer.BuildPreviewAsync(new KnownHostsParseResult(
        [
            CreateRawEntry("host", 22, [0x01, 0x02, 0x03], 1),
            CreateRawEntry("host", 22, [0x04, 0x05, 0x06], 2)
        ],
        []));

        Assert.Equal(2, preview.Rows.Count);
        Assert.All(preview.Rows, row => Assert.Equal(KnownHostsCandidateStatus.Conflict, row.Status));
        Assert.Contains(preview.Diagnostics, diagnostic => diagnostic.Code == KnownHostsDiagnosticCode.IntraFileFingerprintConflict);
    }

    [Fact]
    public async Task ImportSelectedAsync_PersistsOnlyNew_AndCallsHostKeyStoreTrust_OncePerImported()
    {
        var importer = CreateImporter(out var config, out var store);
        var existingFingerprint = HostKeyFormats.ComputeSha256Fingerprint([0x09, 0x09, 0x09]);
        config.SetTrustedHostKey(HostKeyFormats.MakeKey("existing", 22), existingFingerprint);

        var newCandidate = new KnownHostsImportCandidate
        {
            Host = "new-host",
            Port = 22,
            Fingerprint = HostKeyFormats.ComputeSha256Fingerprint([0x01, 0x02, 0x03]),
            SourceLineNumber = 1
        };

        var existingCandidate = new KnownHostsImportCandidate
        {
            Host = "existing",
            Port = 22,
            Fingerprint = existingFingerprint,
            SourceLineNumber = 2
        };

        var outcome = await importer.ImportSelectedAsync([newCandidate, existingCandidate]);

        Assert.Equal(1, outcome.Imported);
        Assert.Equal(1, outcome.SkippedExisting);
        Assert.Equal(0, outcome.SkippedConflict);
        Assert.Equal(newCandidate.Fingerprint, store.GetFingerprint("new-host", 22));
        Assert.Equal(HostKeySource.ImportedKnownHosts, store.GetEntry("new-host", 22)?.Source);
    }

    [Fact]
    public async Task ImportSelectedAsync_ImportedEntryPreservesKnownHostsMetadata()
    {
        var importer = CreateImporter(out var config, out var store);
        var keyBytes = new byte[] { 0x0A, 0x0B, 0x0C };
        var preview = await importer.BuildPreviewAsync(new KnownHostsParseResult(
            [CreateRawEntry("metadata-host", 2222, keyBytes)],
            []));
        var candidate = Assert.Single(preview.Rows).Candidate;

        var outcome = await importer.ImportSelectedAsync([candidate]);

        var entry = store.GetEntry("metadata-host", 2222);
        var settings = await config.LoadSettingsAsync();
        var persisted = settings.TrustedHostKeysV2[HostKeyFormats.MakeKey("metadata-host", 2222)];
        Assert.Equal(1, outcome.Imported);
        Assert.NotNull(entry);
        Assert.Equal(HostKeySource.ImportedKnownHosts, entry.Source);
        Assert.Equal("ssh-ed25519", entry.Algorithm);
        Assert.Equal(Convert.ToBase64String(keyBytes), entry.PublicKeyBase64);
        Assert.Equal(HostKeySource.ImportedKnownHosts, persisted.Source);
        Assert.Equal("ssh-ed25519", persisted.Algorithm);
        Assert.Equal(Convert.ToBase64String(keyBytes), persisted.PublicKeyBase64);
    }

    [Fact]
    public async Task BuildPreview_ConflictVsHashedStoreEntry_WhenDifferentFingerprint()
    {
        KnownHostsImporter importer = CreateImporter(out _, out HostKeyStore store);
        string storedFingerprint = HostKeyFormats.ComputeSha256Fingerprint([0x09, 0x09, 0x09]);
        store.TrustEntry(
            CreateHashedHost("host", [0x01, 0x02, 0x03, 0x04]),
            22,
            new HostKeyEntry(
                storedFingerprint,
                DateTimeOffset.UtcNow.AddDays(-2),
                DateTimeOffset.UtcNow.AddDays(-1),
                "ssh-ed25519",
                HostKeySource.ImportedKnownHosts));

        KnownHostsImportPreview preview = await importer.BuildPreviewAsync(new KnownHostsParseResult(
            [CreateRawEntry("host", 22, [0x01, 0x02, 0x03])],
            []));

        KnownHostsPreviewRow row = Assert.Single(preview.Rows);
        Assert.Equal(KnownHostsCandidateStatus.Conflict, row.Status);
        Assert.Equal(storedFingerprint, row.ExistingFingerprint);
    }

    [Fact]
    public async Task ImportSelectedAsync_SkipsCandidateConflictingWithHashedStoreEntry()
    {
        KnownHostsImporter importer = CreateImporter(out InMemoryConfigManager config, out HostKeyStore store);
        string storedFingerprint = HostKeyFormats.ComputeSha256Fingerprint([0x09, 0x09, 0x09]);
        store.TrustEntry(
            CreateHashedHost("host", [0x01, 0x02, 0x03, 0x04]),
            22,
            new HostKeyEntry(
                storedFingerprint,
                DateTimeOffset.UtcNow.AddDays(-2),
                DateTimeOffset.UtcNow.AddDays(-1),
                "ssh-ed25519",
                HostKeySource.ImportedKnownHosts));
        byte[] importedKey = [0x01, 0x02, 0x03];
        KnownHostsImportCandidate candidate = new()
        {
            Host = "host",
            Port = 22,
            Fingerprint = HostKeyFormats.ComputeSha256Fingerprint(importedKey),
            Algorithm = "ssh-ed25519",
            PublicKeyBase64 = Convert.ToBase64String(importedKey),
            SourceLineNumber = 1
        };

        KnownHostsImportOutcome outcome = await importer.ImportSelectedAsync([candidate]);

        Assert.Equal(0, outcome.Imported);
        Assert.Equal(1, outcome.SkippedConflict);
        Assert.Equal(storedFingerprint, new HostKeyTrustService(store).GetEntry("host", 22)?.Fingerprint);
        Assert.False((await config.LoadSettingsAsync()).TrustedHostKeys.ContainsKey("host:22"));
    }

    private static string CreateHashedHost(string host, byte[] salt)
    {
        using System.Security.Cryptography.HMACSHA1 hmac = new(salt);
        byte[] hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(host));
        return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";
    }

    private static KnownHostsImporter CreateImporter(out InMemoryConfigManager config, out HostKeyStore store)
    {
        config = new InMemoryConfigManager();
        store = new HostKeyStore();
        return new KnownHostsImporter(config, store);
    }

    private static KnownHostsRawEntry CreateRawEntry(string host, int port, byte[] keyBytes, int lineNumber = 1)
    {
        return new KnownHostsRawEntry
        {
            Host = host,
            Port = port,
            KeyType = "ssh-ed25519",
            Base64Key = keyBytes,
            SourceLineNumber = lineNumber
        };
    }
}
