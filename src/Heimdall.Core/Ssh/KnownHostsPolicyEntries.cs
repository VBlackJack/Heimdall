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

/// <summary>A parsed <c>@cert-authority</c> rule from an OpenSSH <c>known_hosts</c> file.</summary>
public sealed record KnownHostsCertAuthorityEntry(
    string HostPatterns,
    string KeyType,
    string CaFingerprint,
    byte[] CaKeyBlob,
    int SourceLineNumber);

/// <summary>A parsed <c>@revoked</c> rule from an OpenSSH <c>known_hosts</c> file.</summary>
public sealed record KnownHostsRevokedEntry(
    string HostPatterns,
    string KeyType,
    string Fingerprint,
    int SourceLineNumber);

/// <summary>
/// Persisted representation of an OpenSSH <c>@cert-authority</c> or <c>@revoked</c> rule.
/// </summary>
public sealed record KnownHostsPolicyRuleDto(
    string HostPatterns,
    string Fingerprint);

/// <summary>Certificate authority and revocation rules extracted from <c>known_hosts</c>.</summary>
public sealed record KnownHostsPolicyEntries(
    IReadOnlyList<KnownHostsCertAuthorityEntry> CertAuthorities,
    IReadOnlyList<KnownHostsRevokedEntry> RevokedKeys);
