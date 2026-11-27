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

    event Action<string, HostKeyEntry>? EntryTrusted;

    event Action<string>? EntryRemoved;

    event Action<string, HostKeyEntry, HostKeyEntry>? EntryReplaced;
}
