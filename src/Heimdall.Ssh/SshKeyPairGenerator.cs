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
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Heimdall.Ssh;

/// <summary>
/// Supported SSH key pair algorithms.
/// </summary>
public enum SshKeyAlgorithm
{
    Ed25519,
    Rsa4096,
    Rsa2048,
    EcdsaP256,
    EcdsaP384,
    EcdsaP521
}

/// <summary>
/// Supported private key serialization formats.
/// </summary>
public enum SshPrivateKeyFormat
{
    OpenSsh,
    Pkcs8Pem
}

/// <summary>
/// Result of generating an SSH key pair.
/// </summary>
public sealed record GeneratedSshKeyPair(
    SshKeyAlgorithm Algorithm,
    SshPrivateKeyFormat Format,
    string PublicKeyOpenSsh,
    string PrivateKey,
    string Fingerprint,
    string DefaultFileBaseName);

/// <summary>
/// Pure, reflection-free SSH key pair generator supporting Ed25519, RSA (2048/4096),
/// and ECDSA (P-256/P-384/P-521) with OpenSSH (<c>openssh-key-v1</c>) and PKCS#8 PEM export.
/// </summary>
public static class SshKeyPairGenerator
{
    private const int Rsa2048KeySize = 2048;
    private const int Rsa4096KeySize = 4096;
    private const int PbeIterationCount = 600_000;
    private const int OpenSshBcryptRounds = 16;
    private const int OpenSshSaltLength = 16;
    private const int Aes256KeyLength = 32;
    private const int AesBlockLength = 16;
    private const int OpenSshUnencryptedBlockSize = 8;
    private const int PemLineLength = 70;

    private const string OpenSshRsaPrefix = "ssh-rsa";
    private const string OpenSshEd25519Prefix = "ssh-ed25519";
    private const string OpenSshEcdsaP256Prefix = "ecdsa-sha2-nistp256";
    private const string OpenSshEcdsaP384Prefix = "ecdsa-sha2-nistp384";
    private const string OpenSshEcdsaP521Prefix = "ecdsa-sha2-nistp521";

    private const string CurveNameP256 = "nistp256";
    private const string CurveNameP384 = "nistp384";
    private const string CurveNameP521 = "nistp521";

    private const string OpenSshAuthMagic = "openssh-key-v1\0";
    private const string OpenSshCipherNone = "none";
    private const string OpenSshCipherAes256Ctr = "aes256-ctr";
    private const string OpenSshKdfNone = "none";
    private const string OpenSshKdfBcrypt = "bcrypt";
    private const string OpenSshPrivateKeyLabel = "OPENSSH PRIVATE KEY";
    private const string Pkcs8PrivateKeyLabel = "PRIVATE KEY";
    private const string Pkcs8EncryptedPrivateKeyLabel = "ENCRYPTED PRIVATE KEY";

    private const string OidEd25519 = "1.3.101.112";
    private const string OidPbes2 = "1.2.840.113549.1.5.13";
    private const string OidPbkdf2 = "1.2.840.113549.1.5.12";
    private const string OidHmacWithSha256 = "1.2.840.113549.2.9";
    private const string OidAes256Cbc = "2.16.840.1.101.3.4.1.42";

    /// <summary>
    /// Generates an SSH key pair for the requested algorithm and private key format.
    /// </summary>
    public static GeneratedSshKeyPair Generate(
        SshKeyAlgorithm algorithm,
        string? comment = null,
        string? passphrase = null,
        SshPrivateKeyFormat format = SshPrivateKeyFormat.OpenSsh)
    {
        var normalizedComment = string.IsNullOrWhiteSpace(comment) ? string.Empty : comment.Trim();
        var normalizedPassphrase = passphrase ?? string.Empty;

        return algorithm switch
        {
            SshKeyAlgorithm.Ed25519 => GenerateEd25519(normalizedComment, normalizedPassphrase, format),
            SshKeyAlgorithm.Rsa2048 => GenerateRsa(Rsa2048KeySize, SshKeyAlgorithm.Rsa2048, normalizedComment, normalizedPassphrase, format),
            SshKeyAlgorithm.Rsa4096 => GenerateRsa(Rsa4096KeySize, SshKeyAlgorithm.Rsa4096, normalizedComment, normalizedPassphrase, format),
            SshKeyAlgorithm.EcdsaP256 => GenerateEcdsa(ECCurve.NamedCurves.nistP256, OpenSshEcdsaP256Prefix, CurveNameP256, 32, SshKeyAlgorithm.EcdsaP256, normalizedComment, normalizedPassphrase, format),
            SshKeyAlgorithm.EcdsaP384 => GenerateEcdsa(ECCurve.NamedCurves.nistP384, OpenSshEcdsaP384Prefix, CurveNameP384, 48, SshKeyAlgorithm.EcdsaP384, normalizedComment, normalizedPassphrase, format),
            SshKeyAlgorithm.EcdsaP521 => GenerateEcdsa(ECCurve.NamedCurves.nistP521, OpenSshEcdsaP521Prefix, CurveNameP521, 66, SshKeyAlgorithm.EcdsaP521, normalizedComment, normalizedPassphrase, format),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported SSH key algorithm.")
        };
    }

