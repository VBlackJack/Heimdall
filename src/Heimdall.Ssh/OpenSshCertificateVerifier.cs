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

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Heimdall.Core.Ssh;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Heimdall.Ssh;

/// <summary>Outcome of evaluating an OpenSSH host certificate against trusted CAs and revocation rules.</summary>
public enum HostCertificateVerificationStatus
{
    NotACertificate,
    TrustedByCertAuthority,
    Revoked,
    InvalidOrUntrusted
}

/// <summary>
/// Cryptographically verifies OpenSSH v01 host certificates against <see cref="HostKeyStore"/>
/// <c>@cert-authority</c> and <c>@revoked</c> rules.
/// </summary>
public static class OpenSshCertificateVerifier
{
    public static HostCertificateVerificationStatus VerifyHostCertificate(
        byte[]? hostKeyBlob,
        string host,
        int port,
        HostKeyStore hostKeyStore,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostKeyStore);

        if (!OpenSshCertificateParser.TryParseBlob(hostKeyBlob, out var cert) || cert is null)
        {
            return HostCertificateVerificationStatus.NotACertificate;
        }

        if (hostKeyStore.IsRevoked(host, port, cert.CertificateFingerprint)
            || hostKeyStore.IsRevoked(host, port, cert.SignatureKeyFingerprint)
            || (!string.IsNullOrEmpty(cert.SubjectPublicKeyFingerprint)
                && hostKeyStore.IsRevoked(host, port, cert.SubjectPublicKeyFingerprint)))
        {
            return HostCertificateVerificationStatus.Revoked;
        }

        if (cert.Type != OpenSshCertificateType.Host)
        {
            return HostCertificateVerificationStatus.InvalidOrUntrusted;
        }

        // Per OpenSSH PROTOCOL.certkeys, any unrecognized critical option MUST fail verification.
        // No critical options are currently defined for host certificates.
        if (cert.CriticalOptions.Count > 0)
        {
            return HostCertificateVerificationStatus.InvalidOrUntrusted;
        }

        var checkTime = now ?? DateTimeOffset.UtcNow;
        if (!cert.IsValidAt(checkTime) || !cert.MatchesPrincipal(host))
        {
            return HostCertificateVerificationStatus.InvalidOrUntrusted;
        }

        if (!hostKeyStore.IsTrustedCertAuthority(host, port, cert.SignatureKeyFingerprint))
        {
            return HostCertificateVerificationStatus.InvalidOrUntrusted;
        }

