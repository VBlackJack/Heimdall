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

using System.Security.Cryptography;
using System.Text;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh.Tests;

public sealed class KnownHostsImportExportTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "heimdall-known-hosts-import-export",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Fact]
    public void ImportFile_EmptyKnownHosts_ReturnsEmptyReport()
    {
        var path = WriteKnownHosts(string.Empty);
        var (_, service) = CreateService();
        var importer = new KnownHostsImporter(service);

        var report = importer.ImportFile(path, DateTimeOffset.Parse("2026-04-24T10:15:00Z"));

        Assert.Equal(0, report.Imported);
        Assert.Equal(0, report.Matched);
        Assert.Empty(report.Conflicts);
        Assert.Empty(service.GetAllEntries());
    }

    [Fact]
    public void Import_NewEntries_StoresImportedMetadataAndPublicKey()
    {
        var keyBlob = new byte[] { 0x01, 0x02, 0x03 };
        var path = WriteKnownHosts($"new.example.com ssh-ed25519 {ToKey(keyBlob)}");
        var importedAt = DateTimeOffset.Parse("2026-04-24T10:15:00Z");
        var (_, service) = CreateService();
        var importer = new KnownHostsImporter(service);

        var report = importer.ImportFile(path, importedAt);

        var entry = service.GetEntry("new.example.com", 22);
        Assert.NotNull(entry);
        Assert.Equal(1, report.Imported);
        Assert.Equal(HostKeySource.ImportedKnownHosts, entry.Source);
        Assert.Equal(importedAt, entry.FirstSeen);
        Assert.Equal("ssh-ed25519", entry.Algorithm);
        Assert.Equal(ToKey(keyBlob), entry.PublicKeyBase64);
    }

    [Fact]
    public void Import_MatchingEntry_UpdatesLastSeenWithoutConflict()
    {
        var keyBlob = new byte[] { 0x04, 0x05, 0x06 };
        var path = WriteKnownHosts($"match.example.com ssh-ed25519 {ToKey(keyBlob)}");
        var (store, service) = CreateService();
        var oldLastSeen = DateTimeOffset.Parse("2026-04-23T10:15:00Z");
        store.TrustEntry(
            "match.example.com",
            22,
            new HostKeyEntry(
                HostKeyFormats.ComputeSha256Fingerprint(keyBlob),
                oldLastSeen.AddDays(-1),
                oldLastSeen,
                "ssh-rsa",
                HostKeySource.UserConfirmed));
        var importer = new KnownHostsImporter(service);

        var report = importer.ImportFile(path, DateTimeOffset.Parse("2026-04-24T10:15:00Z"));

        var entry = service.GetEntry("match.example.com", 22);
        Assert.NotNull(entry);
        Assert.Equal(0, report.Imported);
        Assert.Equal(1, report.Matched);
        Assert.Empty(report.Conflicts);
        Assert.True(entry.LastSeen > oldLastSeen);
        Assert.Equal(HostKeySource.UserConfirmed, entry.Source);
    }

    [Fact]
    public void Import_ConflictingEntry_ReportsConflictAndLeavesStoreUnchanged()
    {
        var path = WriteKnownHosts($"conflict.example.com ssh-ed25519 {ToKey([0x07, 0x08, 0x09])}");
        var (store, service) = CreateService();
        var existing = new HostKeyEntry(
            HostKeyFormats.ComputeSha256Fingerprint([0x01, 0x01, 0x01]),
            DateTimeOffset.Parse("2026-04-23T10:15:00Z"),
            DateTimeOffset.Parse("2026-04-23T10:15:00Z"),
            "ssh-rsa",
            HostKeySource.UserConfirmed);
        store.TrustEntry("conflict.example.com", 22, existing);
        var importer = new KnownHostsImporter(service);

        var report = importer.ImportFile(path, DateTimeOffset.Parse("2026-04-24T10:15:00Z"));

        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal("conflict.example.com", conflict.Host);
        Assert.Equal(existing.Fingerprint, conflict.ExistingFingerprint);
        Assert.Equal(existing, service.GetEntry("conflict.example.com", 22));
    }

    [Fact]
    public void Import_HashedEntry_CanMatchPlainHostDuringVerification()
    {
        var keyBlob = new byte[] { 0x0A, 0x0B, 0x0C };
        var hashedHost = CreateHashedHost("hashed.example.com", [0x01, 0x02, 0x03, 0x04]);
        var path = WriteKnownHosts($"{hashedHost} ssh-ed25519 {ToKey(keyBlob)}");
        var (_, service) = CreateService();
        var importer = new KnownHostsImporter(service);

        importer.ImportFile(path, DateTimeOffset.Parse("2026-04-24T10:15:00Z"));
        var result = service.Verify(
            "hashed.example.com",
            22,
            HostKeyFormats.ComputeSha256Fingerprint(keyBlob),
            "ssh-ed25519");

        Assert.True(result.Trusted);
        Assert.False(result.FirstUse);
        Assert.NotNull(service.GetEntry("hashed.example.com", 22));
        Assert.Contains(service.GetAllEntries(), item => item.HostPort.StartsWith("|1|", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hashed.example.com", 2222)]
    [InlineData("2001:db8::17", 2200)]
    public void Import_HashedNonStandardPortEntry_MatchesOnlyCanonicalHostToken(string host, int port)
    {
        byte[] keyBlob = [0x1A, 0x1B, 0x1C];
        string canonicalHostToken = $"[{host}]:{port}";
        string hashedHost = CreateHashedHost(canonicalHostToken, [0x11, 0x12, 0x13, 0x14]);
        string path = WriteKnownHosts($"{hashedHost} ssh-ed25519 {ToKey(keyBlob)}");
        HostKeyTrustService service = CreateService().Service;
        KnownHostsImporter importer = new(service);
        string fingerprint = HostKeyFormats.ComputeSha256Fingerprint(keyBlob);

        importer.ImportFile(path, DateTimeOffset.Parse("2026-04-24T10:15:00Z"));
        HostKeyVerifyResult matchingResult = service.Verify(host, port, fingerprint, "ssh-ed25519");
        HostKeyVerifyResult differentPortResult = service.Verify(host, port + 1, fingerprint, "ssh-ed25519");

        Assert.True(matchingResult.Trusted);
        Assert.False(matchingResult.FirstUse);
        Assert.NotNull(service.GetEntry(host, port));
        Assert.True(differentPortResult.Trusted);
        Assert.True(differentPortResult.FirstUse);
        Assert.Null(service.GetEntry(host, port + 1));
        Assert.Single(service.GetAllEntries());
        Assert.Contains(service.GetAllEntries(), item => item.HostPort.StartsWith("|1|", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportFile_NewFile_WritesAllEntriesWithPublicKeys()
    {
        var path = Path.Combine(_rootPath, "new-known-hosts");
        var keyBlob = new byte[] { 0x0D, 0x0E, 0x0F };
        var (_, service) = CreateService();
        service.Import(
            "export.example.com",
            2222,
            HostKeyFormats.ComputeSha256Fingerprint(keyBlob),
            "ssh-ed25519",
            DateTimeOffset.Parse("2026-04-24T10:15:00Z"),
            ToKey(keyBlob));
        var exporter = new KnownHostsExporter(service);

        var report = exporter.ExportFile(path);

        var line = Assert.Single(File.ReadAllLines(path, Encoding.UTF8));
        Assert.Equal($"[export.example.com]:2222 ssh-ed25519 {ToKey(keyBlob)}", line);
        Assert.Equal(1, report.Written);
        Assert.Equal(0, report.SkippedWithoutPublicKey);
    }

    [Fact]
    public void ExportFile_ExistingFile_PreservesNonHeimdallLinesVerbatim()
    {
        var managedKey = new byte[] { 0x10, 0x11, 0x12 };
        var oldManagedKey = new byte[] { 0x13, 0x14, 0x15 };
        var hashedLine = $"{CreateHashedHost("manual.example.com", [0x05, 0x06, 0x07, 0x08])} ssh-ed25519 {ToKey([0x16])}";
        const string commentLine = "# managed by someone else";
        const string certLine = "@cert-authority *.example.com ssh-ed25519 AAAAmanual";
        var unmanagedLine = $"unmanaged.example.com ssh-rsa {ToKey([0x17, 0x18])}";
        var path = WriteKnownHosts(string.Join(Environment.NewLine,
            commentLine,
            hashedLine,
            certLine,
            unmanagedLine,
            $"managed.example.com ssh-rsa {ToKey(oldManagedKey)}"));
        var (_, service) = CreateService();
        service.Import(
            "managed.example.com",
            22,
            HostKeyFormats.ComputeSha256Fingerprint(managedKey),
            "ssh-ed25519",
            DateTimeOffset.Parse("2026-04-24T10:15:00Z"),
            ToKey(managedKey));
        var exporter = new KnownHostsExporter(service);

        exporter.ExportFile(path);

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Contains(commentLine, lines);
        Assert.Contains(hashedLine, lines);
        Assert.Contains(certLine, lines);
        Assert.Contains(unmanagedLine, lines);
        Assert.Contains($"managed.example.com ssh-ed25519 {ToKey(managedKey)}", lines);
        Assert.DoesNotContain($"managed.example.com ssh-rsa {ToKey(oldManagedKey)}", lines);
    }

    [Fact]
    public void ExportFile_WriteFailure_LeavesExistingFileIntact()
    {
        var path = WriteKnownHosts("original.example.com ssh-ed25519 AAAA");
        var (_, service) = CreateService();
        service.Import(
            "new.example.com",
            22,
            HostKeyFormats.ComputeSha256Fingerprint([0x19]),
            "ssh-ed25519",
            DateTimeOffset.Parse("2026-04-24T10:15:00Z"),
            ToKey([0x19]));
        var exporter = new KnownHostsExporter(
            service,
            (_, _) => throw new IOException("simulated temp write failure"));

        Assert.Throws<IOException>(() => exporter.ExportFile(path));

        Assert.Equal("original.example.com ssh-ed25519 AAAA", File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void ExportThenImport_RestoresFingerprints()
    {
        var path = Path.Combine(_rootPath, "round-trip-known-hosts");
        var keyBlob = new byte[] { 0x20, 0x21, 0x22 };
        var (_, sourceService) = CreateService();
        sourceService.Import(
            "roundtrip.example.com",
            22,
            HostKeyFormats.ComputeSha256Fingerprint(keyBlob),
            "ssh-ed25519",
            DateTimeOffset.Parse("2026-04-24T10:15:00Z"),
            ToKey(keyBlob));
        new KnownHostsExporter(sourceService).ExportFile(path);
        var (_, targetService) = CreateService();

        new KnownHostsImporter(targetService).ImportFile(path, DateTimeOffset.Parse("2026-04-24T10:20:00Z"));

        Assert.Equal(
            HostKeyFormats.ComputeSha256Fingerprint(keyBlob),
            targetService.GetEntry("roundtrip.example.com", 22)?.Fingerprint);
    }

    [Fact]
    public void ImportFile_NonExistentPath_ReturnsEmptyReportWithoutThrowing()
    {
        var (_, service) = CreateService();
        var importer = new KnownHostsImporter(service);
        var missing = Path.Combine(_rootPath, "does-not-exist", "known_hosts");

        var report = importer.ImportFile(missing);

        Assert.Equal(0, report.Imported);
        Assert.Empty(report.Conflicts);
    }

    [Fact]
    public void ImportFile_OversizedFile_RejectedWithoutThrowing()
    {
        Directory.CreateDirectory(_rootPath);
        var path = Path.Combine(_rootPath, "oversized_known_hosts");
        // Write a sparse file slightly larger than the cap. Use a single
        // SetLength call to avoid materializing the bytes.
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(KnownHostsParser.MaxFileSizeBytes + 1);
        }

        var (_, service) = CreateService();
        var importer = new KnownHostsImporter(service);

        var report = importer.ImportFile(path);

        Assert.Equal(0, report.Imported);
        Assert.Empty(report.Conflicts);
        Assert.Empty(service.GetAllEntries());
    }

    [Fact]
    public void ImportFile_LongLineInOtherwiseValidFile_EmitsDiagnosticsAndContinues()
    {
        var keyBlob = new byte[] { 0x10, 0x11, 0x12 };
        var key = ToKey(keyBlob);
        var hugeLine = new string('a', KnownHostsParser.MaxLineLength + 16);
        var content = string.Join(
            Environment.NewLine,
            $"good.example.com ssh-ed25519 {key}",
            hugeLine,
            $"alsogood.example.com ssh-ed25519 {key}");
        var path = WriteKnownHosts(content);

        var (_, service) = CreateService();
        var importer = new KnownHostsImporter(service);

        var report = importer.ImportFile(path);

        Assert.Equal(2, report.Imported);
        Assert.NotNull(service.GetEntry("good.example.com", 22));
        Assert.NotNull(service.GetEntry("alsogood.example.com", 22));
    }

    [Fact]
    public void Import_ConflictingEntry_CarriesImportedPublicKeyOnConflict()
    {
        byte[] importedKey = [0x21, 0x22, 0x23];
        string path = WriteKnownHosts($"conflict.example.com ssh-ed25519 {ToKey(importedKey)}");
        (_, HostKeyTrustService service) = CreateService();
        service.Trust("conflict.example.com", 22, "SHA256:existing", "ssh-ed25519", HostKeySource.UserConfirmed, "AAAAexisting");
        KnownHostsImporter importer = new(service);

        KnownHostsImportReport report = importer.ImportFile(path, DateTimeOffset.Parse("2026-04-24T10:15:00Z"));

        KnownHostsImportConflict conflict = Assert.Single(report.Conflicts);
        Assert.Equal(ToKey(importedKey), conflict.ImportedPublicKeyBase64);
    }

    [Fact]
    public void ExportFile_WritesUtf8WithoutByteOrderMark()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "known_hosts");
        (_, HostKeyTrustService service) = CreateService();
        service.Import(
            "host.example.com",
            22,
            HostKeyFormats.ComputeSha256Fingerprint([0x31, 0x32]),
            "ssh-ed25519",
            DateTimeOffset.Parse("2026-04-24T10:15:00Z"),
            ToKey([0x31, 0x32]));

        new KnownHostsExporter(service).ExportFile(path);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 3);
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal((byte)'h', bytes[0]);
    }

    private string WriteKnownHosts(string content)
    {
        Directory.CreateDirectory(_rootPath);
        var path = Path.Combine(_rootPath, "known_hosts");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static (HostKeyStore Store, HostKeyTrustService Service) CreateService()
    {
        var store = new HostKeyStore();
        return (store, new HostKeyTrustService(store));
    }

    private static string ToKey(byte[] keyBlob) => Convert.ToBase64String(keyBlob);

    private static string CreateHashedHost(string host, byte[] salt)
    {
        using var hmac = new HMACSHA1(salt);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(host));
        return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";
    }
}
