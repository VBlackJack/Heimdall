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

namespace Heimdall.Core.Ssh;

public interface IHostKeyTrustService
{
    HostKeyEntry? GetEntry(string host, int port);

    HostKeyEntry? GetEffectiveEntry(string host, int port);

    IReadOnlyList<(string HostPort, HostKeyEntry Entry)> GetAllEntries();

    /// <summary>
    /// Returns all trusted entries including additional algorithms stored for the same host and port.
    /// </summary>
    IReadOnlyList<(string HostPort, HostKeyEntry Entry, bool IsAdditional)> GetAllAlgorithmEntries()
        => GetAllEntries().Select(static item => (item.HostPort, item.Entry, false)).ToList();

    HostKeyVerifyResult Verify(string host, int port, string presentedFingerprint, string algorithm);

    void Trust(string host, int port, string fingerprint, string algorithm, HostKeySource source, string? publicKeyBase64 = null);

    void TrustForSession(string host, int port, string fingerprint, string algorithm, string? publicKeyBase64 = null);

    void Import(string host, int port, string fingerprint, string algorithm, DateTimeOffset importedAt, string? publicKeyBase64 = null);

    bool Remove(string host, int port);

    /// <summary>
    /// Removes a specific algorithm entry for <c>(host, port)</c>, preserving other algorithms
    /// trusted for the same host when present.
    /// </summary>
    bool RemoveAlgorithm(string host, int port, string algorithm) => Remove(host, port);

    /// <summary>
    /// Loads <c>@cert-authority</c> and <c>@revoked</c> policy entries from OpenSSH <c>known_hosts</c> text.
    /// </summary>
    KnownHostsPolicyEntries LoadPolicyFromKnownHosts(string knownHostsContent) => new([], []);

    /// <summary>Returns all active <c>@cert-authority</c> rules.</summary>
    IReadOnlyList<(string HostPatterns, string Fingerprint)> GetCertAuthorities() => [];

    /// <summary>Returns all active <c>@revoked</c> rules.</summary>
    IReadOnlyList<(string HostPatterns, string Fingerprint)> GetRevokedKeys() => [];

    /// <summary>Returns true if <paramref name="fingerprint"/> is explicitly revoked for <paramref name="host"/>:<paramref name="port"/>.</summary>
    bool IsRevoked(string host, int port, string fingerprint) => false;

    /// <summary>Registers a trusted OpenSSH <c>@cert-authority</c> rule.</summary>
    void AddCertAuthority(string hostPatterns, string caFingerprint) { }

    /// <summary>Removes a trusted OpenSSH <c>@cert-authority</c> rule.</summary>
    bool RemoveCertAuthority(string hostPatterns, string caFingerprint) => false;

    /// <summary>Registers an OpenSSH <c>@revoked</c> host key or CA rule.</summary>
    void AddRevokedKey(string hostPatterns, string fingerprint) { }

    /// <summary>Removes an OpenSSH <c>@revoked</c> host key or CA rule.</summary>
    bool RemoveRevokedKey(string hostPatterns, string fingerprint) => false;

    event Action<string, HostKeyEntry>? EntryTrusted;

    event Action<string>? EntryRemoved;

    event Action<string, HostKeyEntry, HostKeyEntry>? EntryReplaced;

    event Action? PolicyChanged { add { } remove { } }
}