        return VerifyCaSignature(cert)
            ? HostCertificateVerificationStatus.TrustedByCertAuthority
            : HostCertificateVerificationStatus.InvalidOrUntrusted;
    }

    /// <summary>
    /// Verifies <see cref="OpenSshCertificateInfo.SignatureBlob"/> over
    /// <see cref="OpenSshCertificateInfo.SignedData"/> using
    /// <see cref="OpenSshCertificateInfo.SignatureKeyBlob"/>.
    /// </summary>
    public static bool VerifyCaSignature(OpenSshCertificateInfo certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var sigOffset = 0;
        if (!TryReadString(certificate.SignatureBlob, ref sigOffset, out var sigAlg)
            || !TryReadBytes(certificate.SignatureBlob, ref sigOffset, out var sigData)
            || sigOffset != certificate.SignatureBlob.Length)
        {
            return false;
        }

        var keyOffset = 0;
        if (!TryReadString(certificate.SignatureKeyBlob, ref keyOffset, out var caKeyType))
        {
            return false;
        }

        try
        {
            return caKeyType switch
            {
                "ssh-ed25519" => VerifyEd25519(certificate.SignatureKeyBlob, keyOffset, sigAlg, sigData, certificate.SignedData),
                "ssh-rsa" => VerifyRsa(certificate.SignatureKeyBlob, keyOffset, sigAlg, sigData, certificate.SignedData),
                "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521"
                    => VerifyEcdsa(certificate.SignatureKeyBlob, keyOffset, caKeyType, sigAlg, sigData, certificate.SignedData),
                _ => false
            };
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool VerifyEd25519(byte[] keyBlob, int keyOffset, string sigAlg, byte[] sigData, byte[] signedData)
    {
        if (!string.Equals(sigAlg, "ssh-ed25519", StringComparison.Ordinal) || sigData.Length != 64)
        {
            return false;
        }

        if (!TryReadBytes(keyBlob, ref keyOffset, out var pubKeyBytes)
            || pubKeyBytes.Length != 32
            || keyOffset != keyBlob.Length)
        {
            return false;
        }

        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(pubKeyBytes, 0));
        signer.BlockUpdate(signedData, 0, signedData.Length);
        return signer.VerifySignature(sigData);
    }

    private static bool VerifyRsa(byte[] keyBlob, int keyOffset, string sigAlg, byte[] sigData, byte[] signedData)
    {
        HashAlgorithmName hashAlg;
        if (string.Equals(sigAlg, "rsa-sha2-512", StringComparison.Ordinal))
        {
            hashAlg = HashAlgorithmName.SHA512;
        }
        else if (string.Equals(sigAlg, "rsa-sha2-256", StringComparison.Ordinal))
        {
            hashAlg = HashAlgorithmName.SHA256;
        }
        else
        {
            return false;
        }

        if (!TryReadBytes(keyBlob, ref keyOffset, out var eRaw)
            || !TryReadBytes(keyBlob, ref keyOffset, out var nRaw)
            || keyOffset != keyBlob.Length)
        {
            return false;
        }

        var parameters = new RSAParameters
        {
            Exponent = TrimLeadingZero(eRaw),
            Modulus = TrimLeadingZero(nRaw)
        };

        using var rsa = RSA.Create();
        rsa.ImportParameters(parameters);
        return rsa.VerifyData(signedData, sigData, hashAlg, RSASignaturePadding.Pkcs1);
    }

    private static bool VerifyEcdsa(byte[] keyBlob, int keyOffset, string caKeyType, string sigAlg, byte[] sigData, byte[] signedData)
    {
        if (!string.Equals(sigAlg, caKeyType, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryReadString(keyBlob, ref keyOffset, out var curveName)
            || !TryReadBytes(keyBlob, ref keyOffset, out var qPoint)
            || keyOffset != keyBlob.Length
            || !string.Equals(caKeyType, $"ecdsa-sha2-{curveName}", StringComparison.Ordinal)
            || qPoint.Length < 3
            || qPoint[0] != 0x04)
        {
            return false;
        }

        (ECCurve curve, HashAlgorithmName hash, int coordSize) = curveName switch
        {
            "nistp256" => (ECCurve.NamedCurves.nistP256, HashAlgorithmName.SHA256, 32),
            "nistp384" => (ECCurve.NamedCurves.nistP384, HashAlgorithmName.SHA384, 48),
            "nistp521" => (ECCurve.NamedCurves.nistP521, HashAlgorithmName.SHA512, 66),
            _ => default
        };

        if (coordSize == 0 || qPoint.Length != 1 + 2 * coordSize)
        {
            return false;
        }

        var rOffset = 0;
        if (!TryReadBytes(sigData, ref rOffset, out var rRaw)
            || !TryReadBytes(sigData, ref rOffset, out var sRaw)
            || rOffset != sigData.Length)
        {
            return false;
        }

        var ieeeSig = new byte[coordSize * 2];
        if (!TryCopyFixedUnsigned(rRaw, ieeeSig.AsSpan(0, coordSize))
            || !TryCopyFixedUnsigned(sRaw, ieeeSig.AsSpan(coordSize, coordSize)))
        {
            return false;
        }

        var parameters = new ECParameters
        {
            Curve = curve,
            Q = new ECPoint
            {
                X = qPoint.AsSpan(1, coordSize).ToArray(),
                Y = qPoint.AsSpan(1 + coordSize, coordSize).ToArray()
            }
        };

        using var ecdsa = ECDsa.Create(parameters);
        return ecdsa.VerifyData(signedData, ieeeSig, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static byte[] TrimLeadingZero(byte[] mpint)
    {
        var start = 0;
        while (start < mpint.Length - 1 && mpint[start] == 0)
        {
            start++;
        }

        return mpint.AsSpan(start).ToArray();
    }

    private static bool TryCopyFixedUnsigned(byte[] mpint, Span<byte> destination)
    {
        if (mpint.Length == 0 || (mpint[0] & 0x80) != 0)
        {
            return false;
        }

        var trimmed = TrimLeadingZero(mpint);
        if (trimmed.Length > destination.Length)
        {
            return false;
        }

        destination.Clear();
        trimmed.CopyTo(destination.Slice(destination.Length - trimmed.Length, trimmed.Length));
        return true;
    }

    private static bool TryReadBytes(byte[] data, ref int offset, out byte[] value)
    {
        value = [];
        if (offset < 0 || data.Length - offset < 4)
        {
            return false;
        }

        var len = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;
        if (len > (uint)(data.Length - offset))
        {
            return false;
        }

        value = data.AsSpan(offset, (int)len).ToArray();
        offset += (int)len;
        return true;
    }

    private static bool TryReadString(byte[] data, ref int offset, out string value)
    {
        value = string.Empty;
        if (!TryReadBytes(data, ref offset, out var bytes))
        {
            return false;
        }

        value = Encoding.ASCII.GetString(bytes);
        return true;
    }
}
