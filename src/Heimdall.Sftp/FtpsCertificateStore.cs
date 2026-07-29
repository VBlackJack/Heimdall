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
using Heimdall.Core.Certificates;

namespace Heimdall.Sftp;

/// <summary>
/// Trust-On-First-Use certificate store for FTPS server certificates.
/// </summary>
public sealed class FtpsCertificateStore
{
    private static readonly FtpsCertificateEntry EmptyEntry = new(
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        string.Empty,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        FtpsCertificateSource.Unknown);

    private readonly ConcurrentDictionary<string, FtpsCertificateEntry> _trustedCertificates = new();
    private readonly ConcurrentDictionary<string, FtpsCertificateEntry> _sessionTrustedCertificates = new();

    public event Action<string, FtpsCertificateEntry>? CertificateTrusted;

    internal static bool ConstantTimeEquals(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }

        var aBytes = Encoding.ASCII.GetBytes(a);
        var bBytes = Encoding.ASCII.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    public static string MakeKey(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var normalizedHost = host.Trim();
        if (normalizedHost.Contains(':', StringComparison.Ordinal)
            && !normalizedHost.StartsWith("[", StringComparison.Ordinal))
        {
            normalizedHost = $"[{normalizedHost}]";
        }

        return $"{normalizedHost}:{port}";
    }

    public void LoadEntriesFromConfig(IEnumerable<KeyValuePair<string, FtpsCertificateEntry>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var (key, entry) in entries)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(entry.Fingerprint))
            {
                _trustedCertificates[key] = NormalizeEntry(entry);
            }
        }
    }

    public FtpsCertificateEntry? GetEntry(string host, int port)
    {
        var key = MakeKey(host, port);
        return _trustedCertificates.TryGetValue(key, out var entry) ? entry : null;
    }

    public FtpsCertificateEntry? GetSessionEntry(string host, int port)
    {
        var key = MakeKey(host, port);
        return _sessionTrustedCertificates.TryGetValue(key, out var entry) ? entry : null;
    }

    public IReadOnlyDictionary<string, FtpsCertificateEntry> GetAllEntries()
        => _trustedCertificates.ToDictionary(
            static kvp => kvp.Key,
            static kvp => kvp.Value,
            StringComparer.Ordinal);

    public void Trust(string host, int port, FtpsCertificateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var key = MakeKey(host, port);
        var existing = _trustedCertificates.TryGetValue(key, out var current) ? current : EmptyEntry;
        var normalized = NormalizeEntry(entry) with
        {
            FirstSeen = existing.FirstSeen > DateTimeOffset.MinValue
                ? existing.FirstSeen
                : entry.FirstSeen
        };

        _trustedCertificates[key] = normalized;
        _sessionTrustedCertificates.TryRemove(key, out _);
        CertificateTrusted?.Invoke(key, normalized);
    }

    public void TrustForSession(string host, int port, FtpsCertificateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var key = MakeKey(host, port);
        _sessionTrustedCertificates[key] = NormalizeEntry(entry);
    }

    public void RefreshLastSeen(string host, int port)
    {
        var key = MakeKey(host, port);
        if (!_trustedCertificates.TryGetValue(key, out var existing))
        {
            return;
        }

        var updated = existing with { LastSeen = DateTimeOffset.UtcNow };
        _trustedCertificates[key] = updated;
        CertificateTrusted?.Invoke(key, updated);
    }

    public bool Remove(string host, int port)
    {
        var key = MakeKey(host, port);
        _sessionTrustedCertificates.TryRemove(key, out _);
        return _trustedCertificates.TryRemove(key, out _);
    }

    private static FtpsCertificateEntry NormalizeEntry(FtpsCertificateEntry entry)
        => entry with
        {
            Subject = string.IsNullOrWhiteSpace(entry.Subject) ? "(unknown)" : entry.Subject,
            Issuer = string.IsNullOrWhiteSpace(entry.Issuer) ? "(unknown)" : entry.Issuer
        };
}
