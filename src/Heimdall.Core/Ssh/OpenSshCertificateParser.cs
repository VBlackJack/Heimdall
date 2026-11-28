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
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Heimdall.Core.Ssh;

/// <summary>OpenSSH certificate type as defined in PROTOCOL.certkeys.</summary>
public enum OpenSshCertificateType : uint
{
    User = 1,
    Host = 2
}

/// <summary>
/// Parsed representation of an OpenSSH v01 certificate (<c>*-cert-v01@openssh.com</c>).
/// </summary>
public sealed record OpenSshCertificateInfo(
    string KeyType,
    string UnderlyingKeyType,
    int KeySize,
    OpenSshCertificateType Type,
    ulong Serial,
    string KeyId,
    IReadOnlyList<string> ValidPrincipals,
    ulong ValidAfterUnix,
    ulong ValidBeforeUnix,
    IReadOnlyDictionary<string, string> CriticalOptions,
    IReadOnlyDictionary<string, string> Extensions,
    byte[] SignatureKeyBlob,
    string SignatureKeyFingerprint,
    string CertificateFingerprint,
    byte[] SignedData,
    byte[] SignatureBlob,
    string? SubjectPublicKeyFingerprint = null,
    byte[]? SubjectPublicKeyBlob = null)
{
    public DateTimeOffset? ValidAfter => ValidAfterUnix == 0 || ValidAfterUnix > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()
        ? null
        : DateTimeOffset.FromUnixTimeSeconds((long)ValidAfterUnix);

    public DateTimeOffset? ValidBefore => ValidBeforeUnix == ulong.MaxValue || ValidBeforeUnix > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()
        ? null
        : DateTimeOffset.FromUnixTimeSeconds((long)ValidBeforeUnix);

    public bool HasUnboundedValidity => ValidBeforeUnix == ulong.MaxValue;

    /// <summary>Returns true when <paramref name="instant"/> falls within [ValidAfter, ValidBefore].</summary>
    public bool IsValidAt(DateTimeOffset instant)
    {
        var unix = instant.ToUnixTimeSeconds();
        if (unix < 0)
        {
            return false;
        }

        var now = (ulong)unix;
        return now >= ValidAfterUnix && now <= ValidBeforeUnix;
    }

    /// <summary>
    /// Returns true when <paramref name="principal"/> is allowed by <see cref="ValidPrincipals"/>.
    /// Per OpenSSH PROTOCOL.certkeys, principals are matched literally (case-insensitive for DNS hostnames),
    /// and an empty principals list acts as a wildcard matching any principal.
    /// </summary>
    public bool MatchesPrincipal(string principal)
    {
        if (string.IsNullOrWhiteSpace(principal))
        {
            return false;
        }

        if (ValidPrincipals.Count == 0)
        {
            return true;
        }

        foreach (var candidate in ValidPrincipals)
        {
            if (string.Equals(candidate, principal, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Pure managed parser for OpenSSH v01 certificate blobs and public lines (PROTOCOL.certkeys).
/// </summary>
public static class OpenSshCertificateParser
{
    public const string CertEd25519 = "ssh-ed25519-cert-v01@openssh.com";
    public const string CertRsa = "ssh-rsa-cert-v01@openssh.com";
    public const string CertEcdsa256 = "ecdsa-sha2-nistp256-cert-v01@openssh.com";
    public const string CertEcdsa384 = "ecdsa-sha2-nistp384-cert-v01@openssh.com";
    public const string CertEcdsa521 = "ecdsa-sha2-nistp521-cert-v01@openssh.com";
    public const string CertSkEd25519 = "sk-ssh-ed25519-cert-v01@openssh.com";
    public const string CertSkEcdsa256 = "sk-ecdsa-sha2-nistp256-cert-v01@openssh.com";

    public static bool IsCertificateKeyType(string? keyType) => keyType is
        CertEd25519 or CertRsa or CertEcdsa256 or CertEcdsa384 or CertEcdsa521 or CertSkEd25519 or CertSkEcdsa256;

    public static bool TryParsePublicLine(string? line, out OpenSshCertificateInfo? certificate)
    {
        certificate = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !IsCertificateKeyType(parts[0]))
        {
            return false;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        return TryParseBlob(blob, out certificate)
            && string.Equals(certificate!.KeyType, parts[0], StringComparison.Ordinal);
    }

    public static bool TryParseBlob(byte[]? blob, out OpenSshCertificateInfo? certificate)
    {
        certificate = null;
        if (blob is null || blob.Length < 32)
        {
            return false;
        }

        var offset = 0;
        if (!TryReadString(blob, ref offset, out var keyType) || !IsCertificateKeyType(keyType))
        {
            return false;
        }

        // Nonce
        if (!TryReadBytes(blob, ref offset, out _))
        {
            return false;
        }

        string underlyingKeyType;
        int keySize;
        byte[] subjectPublicKeyBlob;

        switch (keyType)
        {
            case CertEd25519:
                if (!TryReadBytes(blob, ref offset, out var pk) || pk.Length != 32)
                {
                    return false;
                }

                underlyingKeyType = "ssh-ed25519";
                keySize = 256;
                subjectPublicKeyBlob = BuildStandardPublicKeyBlob(underlyingKeyType, pk);
                break;

            case CertSkEd25519:
                if (!TryReadBytes(blob, ref offset, out var skPk) || skPk.Length != 32
                    || !TryReadBytes(blob, ref offset, out var skEdApp))
                {
                    return false;
                }

                underlyingKeyType = "sk-ssh-ed25519@openssh.com";
                keySize = 256;
                subjectPublicKeyBlob = BuildStandardPublicKeyBlob(underlyingKeyType, skPk, skEdApp);
                break;

            case CertSkEcdsa256:
                if (!TryReadString(blob, ref offset, out var skCurve)
                    || !string.Equals(skCurve, "nistp256", StringComparison.Ordinal)
                    || !TryReadBytes(blob, ref offset, out var skQPoint)
                    || !TryReadBytes(blob, ref offset, out var skEcApp))
                {
                    return false;
                }

                underlyingKeyType = "sk-ecdsa-sha2-nistp256@openssh.com";
                keySize = 256;
                subjectPublicKeyBlob = BuildStandardPublicKeyBlob(underlyingKeyType, Encoding.ASCII.GetBytes(skCurve), skQPoint, skEcApp);
                break;

            case CertRsa:
                if (!TryReadBytes(blob, ref offset, out var rsaE) || !TryReadBytes(blob, ref offset, out var n))
                {
                    return false;
                }

                underlyingKeyType = "ssh-rsa";
                keySize = GetMpintBitLength(n);
                subjectPublicKeyBlob = BuildStandardPublicKeyBlob(underlyingKeyType, rsaE, n);
                break;

            case CertEcdsa256:
            case CertEcdsa384:
            case CertEcdsa521:
                if (!TryReadString(blob, ref offset, out var curve) || !TryReadBytes(blob, ref offset, out var qPoint))
                {
                    return false;
                }

                var expectedCurve = keyType switch
                {
                    CertEcdsa256 => "nistp256",
                    CertEcdsa384 => "nistp384",
                    CertEcdsa521 => "nistp521",
                    _ => string.Empty
                };

                if (!string.Equals(curve, expectedCurve, StringComparison.Ordinal))
                {
                    return false;
                }

                keySize = curve switch
                {
                    "nistp256" => 256,
                    "nistp384" => 384,
                    "nistp521" => 521,
                    _ => 0
                };
                if (keySize == 0)
                {
                    return false;
                }

                underlyingKeyType = $"ecdsa-sha2-{curve}";
                subjectPublicKeyBlob = BuildStandardPublicKeyBlob(underlyingKeyType, Encoding.ASCII.GetBytes(curve), qPoint);
                break;

            default:
                return false;
        }

        if (!TryReadUInt64(blob, ref offset, out var serial)
            || !TryReadUInt32(blob, ref offset, out var rawType)
            || (rawType != (uint)OpenSshCertificateType.User && rawType != (uint)OpenSshCertificateType.Host)
            || !TryReadString(blob, ref offset, out var keyId)
            || !TryReadBytes(blob, ref offset, out var principalsBlob)
            || !TryReadUInt64(blob, ref offset, out var validAfter)
            || !TryReadUInt64(blob, ref offset, out var validBefore)
            || !TryReadBytes(blob, ref offset, out var criticalOptionsBlob)
            || !TryReadBytes(blob, ref offset, out var extensionsBlob)
            || !TryReadBytes(blob, ref offset, out _) // reserved
            || !TryReadBytes(blob, ref offset, out var signatureKeyBlob))
        {
            return false;
        }

        var signedLength = offset;
        if (!TryReadBytes(blob, ref offset, out var signatureBlob) || offset != blob.Length)
        {
            return false;
        }

        if (!TryParseStringList(principalsBlob, out var principals)
            || !TryParseNameValueMap(criticalOptionsBlob, out var criticalOptions)
            || !TryParseNameValueMap(extensionsBlob, out var extensions))
        {
            return false;
        }

        var signedData = blob.AsSpan(0, signedLength).ToArray();
        var caFingerprint = HostKeyFormats.ComputeSha256Fingerprint(signatureKeyBlob);
        var certFingerprint = HostKeyFormats.ComputeSha256Fingerprint(blob);
        var subjectKeyFingerprint = HostKeyFormats.ComputeSha256Fingerprint(subjectPublicKeyBlob);

        certificate = new OpenSshCertificateInfo(
            keyType,
            underlyingKeyType,
            keySize,
            (OpenSshCertificateType)rawType,
            serial,
            keyId,
            principals,
            validAfter,
            validBefore,
            criticalOptions,
            extensions,
            signatureKeyBlob,
            caFingerprint,
            certFingerprint,
            signedData,
            signatureBlob,
            subjectKeyFingerprint,
            subjectPublicKeyBlob);
        return true;
    }

    private static byte[] BuildStandardPublicKeyBlob(string keyType, params byte[][] parts)
    {
        var typeBytes = Encoding.ASCII.GetBytes(keyType);
        var total = 4 + typeBytes.Length;
        foreach (var part in parts)
        {
            total += 4 + part.Length;
        }

        var buffer = new byte[total];
        var pos = 0;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), (uint)typeBytes.Length);
        pos += 4;
        typeBytes.CopyTo(buffer.AsSpan(pos));
        pos += typeBytes.Length;

        foreach (var part in parts)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), (uint)part.Length);
            pos += 4;
            part.CopyTo(buffer.AsSpan(pos));
            pos += part.Length;
        }

        return buffer;
    }

    private static bool TryParseStringList(byte[] data, out IReadOnlyList<string> items)
    {
        var list = new List<string>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadString(data, ref offset, out var item))
            {
                items = [];
                return false;
            }

            list.Add(item);
        }

        items = list;
        return true;
    }

    private static bool TryParseNameValueMap(byte[] data, out IReadOnlyDictionary<string, string> map)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadString(data, ref offset, out var name)
                || !TryReadBytes(data, ref offset, out var rawVal))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                return false;
            }

            string value = string.Empty;
            if (rawVal.Length > 0)
            {
                var valOffset = 0;
                value = TryReadString(rawVal, ref valOffset, out var inner) && valOffset == rawVal.Length
                    ? inner
                    : Convert.ToHexString(rawVal);
            }

            dict[name] = value;
        }

        map = dict;
        return true;
    }

    private static bool TryReadUInt32(byte[] data, ref int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || data.Length - offset < 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;
        return true;
    }

    private static bool TryReadUInt64(byte[] data, ref int offset, out ulong value)
    {
        value = 0;
        if (offset < 0 || data.Length - offset < 8)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
        offset += 8;
        return true;
    }

    private static bool TryReadBytes(byte[] data, ref int offset, out byte[] value)
    {
        value = [];
        if (!TryReadUInt32(data, ref offset, out var length))
        {
            return false;
        }

        if (length > (uint)(data.Length - offset))
        {
            return false;
        }

        value = data.AsSpan(offset, (int)length).ToArray();
        offset += (int)length;
        return true;
    }

    private static bool TryReadString(byte[] data, ref int offset, out string value)
    {
        value = string.Empty;
        if (!TryReadBytes(data, ref offset, out var bytes))
        {
            return false;
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static int GetMpintBitLength(byte[] mpint)
    {
        var start = 0;
        while (start < mpint.Length && mpint[start] == 0)
        {
            start++;
        }

        if (start >= mpint.Length)
        {
            return 0;
        }

        var firstByte = mpint[start];
        var highBits = 0;
        while (firstByte > 0)
        {
            highBits++;
            firstByte >>= 1;
        }

        return (mpint.Length - start - 1) * 8 + highBits;
    }
}

/// <summary>
/// OpenSSH host-pattern matcher supporting comma-separated wildcards (<c>*</c>, <c>?</c>),
/// negation (<c>!pattern</c>), and bracketed non-default port tokens (<c>[host]:port</c>).
/// </summary>
public static class OpenSshHostPatternMatcher
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    public static bool Matches(string patternList, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(patternList) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var candidateDefault = host;
        var candidateWithPort = string.Create(CultureInfo.InvariantCulture, $"[{host}]:{port}");
        var matched = false;

        foreach (var rawToken in patternList.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var negated = token.StartsWith('!');
            var pattern = negated ? token[1..] : token;
            if (pattern.Length == 0)
            {
                continue;
            }

            var tokenMatches = KnownHostsHash.IsHashedToken(pattern)
                ? (port == 22
                    ? KnownHostsHash.TryMatches(pattern, candidateDefault) || KnownHostsHash.TryMatches(pattern, candidateWithPort)
                    : KnownHostsHash.TryMatches(pattern, candidateWithPort))
                : pattern.StartsWith('[')
                    ? MatchesWildcard(pattern, candidateWithPort)
                    : port == 22
                        ? MatchesWildcard(pattern, candidateDefault) || MatchesWildcard(pattern, candidateWithPort)
                        : string.Equals(pattern, "*", StringComparison.Ordinal);

            if (tokenMatches)
            {
                if (negated)
                {
                    return false;
                }

                matched = true;
            }
        }

        return matched;
    }

    public static bool MatchesWildcard(string pattern, string candidate)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        if (pattern == "*")
        {
            return true;
        }

        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase);
        }

        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        try
        {
            return Regex.IsMatch(candidate, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
