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

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Writes a passphrase-protected RSA key in the PuTTY version 2 file format.
/// </summary>
/// <remarks>
/// The .NET framework exports encrypted keys only as PKCS#8, and SSH.NET
/// surfaces a wrong passphrase on that format as a BouncyCastle cipher-text
/// exception. The passphrase failures the connection factory swallows are the
/// <c>SshException</c> ones, which SSH.NET raises for PuTTY and OpenSSH key
/// files: for a PuTTY file the private-blob MAC does not verify. A fixture in
/// that format is what reaches the code path under test.
/// </remarks>
internal static class PuttyPrivateKeyFileWriter
{
    private const string KeyAlgorithm = "ssh-rsa";
    private const string EncryptionType = "aes256-cbc";
    private const string Comment = "heimdall-test";
    private const string MacKeySeed = "putty-private-key-file-mac-key";
    private const int AesKeyLength = 32;
    private const int AesBlockLength = 16;
    private const int Base64LineLength = 64;

    /// <summary>
    /// Writes an RSA key encrypted with <paramref name="passphrase"/> to a fresh
    /// temporary file and returns its path.
    /// </summary>
    public static string WriteTemporaryFile(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        using RSA rsa = RSA.Create(2048);
        RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: true);

        byte[] publicBlob = Concat(
            SshString(Encoding.ASCII.GetBytes(KeyAlgorithm)),
            SshMpint(parameters.Exponent!),
            SshMpint(parameters.Modulus!));
        byte[] privateBlob = Concat(
            SshMpint(parameters.D!),
            SshMpint(parameters.P!),
            SshMpint(parameters.Q!),
            SshMpint(parameters.InverseQ!));

        byte[] paddedPrivateBlob = PadWithBlobHash(privateBlob);
        byte[] encryptedPrivateBlob = EncryptPrivateBlob(paddedPrivateBlob, passphrase);
        string mac = ComputeMac(publicBlob, paddedPrivateBlob, passphrase);

        StringBuilder file = new StringBuilder();
        file.Append("PuTTY-User-Key-File-2: ").Append(KeyAlgorithm).Append('\n');
        file.Append("Encryption: ").Append(EncryptionType).Append('\n');
        file.Append("Comment: ").Append(Comment).Append('\n');
        AppendBase64Section(file, "Public-Lines", publicBlob);
        AppendBase64Section(file, "Private-Lines", encryptedPrivateBlob);
        file.Append("Private-MAC: ").Append(mac).Append('\n');

        string path = Path.Combine(Path.GetTempPath(), $"heimdall_test_key_{Guid.NewGuid():N}.ppk");
        File.WriteAllText(path, file.ToString());
        return path;
    }

    private static byte[] PadWithBlobHash(byte[] privateBlob)
    {
        int paddedLength = (privateBlob.Length + AesBlockLength - 1) / AesBlockLength * AesBlockLength;
        byte[] padded = new byte[paddedLength];
        privateBlob.CopyTo(padded, 0);
        byte[] blobHash = SHA1.HashData(privateBlob);
        Array.Copy(blobHash, 0, padded, privateBlob.Length, paddedLength - privateBlob.Length);
        return padded;
    }

    private static byte[] EncryptPrivateBlob(byte[] paddedPrivateBlob, string passphrase)
    {
        byte[] passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        byte[] key = new byte[AesKeyLength];
        byte[] firstHalf = SHA1.HashData(Concat(new byte[] { 0, 0, 0, 0 }, passphraseBytes));
        byte[] secondHalf = SHA1.HashData(Concat(new byte[] { 0, 0, 0, 1 }, passphraseBytes));
        Array.Copy(firstHalf, 0, key, 0, firstHalf.Length);
        Array.Copy(secondHalf, 0, key, firstHalf.Length, AesKeyLength - firstHalf.Length);

        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = new byte[AesBlockLength];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(paddedPrivateBlob, 0, paddedPrivateBlob.Length);
    }

    private static string ComputeMac(byte[] publicBlob, byte[] paddedPrivateBlob, string passphrase)
    {
        byte[] macKey = SHA1.HashData(Encoding.UTF8.GetBytes(MacKeySeed + passphrase));
        byte[] macData = Concat(
            SshString(Encoding.ASCII.GetBytes(KeyAlgorithm)),
            SshString(Encoding.ASCII.GetBytes(EncryptionType)),
            SshString(Encoding.ASCII.GetBytes(Comment)),
            SshString(publicBlob),
            SshString(paddedPrivateBlob));
        return Convert.ToHexString(HMACSHA1.HashData(macKey, macData)).ToLowerInvariant();
    }

    private static void AppendBase64Section(StringBuilder file, string header, byte[] payload)
    {
        string base64 = Convert.ToBase64String(payload);
        int lineCount = (base64.Length + Base64LineLength - 1) / Base64LineLength;
        file.Append(header).Append(": ").Append(lineCount).Append('\n');
        for (int offset = 0; offset < base64.Length; offset += Base64LineLength)
        {
            int length = Math.Min(Base64LineLength, base64.Length - offset);
            file.Append(base64, offset, length).Append('\n');
        }
    }

    private static byte[] SshString(byte[] payload)
    {
        byte[] result = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)payload.Length);
        payload.CopyTo(result, sizeof(uint));
        return result;
    }

    private static byte[] SshMpint(byte[] unsignedBigEndian)
    {
        int leadingZeros = 0;
        while (leadingZeros < unsignedBigEndian.Length - 1 && unsignedBigEndian[leadingZeros] == 0)
        {
            leadingZeros++;
        }

        bool needsSignByte = (unsignedBigEndian[leadingZeros] & 0x80) != 0;
        byte[] magnitude = new byte[unsignedBigEndian.Length - leadingZeros + (needsSignByte ? 1 : 0)];
        Array.Copy(unsignedBigEndian, leadingZeros, magnitude, needsSignByte ? 1 : 0, unsignedBigEndian.Length - leadingZeros);
        return SshString(magnitude);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