    private static GeneratedSshKeyPair GenerateEd25519(
        string comment,
        string passphrase,
        SshPrivateKeyFormat format)
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var keyPair = generator.GenerateKeyPair();
        var privateParams = (Ed25519PrivateKeyParameters)keyPair.Private;
        var publicParams = (Ed25519PublicKeyParameters)keyPair.Public;

        var privateSeed = privateParams.GetEncoded();
        var publicBytes = publicParams.GetEncoded();
        try
        {
            var publicBlob = BuildEd25519PublicKeyBlob(publicBytes);
            var publicOpenSsh = FormatOpenSshPublicKey(OpenSshEd25519Prefix, publicBlob, comment);
            var fingerprint = ComputeSha256Fingerprint(publicBlob);

            var privateKey = format switch
            {
                SshPrivateKeyFormat.OpenSsh => EncodeOpenSshPrivateKey(
                    publicBlob,
                    writer => WriteEd25519PrivatePayload(writer, publicBytes, privateSeed),
                    comment,
                    passphrase),
                SshPrivateKeyFormat.Pkcs8Pem => ExportEd25519Pkcs8Pem(privateSeed, passphrase),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported private key format.")
            };

            return new GeneratedSshKeyPair(
                SshKeyAlgorithm.Ed25519,
                format,
                publicOpenSsh,
                privateKey,
                fingerprint,
                "id_ed25519");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateSeed);
        }
    }

    private static GeneratedSshKeyPair GenerateRsa(
        int keySize,
        SshKeyAlgorithm algorithm,
        string comment,
        string passphrase,
        SshPrivateKeyFormat format)
    {
        using var rsa = RSA.Create(keySize);
        var parameters = rsa.ExportParameters(includePrivateParameters: true);
        try
        {
            var publicBlob = BuildRsaPublicKeyBlob(parameters);
            var publicOpenSsh = FormatOpenSshPublicKey(OpenSshRsaPrefix, publicBlob, comment);
            var fingerprint = ComputeSha256Fingerprint(publicBlob);

            var privateKey = format switch
            {
                SshPrivateKeyFormat.OpenSsh => EncodeOpenSshPrivateKey(
                    publicBlob,
                    writer => WriteRsaPrivatePayload(writer, parameters),
                    comment,
                    passphrase),
                SshPrivateKeyFormat.Pkcs8Pem => ExportNetPkcs8Pem(rsa, passphrase),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported private key format.")
            };

            return new GeneratedSshKeyPair(
                algorithm,
                format,
                publicOpenSsh,
                privateKey,
                fingerprint,
                "id_rsa");
        }
        finally
        {
            ZeroRsaParameters(parameters);
        }
    }

    private static GeneratedSshKeyPair GenerateEcdsa(
        ECCurve curve,
        string keyType,
        string curveName,
        int coordinateLength,
        SshKeyAlgorithm algorithm,
        string comment,
        string passphrase,
        SshPrivateKeyFormat format)
    {
        using var ecdsa = ECDsa.Create(curve);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        try
        {
            var uncompressedPoint = BuildUncompressedEcPoint(parameters.Q.X!, parameters.Q.Y!, coordinateLength);
            var publicBlob = BuildEcdsaPublicKeyBlob(keyType, curveName, uncompressedPoint);
            var publicOpenSsh = FormatOpenSshPublicKey(keyType, publicBlob, comment);
            var fingerprint = ComputeSha256Fingerprint(publicBlob);

            var privateKey = format switch
            {
                SshPrivateKeyFormat.OpenSsh => EncodeOpenSshPrivateKey(
                    publicBlob,
                    writer => WriteEcdsaPrivatePayload(writer, keyType, curveName, uncompressedPoint, parameters.D!),
                    comment,
                    passphrase),
                SshPrivateKeyFormat.Pkcs8Pem => ExportNetPkcs8Pem(ecdsa, passphrase),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported private key format.")
            };

            return new GeneratedSshKeyPair(
                algorithm,
                format,
                publicOpenSsh,
                privateKey,
                fingerprint,
                "id_ecdsa");
        }
        finally
        {
            if (parameters.D is not null)
            {
                CryptographicOperations.ZeroMemory(parameters.D);
            }
        }
    }

    private static string EncodeOpenSshPrivateKey(
        byte[] publicKeyBlob,
        Action<MemoryStream> writePrivateKeyFields,
        string comment,
        string passphrase)
    {
        var encrypt = !string.IsNullOrEmpty(passphrase);
        var blockSize = encrypt ? AesBlockLength : OpenSshUnencryptedBlockSize;

        byte[] privateBlock;
        using (var privStream = new MemoryStream())
        {
            Span<byte> checkIntBytes = stackalloc byte[4];
            RandomNumberGenerator.Fill(checkIntBytes);
            privStream.Write(checkIntBytes);
            privStream.Write(checkIntBytes);

            writePrivateKeyFields(privStream);
            WriteOpenSshBytes(privStream, Encoding.UTF8.GetBytes(comment));

            byte pad = 1;
            while (privStream.Length % blockSize != 0)
            {
                privStream.WriteByte(pad++);
            }

            privateBlock = privStream.ToArray();
        }

        byte[]? salt = null;
        byte[]? derivedKeyAndIv = null;
        try
        {
            byte[] payloadToWrite;
            string cipherName;
            string kdfName;
            byte[] kdfOptions;

            if (encrypt)
            {
                cipherName = OpenSshCipherAes256Ctr;
                kdfName = OpenSshKdfBcrypt;
                salt = RandomNumberGenerator.GetBytes(OpenSshSaltLength);

                using (var kdfStream = new MemoryStream())
                {
                    WriteOpenSshBytes(kdfStream, salt);
                    WriteUInt32BigEndian(kdfStream, OpenSshBcryptRounds);
                    kdfOptions = kdfStream.ToArray();
                }

                var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
                try
                {
                    derivedKeyAndIv = OpenSshBcryptKdf.DeriveKey(
                        passphraseBytes,
                        salt,
                        OpenSshBcryptRounds,
                        Aes256KeyLength + AesBlockLength);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(passphraseBytes);
                }

                payloadToWrite = EncryptAes256Ctr(
                    privateBlock,
                    derivedKeyAndIv.AsSpan(0, Aes256KeyLength),
                    derivedKeyAndIv.AsSpan(Aes256KeyLength, AesBlockLength));
            }
            else
            {
                cipherName = OpenSshCipherNone;
                kdfName = OpenSshKdfNone;
                kdfOptions = [];
                payloadToWrite = privateBlock;
            }

            using var outer = new MemoryStream();
            outer.Write(Encoding.ASCII.GetBytes(OpenSshAuthMagic));
            WriteOpenSshString(outer, cipherName);
            WriteOpenSshString(outer, kdfName);
            WriteOpenSshBytes(outer, kdfOptions);
            WriteUInt32BigEndian(outer, 1);
            WriteOpenSshBytes(outer, publicKeyBlob);
            WriteOpenSshBytes(outer, payloadToWrite);

            return FormatPem(OpenSshPrivateKeyLabel, outer.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBlock);
            if (derivedKeyAndIv is not null)
            {
                CryptographicOperations.ZeroMemory(derivedKeyAndIv);
            }
        }
    }

    private static byte[] EncryptAes256Ctr(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> initialCounter)
    {
        var output = new byte[plaintext.Length];
        Span<byte> counter = stackalloc byte[AesBlockLength];
        Span<byte> keystream = stackalloc byte[AesBlockLength];
        initialCounter.CopyTo(counter);

        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var offset = 0;
        while (offset < plaintext.Length)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            var blockLen = Math.Min(AesBlockLength, plaintext.Length - offset);
            for (var i = 0; i < blockLen; i++)
            {
                output[offset + i] = (byte)(plaintext[offset + i] ^ keystream[i]);
            }

            IncrementBigEndianCounter(counter);
            offset += blockLen;
        }

        CryptographicOperations.ZeroMemory(keystream);
        return output;
    }

    private static void IncrementBigEndianCounter(Span<byte> counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
            {
                break;
            }
        }
    }

    private static void WriteEd25519PrivatePayload(MemoryStream stream, byte[] publicKey, byte[] privateSeed)
    {
        var combinedPrivate = new byte[privateSeed.Length + publicKey.Length];
        try
        {
            Buffer.BlockCopy(privateSeed, 0, combinedPrivate, 0, privateSeed.Length);
            Buffer.BlockCopy(publicKey, 0, combinedPrivate, privateSeed.Length, publicKey.Length);

            WriteOpenSshString(stream, OpenSshEd25519Prefix);
            WriteOpenSshBytes(stream, publicKey);
            WriteOpenSshBytes(stream, combinedPrivate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(combinedPrivate);
        }
    }

    private static void WriteRsaPrivatePayload(MemoryStream stream, RSAParameters parameters)
    {
        WriteOpenSshString(stream, OpenSshRsaPrefix);
        WriteOpenSshMpint(stream, parameters.Modulus!);
        WriteOpenSshMpint(stream, parameters.Exponent!);
        WriteOpenSshMpint(stream, parameters.D!);
        WriteOpenSshMpint(stream, parameters.InverseQ!);
        WriteOpenSshMpint(stream, parameters.P!);
        WriteOpenSshMpint(stream, parameters.Q!);
    }

    private static void WriteEcdsaPrivatePayload(
        MemoryStream stream,
        string keyType,
        string curveName,
        byte[] uncompressedPoint,
        byte[] privateScalar)
    {
        WriteOpenSshString(stream, keyType);
        WriteOpenSshString(stream, curveName);
        WriteOpenSshBytes(stream, uncompressedPoint);
        WriteOpenSshMpint(stream, privateScalar);
    }

    private static string ExportNetPkcs8Pem(AsymmetricAlgorithm key, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            return key.ExportPkcs8PrivateKeyPem();
        }

        var pbeParams = new PbeParameters(
            PbeEncryptionAlgorithm.Aes256Cbc,
            HashAlgorithmName.SHA256,
            PbeIterationCount);

        return key.ExportEncryptedPkcs8PrivateKeyPem(passphrase.AsSpan(), pbeParams);
    }

    private static string ExportEd25519Pkcs8Pem(byte[] privateSeed, string passphrase)
    {
        // RFC 8410 Section 7: PrivateKeyInfo for Ed25519 wraps the 32-byte seed in an inner OCTET STRING.
        var innerWriter = new AsnWriter(AsnEncodingRules.DER);
        innerWriter.WriteOctetString(privateSeed);
        var wrappedSeed = innerWriter.Encode();

        var pkcs8Writer = new AsnWriter(AsnEncodingRules.DER);
        using (pkcs8Writer.PushSequence())
        {
            pkcs8Writer.WriteInteger(0);
            using (pkcs8Writer.PushSequence())
            {
                pkcs8Writer.WriteObjectIdentifier(OidEd25519);
            }

            pkcs8Writer.WriteOctetString(wrappedSeed);
        }

        var privateKeyInfoDer = pkcs8Writer.Encode();
        try
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                return PemEncoding.WriteString(Pkcs8PrivateKeyLabel, privateKeyInfoDer);
            }

            var salt = RandomNumberGenerator.GetBytes(OpenSshSaltLength);
            var iv = RandomNumberGenerator.GetBytes(AesBlockLength);
            var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
                passphrase.AsSpan(),
                salt,
                PbeIterationCount,
                HashAlgorithmName.SHA256,
                Aes256KeyLength);

            try
            {
                using var aes = Aes.Create();
                aes.Key = derivedKey;
                var encryptedBytes = aes.EncryptCbc(privateKeyInfoDer, iv, PaddingMode.PKCS7);

                var encWriter = new AsnWriter(AsnEncodingRules.DER);
                using (encWriter.PushSequence())
                {
                    using (encWriter.PushSequence())
                    {
                        encWriter.WriteObjectIdentifier(OidPbes2);
                        using (encWriter.PushSequence())
                        {
                            using (encWriter.PushSequence())
                            {
                                encWriter.WriteObjectIdentifier(OidPbkdf2);
                                using (encWriter.PushSequence())
                                {
                                    encWriter.WriteOctetString(salt);
                                    encWriter.WriteInteger(PbeIterationCount);
                                    using (encWriter.PushSequence())
                                    {
                                        encWriter.WriteObjectIdentifier(OidHmacWithSha256);
                                        encWriter.WriteNull();
                                    }
                                }
                            }

                            using (encWriter.PushSequence())
                            {
                                encWriter.WriteObjectIdentifier(OidAes256Cbc);
                                encWriter.WriteOctetString(iv);
                            }
                        }
                    }

                    encWriter.WriteOctetString(encryptedBytes);
                }

                return PemEncoding.WriteString(Pkcs8EncryptedPrivateKeyLabel, encWriter.Encode());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derivedKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappedSeed);
            CryptographicOperations.ZeroMemory(privateKeyInfoDer);
        }
    }

    private static byte[] BuildRsaPublicKeyBlob(RSAParameters parameters)
    {
        using var ms = new MemoryStream();
        WriteOpenSshString(ms, OpenSshRsaPrefix);
        WriteOpenSshMpint(ms, parameters.Exponent!);
        WriteOpenSshMpint(ms, parameters.Modulus!);
        return ms.ToArray();
    }

    private static byte[] BuildEd25519PublicKeyBlob(byte[] publicKeyBytes)
    {
        using var ms = new MemoryStream();
        WriteOpenSshString(ms, OpenSshEd25519Prefix);
        WriteOpenSshBytes(ms, publicKeyBytes);
        return ms.ToArray();
    }

    private static byte[] BuildEcdsaPublicKeyBlob(string keyType, string curveName, byte[] uncompressedPoint)
    {
        using var ms = new MemoryStream();
        WriteOpenSshString(ms, keyType);
        WriteOpenSshString(ms, curveName);
        WriteOpenSshBytes(ms, uncompressedPoint);
        return ms.ToArray();
    }

    private static byte[] BuildUncompressedEcPoint(byte[] x, byte[] y, int coordinateLength)
    {
        var point = new byte[1 + coordinateLength * 2];
        point[0] = 0x04;
        CopyCoordinatePadded(x, point.AsSpan(1, coordinateLength));
        CopyCoordinatePadded(y, point.AsSpan(1 + coordinateLength, coordinateLength));
        return point;
    }

    private static void CopyCoordinatePadded(byte[] source, Span<byte> destination)
    {
        var copyLength = Math.Min(source.Length, destination.Length);
        var sourceOffset = source.Length - copyLength;
        var destOffset = destination.Length - copyLength;
        source.AsSpan(sourceOffset, copyLength).CopyTo(destination.Slice(destOffset, copyLength));
    }

    private static string FormatOpenSshPublicKey(string prefix, byte[] publicKeyBlob, string comment)
    {
        var base64 = Convert.ToBase64String(publicKeyBlob);
        return string.IsNullOrWhiteSpace(comment)
            ? $"{prefix} {base64}"
            : $"{prefix} {base64} {comment}";
    }

    private static string ComputeSha256Fingerprint(byte[] publicKeyBlob)
    {
        var hash = SHA256.HashData(publicKeyBlob);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    private static string FormatPem(string label, byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        var sb = new StringBuilder();
        sb.Append("-----BEGIN ").Append(label).AppendLine("-----");
        for (var i = 0; i < base64.Length; i += PemLineLength)
        {
            var chunkLength = Math.Min(PemLineLength, base64.Length - i);
            sb.AppendLine(base64.Substring(i, chunkLength));
        }

        sb.Append("-----END ").Append(label).Append("-----");
        return sb.ToString();
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteOpenSshString(Stream stream, string value)
    {
        WriteOpenSshBytes(stream, Encoding.ASCII.GetBytes(value));
    }

    private static void WriteOpenSshBytes(Stream stream, byte[] value)
    {
        WriteUInt32BigEndian(stream, (uint)value.Length);
        stream.Write(value);
    }

    private static void WriteOpenSshMpint(Stream stream, byte[] value)
    {
        var start = 0;
        while (start < value.Length && value[start] == 0)
        {
            start++;
        }

        if (start == value.Length)
        {
            WriteUInt32BigEndian(stream, 0);
            return;
        }

        var trimmed = value.AsSpan(start);
        var needsLeadingZero = (trimmed[0] & 0x80) != 0;
        WriteUInt32BigEndian(stream, (uint)(trimmed.Length + (needsLeadingZero ? 1 : 0)));
        if (needsLeadingZero)
        {
            stream.WriteByte(0);
        }

        stream.Write(trimmed);
    }

    private static void ZeroRsaParameters(RSAParameters parameters)
    {
        if (parameters.D is not null) CryptographicOperations.ZeroMemory(parameters.D);
        if (parameters.DP is not null) CryptographicOperations.ZeroMemory(parameters.DP);
        if (parameters.DQ is not null) CryptographicOperations.ZeroMemory(parameters.DQ);
        if (parameters.InverseQ is not null) CryptographicOperations.ZeroMemory(parameters.InverseQ);
        if (parameters.P is not null) CryptographicOperations.ZeroMemory(parameters.P);
        if (parameters.Q is not null) CryptographicOperations.ZeroMemory(parameters.Q);
    }
}
