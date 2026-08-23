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

namespace Heimdall.Core.Certificates;

/// <summary>
/// One RDP certificate a profile trusts, and what was known when it was approved.
/// </summary>
/// <param name="Thumbprint">
/// The SHA-256 thumbprint as <see cref="CertificateFingerprint.ComputeSha256"/> renders it.
/// This is the identity: two entries with the same thumbprint are the same certificate.
/// </param>
/// <param name="FirstTrusted">When the user first approved this certificate.</param>
/// <remarks>
/// <b>A record rather than a bare string, and the reason is in this repository's own
/// history.</b> Trusted SSH host keys were first persisted as plain fingerprints in
/// <c>TrustedHostKeys</c>; when metadata became necessary a second dictionary,
/// <c>TrustedHostKeysV2</c>, had to be added beside it, and BOTH are still written on every
/// save. Starting from a string here would buy the same migration a second time.
/// <para>
/// <see cref="Subject"/> and <see cref="Issuer"/> are left unset until there is a
/// certificate to read them from. They are declared now so that filling them later is a
/// value change rather than a format change; they are not filled now because nothing has
/// inspected a certificate yet.
/// </para>
/// </remarks>
public sealed record RdpCertificateEntry(string Thumbprint, DateTimeOffset FirstTrusted)
{
    /// <summary>Subject of the certificate, when one was inspected.</summary>
    public string? Subject { get; init; }

    /// <summary>Issuer of the certificate, when one was inspected.</summary>
    public string? Issuer { get; init; }
}
