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

    /// <summary>
    /// Raised whenever a host key is explicitly trusted.
    /// Parameters: host:port key, fingerprint, whether it was trusted.
    /// </summary>
    public event Action<string, string, bool>? HostKeyEvent;

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
    /// <param name="entries">Tuples of (host, port, fingerprint) from config.</param>
    public void LoadFromConfig(IEnumerable<(string host, int port, string? fingerprint)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var (host, port, fingerprint) in entries)
        {
            if (!string.IsNullOrWhiteSpace(fingerprint))
            {
                _trustedKeys[HostKeyFormats.MakeKey(host, port)] = CreateLegacyEntry(fingerprint);
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
        return _trustedKeys.TryRemove(key, out _);
    }

    /// <summary>
    /// Remove a trusted entry by its stored key, which for a hashed known_hosts
    /// entry is the <c>|1|salt|hash:port</c> form that cannot be rebuilt from a
    /// plain host name.
    /// </summary>
    internal bool RemoveKey(string hostPortKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPortKey);
        _sessionTrustedKeys.TryRemove(hostPortKey, out _);
        return _trustedKeys.TryRemove(hostPortKey, out _);
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
