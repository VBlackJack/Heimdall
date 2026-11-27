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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh;

/// <summary>
/// Trust-On-First-Use host key verification store.
/// Stores host key entries per host:port, blocks on mismatch.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
public sealed class HostKeyStore
{
    private const string UnknownAlgorithm = "unknown";
    private readonly ConcurrentDictionary<string, HostKeyEntry> _trustedKeys = new();
    private readonly ConcurrentDictionary<string, HostKeyEntry> _sessionTrustedKeys = new();
    private readonly ConcurrentDictionary<string, HostKeyEntry> _additionalAlgorithmKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised whenever a host key is explicitly trusted.
    /// Parameters: host:port key, fingerprint, whether it was trusted.
    /// </summary>
    public event Action<string, string, bool>? HostKeyEvent;

    /// <summary>
    /// Raised when a trusted entry is removed by its stored key (the replacement of a
    /// hashed known_hosts entry). Parameter: the stored host:port key. It is raised on
    /// the store because the trust service that triggers it can be a detached instance
    /// whose own removal event reaches nobody.
    /// </summary>
    public event Action<string>? KeyRemoved;

    /// <summary>
    /// Compare two fingerprint strings without leaking the position of any
    /// differing byte through timing variations. Inputs are OpenSSH-style
    /// fingerprints (<c>SHA256:&lt;base64&gt;</c>) and so are ASCII in practice;
    /// the method itself does not enforce an ASCII charset.
    /// </summary>
    /// <remarks>
    /// The early-exit on length mismatch is acceptable here: OpenSSH host-key
    /// fingerprints are fixed at <c>SHA256:</c> + 43 base64 chars (= 50 chars),
    /// so an attacker observing the timing of a length-mismatch branch learns
    /// nothing useful. <b>Do not copy this pattern</b> to compare variable-length
    /// secrets - re-implement padded equal-length comparison there.
    /// </remarks>
    internal static bool ConstantTimeEquals(string a, string b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (a.Length != b.Length)
        {
            return false;
        }

        var aBytes = Encoding.ASCII.GetBytes(a);
        var bBytes = Encoding.ASCII.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    /// <summary>
    /// Populate the store from persisted gateway/server configurations.
    /// Called at startup before any connections are made.
    /// </summary>
    /// <remarks>
    /// Seeds only hosts the store does not already trust. A gateway fingerprint is a field of
    /// the inventory, which a profile import writes, while the trusted key list is what the user
    /// accepted: letting the first overwrite the second replaced a pinned key with whatever a
    /// shared file declared for the same host and port.
    /// </remarks>
    /// <param name="entries">Tuples of (host, port, fingerprint) from config.</param>
    public void LoadFromConfig(IEnumerable<(string host, int port, string? fingerprint)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var (host, port, fingerprint) in entries)
        {
            if (!string.IsNullOrWhiteSpace(fingerprint))
            {
                _trustedKeys.TryAdd(HostKeyFormats.MakeKey(host, port), CreateLegacyEntry(fingerprint));
            }
        }
    }

