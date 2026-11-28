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

namespace Heimdall.Ssh;

/// <summary>
/// The host key types native <c>ssh.exe</c> may be pinned to, and the <c>HostKeyAlgorithms</c>
/// names that select each one. Anything outside this list (<c>ssh-dss</c>, <c>sk-*</c>, unknown)
/// is refused before a key is written to the ephemeral <c>known_hosts</c> file.
/// </summary>
public static class OpenSshHostKeyTypes
{
    public const string Ed25519 = "ssh-ed25519";
    public const string EcdsaNistP256 = "ecdsa-sha2-nistp256";
    public const string EcdsaNistP384 = "ecdsa-sha2-nistp384";
    public const string EcdsaNistP521 = "ecdsa-sha2-nistp521";
    public const string Rsa = "ssh-rsa";

    private const string CertificateSuffix = "-cert-v01@openssh.com";
    private const string RsaSha512 = "rsa-sha2-512";
    private const string RsaSha256 = "rsa-sha2-256";

    private static readonly string[] Supported =
    [
        Ed25519,
        EcdsaNistP256,
        EcdsaNistP384,
        EcdsaNistP521,
        Rsa,
    ];

    /// <summary>True when <paramref name="keyType"/> is a plain key type Heimdall may pin.</summary>
    public static bool IsSupported(string? keyType)
        => keyType is not null && Array.IndexOf(Supported, keyType) >= 0;

    /// <summary>
    /// Reads the key type a public key blob starts with (its first length-prefixed string), or
    /// null when the blob is too short or not a printable ASCII name.
    /// </summary>
    public static string? ReadBlobKeyType(byte[]? blob)
    {
        const int LengthPrefixBytes = sizeof(uint);
        if (blob is null || blob.Length < LengthPrefixBytes)
        {
            return null;
        }

        uint length = ((uint)blob[0] << 24) | ((uint)blob[1] << 16) | ((uint)blob[2] << 8) | blob[3];
        if (length == 0 || length > blob.Length - LengthPrefixBytes)
        {
            return null;
        }

        var chars = new char[length];
        for (int i = 0; i < chars.Length; i++)
        {
            byte b = blob[LengthPrefixBytes + i];
            if (b is < 0x20 or > 0x7E)
            {
                return null;
            }

            chars[i] = (char)b;
        }

        return new string(chars);
    }

    /// <summary>
    /// The <c>HostKeyAlgorithms</c> names that make ssh offer the given plain key type.
    /// RSA is offered through its SHA-2 signature algorithms, plus the SHA-1 <c>ssh-rsa</c> one
    /// unless <paramref name="allowSha1Rsa"/> is false (the SSH.NET Standard policy parity).
    /// </summary>
    public static IReadOnlyList<string> PlainAlgorithms(string keyType, bool allowSha1Rsa)
    {
        if (!IsSupported(keyType))
        {
            throw new ArgumentException("Unsupported pinned host key type.", nameof(keyType));
        }

        return keyType == Rsa
            ? (allowSha1Rsa ? [RsaSha512, RsaSha256, Rsa] : [RsaSha512, RsaSha256])
            : [keyType];
    }

    /// <summary>The certificate algorithm names matching <see cref="PlainAlgorithms"/>.</summary>
    public static IReadOnlyList<string> CertificateAlgorithms(string keyType, bool allowSha1Rsa)
    {
        var result = new List<string>();
        foreach (string plain in PlainAlgorithms(keyType, allowSha1Rsa))
        {
            result.Add(plain + CertificateSuffix);
        }

        return result;
    }

    /// <summary>
    /// The key type a stored or presented blob will be written under: the underlying type of a
    /// certificate, or the blob's own type. Null when the blob cannot be read.
    /// </summary>
    public static string? ResolvePinnedKeyType(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);

        string? blobType = ReadBlobKeyType(blob);
        if (blobType is null)
        {
            return null;
        }

        if (!OpenSshCertificateParser.IsCertificateKeyType(blobType))
        {
            return blobType;
        }

        return OpenSshCertificateParser.TryParseBlob(blob, out OpenSshCertificateInfo? cert) && cert is not null
            ? cert.UnderlyingKeyType
            : null;
    }
}
