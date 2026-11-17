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

using System.Text.RegularExpressions;

namespace Heimdall.Core.Ssh;

/// <summary>
/// Identifies one SSH agent key by the fingerprint <c>ssh-add -l</c> prints.
/// </summary>
public static partial class SshAgentKeyFingerprints
{
    /// <summary>The prefix of an OpenSSH SHA-256 fingerprint.</summary>
    public const string Prefix = "SHA256:";

    /// <summary>Length of a SHA-256 digest in unpadded base64.</summary>
    public const int Base64DigestLength = 43;

    /// <summary>
    /// Whether <paramref name="fingerprint"/> is a SHA-256 fingerprint in OpenSSH form:
    /// the prefix followed by exactly 43 unpadded base64 characters.
    /// </summary>
    public static bool IsWellFormed(string? fingerprint) =>
        fingerprint is not null && WellFormedPattern().IsMatch(fingerprint);

    /// <summary>Computes the fingerprint of the public key blob an agent reports.</summary>
    public static string Compute(byte[] keyBlob) => HostKeyFormats.ComputeSha256Fingerprint(keyBlob);

    // \z, not $: the latter also matches before a trailing newline.
    [GeneratedRegex(@"^SHA256:[A-Za-z0-9+/]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex WellFormedPattern();
}
