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

using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.Ssh;

public sealed class HostKeyTrustService(HostKeyStore store) : IHostKeyTrustService
{
    private readonly HostKeyStore _store = store;

    public event Action<string, HostKeyEntry>? EntryTrusted;

    public event Action<string>? EntryRemoved;

    public event Action<string, HostKeyEntry, HostKeyEntry>? EntryReplaced;

    public HostKeyEntry? GetEntry(string host, int port)
        => TryFindStoredEntry(host, port, out _, out var entry) ? entry : null;

    public HostKeyEntry? GetEffectiveEntry(string host, int port)
        => _store.GetSessionEntry(host, port) ?? GetEntry(host, port);

    private bool TryFindStoredEntry(
        string host,
        int port,
        out string hostPort,
        out HostKeyEntry entry)
    {
        hostPort = HostKeyFormats.MakeKey(host, port);
        var direct = _store.GetEntry(host, port);
        if (direct is not null)
        {
            entry = direct;
            return true;
        }

        string hashedHostCandidate = port == 22 ? host : $"[{host}]:{port}";
        foreach (var (storedHostPort, storedEntry) in _store.GetAllEntries())
        {
            if (!HostKeyFormats.TryParseKey(storedHostPort, out var storedHost, out _)
                || !storedHost.StartsWith("|1|", StringComparison.Ordinal))
            {
                continue;
            }

            if (KnownHostsHash.TryMatches(storedHost, hashedHostCandidate))
            {
                hostPort = storedHostPort;
                entry = storedEntry;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    public IReadOnlyList<(string HostPort, HostKeyEntry Entry)> GetAllEntries()
    {
        return _store.GetAllEntries()
            .Select(static kvp => (kvp.Key, kvp.Value))
            .ToList();
    }

    public HostKeyVerifyResult Verify(
        string host,
        int port,
        string presentedFingerprint,
        string algorithm)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(presentedFingerprint);

        var sessionEntry = _store.GetSessionEntry(host, port);
        if (sessionEntry is not null
            && HostKeyStore.ConstantTimeEquals(sessionEntry.Fingerprint, presentedFingerprint))
        {
            return new HostKeyVerifyResult(
                Trusted: true,
                FirstUse: false,
                presentedFingerprint,
                sessionEntry.Fingerprint);
        }

        if (!TryFindStoredEntry(host, port, out var hostPort, out var existing))
        {
            if (sessionEntry is not null)
            {
                return new HostKeyVerifyResult(
                    Trusted: false,
                    FirstUse: false,
                    presentedFingerprint,
                    sessionEntry.Fingerprint);
            }

            return new HostKeyVerifyResult(
                Trusted: true,
                FirstUse: true,
                presentedFingerprint,
                StoredFingerprint: null);
        }

        var match = HostKeyStore.ConstantTimeEquals(existing.Fingerprint, presentedFingerprint);
        if (!match)
        {
            return new HostKeyVerifyResult(
                Trusted: false,
                FirstUse: false,
                presentedFingerprint,
                existing.Fingerprint);
        }

        _store.SetEntry(
            hostPort,
            existing with
            {
                LastSeen = DateTimeOffset.UtcNow,
                Algorithm = NormalizeAlgorithm(algorithm, existing.Algorithm)
            },
            raiseEvent: true);

        return new HostKeyVerifyResult(
            Trusted: true,
            FirstUse: false,
            presentedFingerprint,
            existing.Fingerprint);
    }

    public void TrustForSession(
        string host,
        int port,
        string fingerprint,
        string algorithm,
        string? publicKeyBase64 = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fingerprint);

        _store.TrustForSession(
            host,
            port,
            fingerprint,
            NormalizeAlgorithm(algorithm),
            publicKeyBase64);
    }

    public void Trust(
        string host,
        int port,
        string fingerprint,
        string algorithm,
        HostKeySource source,
        string? publicKeyBase64 = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var now = DateTimeOffset.UtcNow;
        Store(
            host,
            port,
            fingerprint,
            publicKeyBase64,
            existing => new HostKeyEntry(
                fingerprint,
                existing?.FirstSeen > DateTimeOffset.MinValue ? existing.FirstSeen : now,
                now,
                NormalizeAlgorithm(algorithm, existing?.Algorithm),
                source));
    }

    public void Import(
        string host,
        int port,
        string fingerprint,
        string algorithm,
        DateTimeOffset importedAt,
        string? publicKeyBase64 = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fingerprint);

        Store(
            host,
            port,
            fingerprint,
            publicKeyBase64,
            existing => new HostKeyEntry(
                fingerprint,
                importedAt,
                importedAt,
                NormalizeAlgorithm(algorithm, existing?.Algorithm),
                HostKeySource.ImportedKnownHosts));
    }

    /// <summary>
    /// Writes a durable entry under the plain <c>host:port</c> key and reconciles it
    /// with whatever the store already held for that host, hashed entries included.
    /// The previous public key blob is carried over only while the fingerprint is
    /// unchanged: a replaced key gets the caller's blob or none, never the blob of
    /// the key the user just decided against, which the known_hosts export would
    /// otherwise write back out under the new fingerprint.
    /// </summary>
    private void Store(
        string host,
        int port,
        string fingerprint,
        string? publicKeyBase64,
        Func<HostKeyEntry?, HostKeyEntry> build)
    {
        var hostPort = HostKeyFormats.MakeKey(host, port);
        HostKeyEntry? existing = TryFindStoredEntry(host, port, out var existingHostPort, out var found)
            ? found
            : null;
        bool fingerprintChanged = existing is not null
            && !HostKeyStore.ConstantTimeEquals(existing.Fingerprint, fingerprint);

        var entry = build(existing) with
        {
            PublicKeyBase64 = fingerprintChanged
                ? publicKeyBase64
                : publicKeyBase64 ?? existing?.PublicKeyBase64
        };

        _store.SetEntry(host, port, entry, raiseEvent: true);

        bool matchedHashedEntry = existing is not null
            && !string.Equals(existingHostPort, hostPort, StringComparison.Ordinal);
        if (matchedHashedEntry && _store.RemoveKey(existingHostPort))
        {
            // The plain entry now carries the trust; leaving the hashed one behind
            // would resurrect the old fingerprint as soon as the plain row is removed.
            EntryRemoved?.Invoke(existingHostPort);
        }

        if (fingerprintChanged)
        {
            EntryReplaced?.Invoke(hostPort, existing!, entry);
        }
        else
        {
            EntryTrusted?.Invoke(hostPort, entry);
        }
    }

    public bool Remove(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);

        var hostPort = HostKeyFormats.MakeKey(host, port);
        if (!_store.Remove(host, port))
        {
            return false;
        }

        EntryRemoved?.Invoke(hostPort);
        return true;
    }

    private static string NormalizeAlgorithm(string? candidate, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(candidate) && !string.Equals(candidate, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            // Algorithms outside the OpenSSH allow-list are kept (back-compat with
            // legacy entries) but logged so an operator can spot tampering or drift.
            if (!KnownHostsParser.IsSupportedKeyType(candidate))
            {
                FileLogger.Warn(
                    $"HostKeyTrustService: unrecognized host key algorithm '{candidate}' accepted; "
                    + "verify the source of this entry.");
            }

            return KnownHostsParser.CanonicalizeKeyType(candidate);
        }

        return string.IsNullOrWhiteSpace(fallback) ? "unknown" : fallback;
    }
}
