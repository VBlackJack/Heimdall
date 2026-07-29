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

using Heimdall.Core.Ssh;

namespace Heimdall.Ssh.Tests;

public class HostKeyTrustServiceTests
{
    private const string Host = "host.example.com";
    private const int Port = 22;

    private readonly HostKeyStore _store = new();
    private readonly HostKeyTrustService _service;

    public HostKeyTrustServiceTests()
    {
        _service = new HostKeyTrustService(_store);
    }

    [Fact]
    public void Verify_MatchingFingerprint_UpdatesLastSeen()
    {
        var originalLastSeen = DateTimeOffset.UtcNow.AddDays(-1);
        _store.TrustEntry(
            Host,
            Port,
            new HostKeyEntry(
                "SHA256:abc123",
                DateTimeOffset.UtcNow.AddDays(-2),
                originalLastSeen,
                "unknown",
                HostKeySource.UserConfirmed));

        var result = _service.Verify(Host, Port, "SHA256:abc123", "ssh-ed25519");

        Assert.True(result.Trusted);
        Assert.False(result.FirstUse);
        var entry = _service.GetEntry(Host, Port);
        Assert.NotNull(entry);
        Assert.True(entry.LastSeen > originalLastSeen);
        Assert.Equal("ssh-ed25519", entry.Algorithm);
    }

    [Fact]
    public void Verify_MatchingFingerprint_EmitsPersistableStoreMutation()
    {
        var originalLastSeen = DateTimeOffset.UtcNow.AddDays(-1);
        _store.TrustEntry(
            Host,
            Port,
            new HostKeyEntry(
                "SHA256:abc123",
                DateTimeOffset.UtcNow.AddDays(-2),
                originalLastSeen,
                "unknown",
                HostKeySource.UserConfirmed));
        (string Key, string Fingerprint, bool Trusted)? mutation = null;
        _store.HostKeyEvent += (key, fingerprint, trusted) =>
            mutation = (key, fingerprint, trusted);

        _service.Verify(Host, Port, "SHA256:abc123", "ssh-ed25519");

        Assert.NotNull(mutation);
        Assert.Equal($"{Host}:{Port}", mutation.Value.Key);
        Assert.Equal("SHA256:abc123", mutation.Value.Fingerprint);
        Assert.True(mutation.Value.Trusted);
    }

    [Fact]
    public void Verify_FirstUse_DoesNotStoreEntry()
    {
        var result = _service.Verify(Host, Port, "SHA256:abc123", "ssh-ed25519");

        Assert.True(result.Trusted);
        Assert.True(result.FirstUse);
        Assert.Null(_service.GetEntry(Host, Port));
    }

    [Fact]
    public void Verify_Mismatch_DoesNotTouchStoredEntry()
    {
        var firstSeen = DateTimeOffset.UtcNow.AddDays(-2);
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-1);
        _store.TrustEntry(
            Host,
            Port,
            new HostKeyEntry("SHA256:old", firstSeen, lastSeen, "ssh-rsa", HostKeySource.UserConfirmed));

        var result = _service.Verify(Host, Port, "SHA256:new", "ssh-ed25519");

        Assert.False(result.Trusted);
        Assert.False(result.FirstUse);
        Assert.Equal("SHA256:old", result.StoredFingerprint);
        Assert.Equal(lastSeen, _service.GetEntry(Host, Port)!.LastSeen);
        Assert.Equal("ssh-rsa", _service.GetEntry(Host, Port)!.Algorithm);
    }

    [Fact]
    public void Trust_EmitsEntryTrustedWithMetadata()
    {
        (string HostPort, HostKeyEntry Entry)? trusted = null;
        _service.EntryTrusted += (hostPort, entry) => trusted = (hostPort, entry);

        _service.Trust(Host, Port, "SHA256:abc123", "ssh-ed25519", HostKeySource.UserConfirmed);

        Assert.NotNull(trusted);
        Assert.Equal($"{Host}:{Port}", trusted.Value.HostPort);
        Assert.Equal("SHA256:abc123", trusted.Value.Entry.Fingerprint);
        Assert.Equal("ssh-ed25519", trusted.Value.Entry.Algorithm);
        Assert.Equal(HostKeySource.UserConfirmed, trusted.Value.Entry.Source);
    }

    [Fact]
    public void Trust_ReplacingDifferentFingerprint_EmitsEntryReplaced()
    {
        _service.Trust(Host, Port, "SHA256:old", "ssh-rsa", HostKeySource.UserConfirmed);
        (string HostPort, HostKeyEntry OldEntry, HostKeyEntry NewEntry)? replaced = null;
        _service.EntryReplaced += (hostPort, oldEntry, newEntry) => replaced = (hostPort, oldEntry, newEntry);

        _service.Trust(Host, Port, "SHA256:new", "ssh-ed25519", HostKeySource.UserConfirmed);

        Assert.NotNull(replaced);
        Assert.Equal($"{Host}:{Port}", replaced.Value.HostPort);
        Assert.Equal("SHA256:old", replaced.Value.OldEntry.Fingerprint);
        Assert.Equal("SHA256:new", replaced.Value.NewEntry.Fingerprint);
    }

    [Fact]
    public void Import_SetsImportedKnownHostsSource()
    {
        var importedAt = DateTimeOffset.Parse("2026-04-24T10:15:00Z");

        _service.Import(Host, Port, "SHA256:abc123", "ssh-ed25519", importedAt);

        var entry = _service.GetEntry(Host, Port);
        Assert.NotNull(entry);
        Assert.Equal(HostKeySource.ImportedKnownHosts, entry.Source);
        Assert.Equal(importedAt, entry.FirstSeen);
        Assert.Equal(importedAt, entry.LastSeen);
    }

    [Fact]
    public void Remove_RemovesEntryAndEmitsEntryRemoved()
    {
        string? removed = null;
        _service.Trust(Host, Port, "SHA256:abc123", "ssh-ed25519", HostKeySource.UserConfirmed);
        _service.EntryRemoved += hostPort => removed = hostPort;

        var result = _service.Remove(Host, Port);

        Assert.True(result);
        Assert.Equal($"{Host}:{Port}", removed);
        Assert.Null(_service.GetEntry(Host, Port));
    }
}