    /// <summary>
    /// Populate the store from persisted metadata-rich host key entries.
    /// Called at startup before any connections are made.
    /// </summary>
    public void LoadEntriesFromConfig(IEnumerable<(string host, int port, HostKeyEntry? entry)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var (host, port, entry) in entries)
        {
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Fingerprint))
            {
                _trustedKeys[HostKeyFormats.MakeKey(host, port)] = NormalizeEntry(entry);
            }
        }
    }

    /// <summary>
    /// Get the stored fingerprint for a host, or null if unknown.
    /// </summary>
    public string? GetFingerprint(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        return _trustedKeys.TryGetValue(HostKeyFormats.MakeKey(host, port), out var entry) ? entry.Fingerprint : null;
    }

    /// <summary>
    /// Get the stored metadata entry for a host, or null if unknown.
    /// </summary>
    public HostKeyEntry? GetEntry(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        return _trustedKeys.TryGetValue(HostKeyFormats.MakeKey(host, port), out var entry) ? entry : null;
    }

    /// <summary>
    /// Get the session-only metadata entry for a host, or null if none was
    /// trusted for this process lifetime.
    /// </summary>
    public HostKeyEntry? GetSessionEntry(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        return _sessionTrustedKeys.TryGetValue(HostKeyFormats.MakeKey(host, port), out var entry)
            ? entry
            : null;
    }

    /// <summary>
    /// Manually trust a host key (e.g., after user confirmation dialog).
    /// Trust uses upsert semantics and emits <see cref="HostKeyEvent"/> for persistence.
    /// </summary>
    public void Trust(string host, int port, string fingerprint)
        => Trust(host, port, fingerprint, UnknownAlgorithm, HostKeySource.UserConfirmed);

    /// <summary>
    /// Manually trust a host key with metadata.
    /// Trust uses upsert semantics and emits <see cref="HostKeyEvent"/> for persistence.
    /// </summary>
    public void Trust(
        string host,
        int port,
        string fingerprint,
        string algorithm,
        HostKeySource source)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var key = HostKeyFormats.MakeKey(host, port);
        _trustedKeys[key] = new HostKeyEntry(
            fingerprint,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(algorithm) ? UnknownAlgorithm : algorithm,
            source);
        _sessionTrustedKeys.TryRemove(key, out _);
        HostKeyEvent?.Invoke(key, fingerprint, true);
    }

    /// <summary>
    /// Trust a host key for the current process only. This does not write to
    /// persisted trust state and does not emit <see cref="HostKeyEvent"/>.
    /// </summary>
    public void TrustForSession(
        string host,
        int port,
        string fingerprint,
        string algorithm,
        string? publicKeyBase64 = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var now = DateTimeOffset.UtcNow;
        var key = HostKeyFormats.MakeKey(host, port);
        _sessionTrustedKeys[key] = new HostKeyEntry(
            fingerprint,
            now,
            now,
            string.IsNullOrWhiteSpace(algorithm) ? UnknownAlgorithm : algorithm,
            HostKeySource.UserConfirmed)
        { PublicKeyBase64 = publicKeyBase64 };
    }

    /// <summary>
    /// Store a pre-built host key entry.
    /// </summary>
    public void TrustEntry(string host, int port, HostKeyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(entry);

        var normalized = NormalizeEntry(entry);
        var key = HostKeyFormats.MakeKey(host, port);
        _trustedKeys[key] = normalized;
        _sessionTrustedKeys.TryRemove(key, out _);
        HostKeyEvent?.Invoke(key, normalized.Fingerprint, true);
    }

    /// <summary>
    /// Remove a previously trusted host key.
    /// </summary>
    public bool Remove(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        var key = HostKeyFormats.MakeKey(host, port);
        _sessionTrustedKeys.TryRemove(key, out _);
        RemoveAdditionalAlgorithms(key);
        return _trustedKeys.TryRemove(key, out _);
    }

    /// <summary>
    /// Registers an additional trusted host key for a different algorithm on the same
    /// <c>(host, port)</c> (for servers that serve both RSA and Ed25519 keys) without
    /// replacing the primary entry.
    /// </summary>
    public void TrustAdditionalAlgorithm(
        string host,
        int port,
        string fingerprint,
        string algorithm,
        HostKeySource source,
        string? publicKeyBase64 = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);

        var canonicalAlgorithm = KnownHostsParser.CanonicalizeKeyType(algorithm);
        var compositeKey = MakeAlgorithmKey(HostKeyFormats.MakeKey(host, port), canonicalAlgorithm);
        var now = DateTimeOffset.UtcNow;
        _additionalAlgorithmKeys[compositeKey] = new HostKeyEntry(
            fingerprint,
            now,
            now,
            canonicalAlgorithm,
            source)
        { PublicKeyBase64 = publicKeyBase64 };
    }

    /// <summary>
    /// Returns an additional algorithm-specific trusted entry for <c>(host, port, algorithm)</c>,
    /// or null if none is stored.
    /// </summary>
    public HostKeyEntry? GetAdditionalEntry(string host, int port, string algorithm)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            return null;
        }

        var canonicalAlgorithm = KnownHostsParser.CanonicalizeKeyType(algorithm);
        var compositeKey = MakeAlgorithmKey(HostKeyFormats.MakeKey(host, port), canonicalAlgorithm);
        return _additionalAlgorithmKeys.TryGetValue(compositeKey, out var entry) ? entry : null;
    }

    /// <summary>
    /// Returns all trusted entries (primary plus any additional algorithms) stored for
    /// <c>(host, port)</c>.
    /// </summary>
    public IReadOnlyList<HostKeyEntry> GetEntries(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);

        var list = new List<HostKeyEntry>();
        var hostPort = HostKeyFormats.MakeKey(host, port);
        if (_trustedKeys.TryGetValue(hostPort, out var primary))
        {
            list.Add(primary);
        }

        var prefix = hostPort + "|";
        foreach (var kvp in _additionalAlgorithmKeys)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                list.Add(kvp.Value);
            }
        }

        return list;
    }

    /// <summary>
    /// Removes a single additional algorithm entry for <c>(host, port, algorithm)</c>.
    /// </summary>
    public bool RemoveAdditionalAlgorithm(string host, int port, string algorithm)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            return false;
        }

        var canonicalAlgorithm = KnownHostsParser.CanonicalizeKeyType(algorithm);
        var compositeKey = MakeAlgorithmKey(HostKeyFormats.MakeKey(host, port), canonicalAlgorithm);
        return _additionalAlgorithmKeys.TryRemove(compositeKey, out _);
    }

    /// <summary>
    /// Returns every trusted entry across all hosts, including both primary and additional
    /// algorithm entries.
    /// </summary>
    public IReadOnlyList<(string HostPort, HostKeyEntry Entry, bool IsAdditional)> GetAllAlgorithmEntries()
    {
        var results = new List<(string HostPort, HostKeyEntry Entry, bool IsAdditional)>(_trustedKeys.Count + _additionalAlgorithmKeys.Count);
        foreach (var kvp in _trustedKeys)
        {
            results.Add((kvp.Key, kvp.Value, false));
        }

        foreach (var kvp in _additionalAlgorithmKeys)
        {
            var separatorIndex = kvp.Key.LastIndexOf('|');
            if (separatorIndex > 0)
            {
                results.Add((kvp.Key[..separatorIndex], kvp.Value, true));
            }
        }

        return results;
    }

    /// <summary>
    /// Removes only the primary entry for <c>(host, port)</c>, promoting the first additional
    /// algorithm entry to primary when one exists.
    /// </summary>
    internal bool RemovePrimaryOrPromote(string host, int port, out HostKeyEntry? promotedPrimary)
    {
        ArgumentNullException.ThrowIfNull(host);
        promotedPrimary = null;

        var hostPort = HostKeyFormats.MakeKey(host, port);
        _sessionTrustedKeys.TryRemove(hostPort, out _);

        var prefix = hostPort + "|";
        KeyValuePair<string, HostKeyEntry>? firstAdditional = null;
        foreach (var kvp in _additionalAlgorithmKeys)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                firstAdditional = kvp;
                break;
            }
        }

        if (firstAdditional is { } next && _additionalAlgorithmKeys.TryRemove(next.Key, out var promoted))
        {
            _trustedKeys[hostPort] = promoted;
            promotedPrimary = promoted;
            HostKeyEvent?.Invoke(hostPort, promoted.Fingerprint, true);
            return true;
        }

        return _trustedKeys.TryRemove(hostPort, out _);
    }

    private void RemoveAdditionalAlgorithms(string hostPortKey)
    {
        var prefix = hostPortKey + "|";
        foreach (var key in _additionalAlgorithmKeys.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _additionalAlgorithmKeys.TryRemove(key, out _);
            }
        }
    }

    private static string MakeAlgorithmKey(string hostPortKey, string canonicalAlgorithm) =>
        $"{hostPortKey}|{canonicalAlgorithm}";

    /// <summary>
    /// Remove a trusted entry by its stored key, which for a hashed known_hosts
    /// entry is the <c>|1|salt|hash:port</c> form that cannot be rebuilt from a
    /// plain host name.
    /// </summary>
    internal bool RemoveKey(string hostPortKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPortKey);
        _sessionTrustedKeys.TryRemove(hostPortKey, out _);
        bool removed = _trustedKeys.TryRemove(hostPortKey, out _);
        if (removed)
        {
            KeyRemoved?.Invoke(hostPortKey);
        }

        return removed;
    }

    /// <summary>
    /// Get all trusted entries for persistence to config.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetAllTrusted()
        => _trustedKeys.ToDictionary(
            static kvp => kvp.Key,
            static kvp => kvp.Value.Fingerprint,
            StringComparer.Ordinal);

    /// <summary>
    /// Enumerates the trusted entries without copying them. The dictionary's own
    /// enumeration is safe under concurrent mutation; a lookup that scans the
    /// hashed entries runs once per known_hosts line during a sync and must not
    /// pay a full copy each time.
    /// </summary>
    internal IEnumerable<KeyValuePair<string, HostKeyEntry>> EnumerateEntries() => _trustedKeys;

    /// <summary>
    /// Get all trusted metadata entries.
    /// </summary>
    public IReadOnlyDictionary<string, HostKeyEntry> GetAllEntries()
        => _trustedKeys.ToDictionary(
            static kvp => kvp.Key,
            static kvp => kvp.Value,
            StringComparer.Ordinal);

    internal void SetEntry(string host, int port, HostKeyEntry entry, bool raiseEvent)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(entry);

        var normalized = NormalizeEntry(entry);
        var key = HostKeyFormats.MakeKey(host, port);
        SetEntry(key, normalized, raiseEvent);
    }

    internal void SetEntry(string hostPort, HostKeyEntry entry, bool raiseEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPort);
        ArgumentNullException.ThrowIfNull(entry);

        var normalized = NormalizeEntry(entry);
        var key = hostPort;
        _trustedKeys[key] = normalized;
        _sessionTrustedKeys.TryRemove(key, out _);
        if (raiseEvent)
        {
            HostKeyEvent?.Invoke(key, normalized.Fingerprint, true);
        }
    }

    /// <summary>
    /// Compute a SHA256 fingerprint from raw host key bytes.
    /// Format: "SHA256:&lt;base64-no-padding&gt;" (matches OpenSSH convention).
    /// </summary>
    internal static string ComputeFingerprint(byte[] hostKey)
    {
        return HostKeyFormats.ComputeSha256Fingerprint(hostKey);
    }

    private static HostKeyEntry CreateLegacyEntry(string fingerprint)
    {
        return new HostKeyEntry(
            fingerprint,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            UnknownAlgorithm,
            HostKeySource.Unknown);
    }

    private static HostKeyEntry NormalizeEntry(HostKeyEntry entry)
    {
        return entry with
        {
            Algorithm = string.IsNullOrWhiteSpace(entry.Algorithm) ? UnknownAlgorithm : entry.Algorithm
        };
    }
}
