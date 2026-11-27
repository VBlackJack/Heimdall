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

using Renci.SshNet;

namespace Heimdall.Ssh;

/// <summary>
/// Negotiated cryptographic and transport diagnostics captured from an active SSH session.
/// Never contains secrets or user credentials.
/// </summary>
public sealed record SshSessionDiagnostics(
    string Host,
    int Port,
    string ServerVersion,
    string KeyExchangeAlgorithm,
    string HostKeyAlgorithm,
    string ClientToServerEncryption,
    string ServerToClientEncryption,
    string ClientToServerMac,
    string ServerToClientMac,
    string ClientToServerCompression,
    string ServerToClientCompression,
    int ActiveForwardedPorts,
    bool HasWeakCrypto)
{
    private const string UnknownToken = "-";

    /// <summary>
    /// Captures negotiated transport metadata from a connected SSH.NET <see cref="ConnectionInfo"/>.
    /// </summary>
    public static SshSessionDiagnostics FromConnectionInfo(
        ConnectionInfo info,
        int activeForwardedPorts = 0)
    {
        ArgumentNullException.ThrowIfNull(info);

        var kex = Normalize(info.CurrentKeyExchangeAlgorithm);
        var hostKey = Normalize(info.CurrentHostKeyAlgorithm);
        var c2sCipher = Normalize(info.CurrentClientEncryption);
        var s2cCipher = Normalize(info.CurrentServerEncryption);
        var c2sMac = Normalize(info.CurrentClientHmacAlgorithm);
        var s2cMac = Normalize(info.CurrentServerHmacAlgorithm);
        var c2sComp = Normalize(info.CurrentClientCompressionAlgorithm);
        var s2cComp = Normalize(info.CurrentServerCompressionAlgorithm);
        var serverVersion = Normalize(info.ServerVersion);

        var weak = IsWeakCrypto(kex, hostKey, c2sCipher, c2sMac)
            || IsWeakCrypto(kex, hostKey, s2cCipher, s2cMac);

        return new SshSessionDiagnostics(
            info.Host ?? string.Empty,
            info.Port,
            serverVersion,
            kex,
            hostKey,
            c2sCipher,
            s2cCipher,
            c2sMac,
            s2cMac,
            c2sComp,
            s2cComp,
            Math.Max(0, activeForwardedPorts),
            weak);
    }

    /// <summary>
    /// Returns true when any negotiated SSH transport algorithm uses deprecated/weak primitives
    /// (SHA-1 KEX, DSS host key, CBC/RC4/3DES/Blowfish ciphers, or MD5/SHA-1 MACs).
    /// </summary>
    public static bool IsWeakCrypto(
        string? keyExchangeAlgorithm,
        string? hostKeyAlgorithm,
        string? encryptionAlgorithm,
        string? macAlgorithm)
    {
        if (IsWeakKex(keyExchangeAlgorithm)
            || IsWeakHostKey(hostKeyAlgorithm)
            || IsWeakCipher(encryptionAlgorithm))
        {
            return true;
        }

        // AEAD ciphers (GCM / ChaCha20-Poly1305) authenticate without a separate MAC.
        if (!IsAeadCipher(encryptionAlgorithm) && IsWeakMac(macAlgorithm))
        {
            return true;
        }

        return false;
    }

    private static bool IsWeakKex(string? kex)
    {
        if (string.IsNullOrWhiteSpace(kex) || kex == UnknownToken)
        {
            return false;
        }

        return kex.EndsWith("-sha1", StringComparison.OrdinalIgnoreCase)
            || kex.Contains("group1-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWeakHostKey(string? hostKey)
    {
        if (string.IsNullOrWhiteSpace(hostKey) || hostKey == UnknownToken)
        {
            return false;
        }

        return string.Equals(hostKey, "ssh-dss", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWeakCipher(string? cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher) || cipher == UnknownToken)
        {
            return false;
        }

        return cipher.EndsWith("-cbc", StringComparison.OrdinalIgnoreCase)
            || cipher.Contains("arcfour", StringComparison.OrdinalIgnoreCase)
            || cipher.Contains("3des", StringComparison.OrdinalIgnoreCase)
            || cipher.Contains("blowfish", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cipher, "none", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAeadCipher(string? cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher))
        {
            return false;
        }

        return cipher.Contains("gcm", StringComparison.OrdinalIgnoreCase)
            || cipher.Contains("poly1305", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWeakMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac) || mac == UnknownToken)
        {
            return false;
        }

        return mac.Contains("md5", StringComparison.OrdinalIgnoreCase)
            || mac.Contains("sha1", StringComparison.OrdinalIgnoreCase)
            || mac.Contains("-96", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mac, "none", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? UnknownToken : value.Trim();
}
