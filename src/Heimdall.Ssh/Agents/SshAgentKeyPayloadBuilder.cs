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

using System.Security.Cryptography;
using System.Text;
using Heimdall.Ssh.OpenSsh;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Heimdall.Ssh.Agents;

/// <summary>
/// Builds OpenSSH <c>PROTOCOL.agent</c> <c>SSH2_AGENTC_ADD_IDENTITY</c> /
/// <c>SSH2_AGENTC_ADD_ID_CONSTRAINED</c> wire payloads from PEM or OpenSSH private keys.
/// </summary>
public static class SshAgentKeyPayloadBuilder
{
    /// <summary>
    /// Parses <paramref name="privateKeyText"/> (OpenSSH or PEM, encrypted or unencrypted) and
    /// serializes an agent add-identity request, optionally constrained by <paramref name="lifetimeSeconds"/>.
    /// </summary>
    public static byte[] BuildAddIdentityMessage(
        string privateKeyText,
        string? passphrase = null,
        string? commentOverride = null,
        uint? lifetimeSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyText);

        var keyBytes = Encoding.UTF8.GetBytes(privateKeyText);
        try
        {
            using var ms = new MemoryStream(keyBytes);
            using var keyFile = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(ms)
                : new PrivateKeyFile(ms, passphrase);

            var effectiveComment = commentOverride ?? keyFile.Key.Comment ?? string.Empty;
            var messageType = lifetimeSeconds is > 0
                ? OpenSshAgentProtocol.SshAgentcAddIdConstrained
                : OpenSshAgentProtocol.SshAgentcAddIdentity;

            return OpenSshAgentProtocol.BuildMessage(messageType, writer =>
            {
                WritePrivateKeyBody(writer, keyFile.Key, effectiveComment);
                if (lifetimeSeconds is > 0)
                {
                    writer.WriteByte(OpenSshAgentProtocol.SshAgentConstrainLifetime);
                    writer.WriteUInt32(lifetimeSeconds.Value);
                }
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static void WritePrivateKeyBody(
        OpenSshAgentProtocol.AgentMessageWriter writer,
        Key key,
        string comment)
    {
        switch (key)
        {
            case ED25519Key ed25519:
                WriteEd25519(writer, ed25519, comment);
                break;

            case RsaKey rsa:
                WriteRsa(writer, rsa, comment);
                break;

            case EcdsaKey ecdsa:
                WriteEcdsa(writer, ecdsa, comment);
                break;

            default:
                throw new NotSupportedException($"Unsupported SSH private key type for agent loading: {key.GetType().Name}.");
        }
    }

    private static void WriteEd25519(
        OpenSshAgentProtocol.AgentMessageWriter writer,
        ED25519Key key,
        string comment)
    {
        var pub = key.PublicKey;
        var priv = key.PrivateKey;
        byte[] priv64;
        if (priv.Length == 64)
        {
            priv64 = (byte[])priv.Clone();
        }
        else if (priv.Length == 32 && pub.Length == 32)
        {
            priv64 = new byte[64];
            Buffer.BlockCopy(priv, 0, priv64, 0, 32);
            Buffer.BlockCopy(pub, 0, priv64, 32, 32);
        }
        else
        {
            throw new InvalidOperationException("Unexpected Ed25519 key material length.");
        }

        try
        {
            writer.WriteUtf8String("ssh-ed25519");
            writer.WriteString(pub);
            writer.WriteString(priv64);
            writer.WriteUtf8String(comment);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(priv64);
        }
    }

    private static void WriteRsa(
        OpenSshAgentProtocol.AgentMessageWriter writer,
        RsaKey key,
        string comment)
    {
        var n = key.Modulus.ToByteArray(isUnsigned: true, isBigEndian: true);
        var e = key.Exponent.ToByteArray(isUnsigned: true, isBigEndian: true);
        var d = key.D.ToByteArray(isUnsigned: true, isBigEndian: true);
        var iqmp = key.InverseQ.ToByteArray(isUnsigned: true, isBigEndian: true);
        var p = key.P.ToByteArray(isUnsigned: true, isBigEndian: true);
        var q = key.Q.ToByteArray(isUnsigned: true, isBigEndian: true);

        try
        {
            writer.WriteUtf8String("ssh-rsa");
            writer.WriteMpint(n);
            writer.WriteMpint(e);
            writer.WriteMpint(d);
            writer.WriteMpint(iqmp);
            writer.WriteMpint(p);
            writer.WriteMpint(q);
            writer.WriteUtf8String(comment);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(d);
            CryptographicOperations.ZeroMemory(iqmp);
            CryptographicOperations.ZeroMemory(p);
            CryptographicOperations.ZeroMemory(q);
        }
    }

    private static void WriteEcdsa(
        OpenSshAgentProtocol.AgentMessageWriter writer,
        EcdsaKey key,
        string comment)
    {
        var curveName = key.KeyLength switch
        {
            256 => "nistp256",
            384 => "nistp384",
            521 => "nistp521",
            _ => throw new NotSupportedException($"Unsupported ECDSA key length: {key.KeyLength}.")
        };

        var parameters = key.Ecdsa.ExportParameters(includePrivateParameters: true);
        if (parameters.Q.X is null || parameters.Q.Y is null || parameters.D is null)
        {
            throw new InvalidOperationException("ECDSA private key parameters are incomplete.");
        }

        var point = new byte[1 + parameters.Q.X.Length + parameters.Q.Y.Length];
        point[0] = 0x04;
        Buffer.BlockCopy(parameters.Q.X, 0, point, 1, parameters.Q.X.Length);
        Buffer.BlockCopy(parameters.Q.Y, 0, point, 1 + parameters.Q.X.Length, parameters.Q.Y.Length);

        try
        {
            writer.WriteUtf8String($"ecdsa-sha2-{curveName}");
            writer.WriteUtf8String(curveName);
            writer.WriteString(point);
            writer.WriteMpint(parameters.D);
            writer.WriteUtf8String(comment);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }
}
