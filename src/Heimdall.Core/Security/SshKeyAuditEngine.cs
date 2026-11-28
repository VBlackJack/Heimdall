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
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Heimdall.Core.Security;

/// <summary>
/// Overall security rating for an SSH key.
/// </summary>
public enum SecurityRating
{
    Strong,
    Acceptable,
    Weak,
    Deprecated
}

/// <summary>
/// Severity level for an individual audit finding.
/// </summary>
public enum FindingSeverity
{
    Pass,
    Warning,
    Fail
}

/// <summary>
/// Result of parsing and auditing an SSH key. Fully decoupled from WPF.
/// </summary>
public sealed class SshKeyAuditResult
{
    public string Algorithm { get; init; } = string.Empty;
    public int KeySize { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public bool IsPrivateKey { get; init; }
    public bool IsEncrypted { get; init; }
    public SecurityRating Rating { get; init; }
    public IReadOnlyList<SshKeyAuditFinding> Findings { get; init; } = [];
}

/// <summary>
/// A single security finding from the key audit.
/// </summary>
public sealed class SshKeyAuditFinding
{
    public FindingSeverity Severity { get; init; }
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Pure parsing and security assessment engine for SSH keys.
/// Supports OpenSSH (public + private), PEM (PKCS#1, PKCS#8, Legacy EC/DSA),
/// and SPKI formats. Thread-safe and stateless.
/// </summary>
public static class SshKeyAuditEngine
{
    private const int MinRsaKeySize = 2048;
    private const int StrongRsaKeySize = 3072;
    private const int StrongEcdsaKeySize = 384;
    private const int Ed25519KeySize = 256;

    public const int MaxKeyFileSize = 1_048_576; // 1 MB

    private const string OpenSshRsaPrefix = "ssh-rsa";
    private const string OpenSshDsaPrefix = "ssh-dss";
    private const string OpenSshEd25519Prefix = "ssh-ed25519";
    private const string OpenSshEcdsaNistp256Prefix = "ecdsa-sha2-nistp256";
    private const string OpenSshEcdsaNistp384Prefix = "ecdsa-sha2-nistp384";
    private const string OpenSshEcdsaNistp521Prefix = "ecdsa-sha2-nistp521";
    private const string OpenSshSkEd25519Prefix = "sk-ssh-ed25519@openssh.com";
    private const string OpenSshSkEcdsaNistp256Prefix = "sk-ecdsa-sha2-nistp256@openssh.com";

    private const string PpkV2HeaderPrefix = "PuTTY-User-Key-File-2:";
    private const string PpkV3HeaderPrefix = "PuTTY-User-Key-File-3:";

    private const string PemBeginOpenSshPrivate = "-----BEGIN OPENSSH PRIVATE KEY-----";
    private const string PemBeginRsaPrivate = "-----BEGIN RSA PRIVATE KEY-----";
    private const string PemBeginDsaPrivate = "-----BEGIN DSA PRIVATE KEY-----";
    private const string PemBeginEcPrivate = "-----BEGIN EC PRIVATE KEY-----";
    private const string PemBeginPrivate = "-----BEGIN PRIVATE KEY-----";
    private const string PemBeginEncryptedPrivate = "-----BEGIN ENCRYPTED PRIVATE KEY-----";
    private const string PemBeginPublic = "-----BEGIN PUBLIC KEY-----";
    private const string PemBeginRsaPublic = "-----BEGIN RSA PUBLIC KEY-----";

    private const string ProcTypeEncrypted = "Proc-Type: 4,ENCRYPTED";
    private const string OpenSshKdfBcrypt = "bcrypt";

    /// <summary>
    /// Parses an SSH key (any supported format) and returns the audit result,
    /// or <c>null</c> if the key could not be parsed.
    /// Input longer than <see cref="MaxKeyFileSize"/> is rejected and returns <c>null</c>.
    /// </summary>
    public static SshKeyAuditResult? Audit(string keyText, Func<string, string>? localize = null)
    {
        if (string.IsNullOrWhiteSpace(keyText))
        {
            return null;
        }

        if (keyText.Length > MaxKeyFileSize)
        {
            return null;
        }

        keyText = keyText.Trim();

        var firstLine = keyText.Split('\n')[0].TrimEnd('\r').Trim();

        if (Ssh.OpenSshCertificateParser.TryParsePublicLine(firstLine, out var cert) && cert is not null)
        {
            return BuildCertificateAuditResult(cert, localize);
        }

        if (firstLine.StartsWith(PpkV2HeaderPrefix, StringComparison.Ordinal)
            || firstLine.StartsWith(PpkV3HeaderPrefix, StringComparison.Ordinal))
        {
            return ParsePuttyPpkKey(keyText, localize);
        }

        if (firstLine.StartsWith(OpenSshRsaPrefix + " ", StringComparison.Ordinal) ||
            firstLine.StartsWith(OpenSshDsaPrefix + " ", StringComparison.Ordinal) ||
            firstLine.StartsWith(OpenSshEd25519Prefix + " ", StringComparison.Ordinal) ||
            firstLine.StartsWith(OpenSshEcdsaNistp256Prefix + " ", StringComparison.Ordinal) ||
            firstLine.StartsWith(OpenSshEcdsaNistp384Prefix + " ", StringComparison.Ordinal) ||
            firstLine.StartsWith(OpenSshEcdsaNistp521Prefix + " ", StringComparison.Ordinal) ||
            firstLine.StartsWith(OpenSshSkEd25519Prefix + " ", StringComparison.Ordinal) ||
            firstLine.StartsWith(OpenSshSkEcdsaNistp256Prefix + " ", StringComparison.Ordinal))
        {
            return ParseOpenSshPublicKey(firstLine, localize);
        }

        if (keyText.Contains(PemBeginOpenSshPrivate, StringComparison.Ordinal))
        {
            return ParseOpenSshPrivateKey(keyText, localize);
        }

        if (keyText.Contains(PemBeginRsaPrivate, StringComparison.Ordinal))
        {
            return ParsePkcs1RsaPrivateKey(keyText, localize);
        }

        if (keyText.Contains(PemBeginDsaPrivate, StringComparison.Ordinal))
        {
            return ParseLegacyDsaPrivateKey(keyText, localize);
        }

        if (keyText.Contains(PemBeginEcPrivate, StringComparison.Ordinal))
        {
            return ParseLegacyEcPrivateKey(keyText, localize);
        }

        if (keyText.Contains(PemBeginEncryptedPrivate, StringComparison.Ordinal))
        {
            return ParsePkcs8PrivateKey(keyText, isEncrypted: true, localize);
        }

        if (keyText.Contains(PemBeginPrivate, StringComparison.Ordinal))
        {
            return ParsePkcs8PrivateKey(keyText, isEncrypted: false, localize);
        }

        if (keyText.Contains(PemBeginPublic, StringComparison.Ordinal))
        {
            return ParseSpkiPublicKey(keyText, localize);
        }

        if (keyText.Contains(PemBeginRsaPublic, StringComparison.Ordinal))
        {
            return ParsePkcs1RsaPublicKey(keyText, localize);
        }

        return null;
    }

    private static SshKeyAuditResult? ParseOpenSshPublicKey(string line, Func<string, string>? localize)
    {
        var parts = line.Split(' ', 3);
        if (parts.Length < 2)
        {
            return null;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        return ParseOpenSshBlob(blob, parts[0], isPrivate: false, isEncrypted: false, "OpenSSH", localize);
    }

    private static SshKeyAuditResult BuildCertificateAuditResult(
        Ssh.OpenSshCertificateInfo cert,
        Func<string, string>? localize)
    {
        string algorithm = cert.UnderlyingKeyType switch
        {
            "ssh-ed25519" => "Ed25519",
            "ssh-rsa" => "RSA",
            _ when cert.UnderlyingKeyType.StartsWith("ecdsa-", StringComparison.Ordinal) => "ECDSA",
            _ => cert.UnderlyingKeyType
        };

        var findings = new List<SshKeyAuditFinding>();
        var now = DateTimeOffset.UtcNow;
        var rating = SecurityRating.Strong;

        if (!cert.IsValidAt(now))
        {
            rating = SecurityRating.Weak;
            findings.Add(new SshKeyAuditFinding
            {
                Severity = FindingSeverity.Fail,
                Text = L(localize, "ToolSshAuditFindingCertExpired")
            });
        }
        else if (cert.HasUnboundedValidity)
        {
            rating = SecurityRating.Acceptable;
            findings.Add(new SshKeyAuditFinding
            {
                Severity = FindingSeverity.Warning,
                Text = L(localize, "ToolSshAuditFindingCertUnbounded")
            });
        }
        else
        {
            findings.Add(new SshKeyAuditFinding
            {
                Severity = FindingSeverity.Pass,
                Text = L(localize, "ToolSshAuditFindingCertValid")
            });
        }

        if (algorithm == "RSA" && cert.KeySize < MinRsaKeySize)
        {
            rating = SecurityRating.Weak;
            findings.Add(new SshKeyAuditFinding
            {
                Severity = FindingSeverity.Fail,
                Text = string.Format(L(localize, "ToolSshAuditFindingRsaWeak"), cert.KeySize)
            });
        }
        else
        {
            findings.Add(new SshKeyAuditFinding
            {
                Severity = FindingSeverity.Pass,
                Text = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    L(localize, "ToolSshAuditCertMetadataFormat"),
                    cert.SignatureKeyFingerprint,
                    cert.KeyId,
                    cert.ValidPrincipals.Count == 0 ? "*" : string.Join(", ", cert.ValidPrincipals))
            });
        }

        var format = cert.Type == Ssh.OpenSshCertificateType.Host
            ? L(localize, "ToolSshAuditFormatCertHost")
            : L(localize, "ToolSshAuditFormatCertUser");

        return new SshKeyAuditResult
        {
            Algorithm = algorithm,
            KeySize = cert.KeySize,
            Fingerprint = cert.CertificateFingerprint,
            Format = format,
            IsPrivateKey = false,
            IsEncrypted = false,
            Rating = rating,
            Findings = findings
        };
    }

    private static SshKeyAuditResult? ParsePuttyPpkKey(string keyText, Func<string, string>? localize)
    {
        try
        {
            string[] lines = keyText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            if (lines.Length < 4)
            {
                return null;
            }

            string headerLine = lines[0].Trim();
            bool isPpkV2;
            string algorithmTag;
            if (headerLine.StartsWith(PpkV3HeaderPrefix, StringComparison.Ordinal))
            {
                isPpkV2 = false;
                algorithmTag = headerLine[PpkV3HeaderPrefix.Length..].Trim();
            }
            else if (headerLine.StartsWith(PpkV2HeaderPrefix, StringComparison.Ordinal))
            {
                isPpkV2 = true;
                algorithmTag = headerLine[PpkV2HeaderPrefix.Length..].Trim();
            }
            else
            {
                return null;
            }

            string? encryption = null;
            int publicLineCount = -1;
            int publicLinesStart = -1;

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("Encryption:", StringComparison.OrdinalIgnoreCase))
                {
                    encryption = line["Encryption:".Length..].Trim();
                }
                else if (line.StartsWith("Public-Lines:", StringComparison.OrdinalIgnoreCase))
                {
                    string countText = line["Public-Lines:".Length..].Trim();
                    if (!int.TryParse(countText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out publicLineCount)
                        || publicLineCount <= 0
                        || publicLineCount > 256)
                    {
                        return null;
                    }

                    publicLinesStart = i + 1;
                    break;
                }
            }

            if (publicLinesStart < 0 || publicLinesStart + publicLineCount > lines.Length)
            {
                return null;
            }

            var b64 = new StringBuilder();
            for (int i = 0; i < publicLineCount; i++)
            {
                b64.Append(lines[publicLinesStart + i].Trim());
            }

            byte[] blob = Convert.FromBase64String(b64.ToString());
            bool isEncrypted = !string.IsNullOrWhiteSpace(encryption)
                && !string.Equals(encryption, "none", StringComparison.OrdinalIgnoreCase);
            string format = isPpkV2 ? "PuTTY PPK v2" : "PuTTY PPK v3";

            return ParseOpenSshBlob(
                blob,
                algorithmTag,
                isPrivate: true,
                isEncrypted: isEncrypted,
                format: format,
                localize: localize,
                isOldFormat: isPpkV2);
        }
        catch (Exception ex) when (IsExpectedParseException(ex))
        {
            return null;
        }
    }

    private static SshKeyAuditResult? ParseOpenSshBlob(
        byte[] blob,
        string algorithmTag,
        bool isPrivate,
        bool isEncrypted,
        string format,
        Func<string, string>? localize,
        bool isOldFormat = false)
    {
        try
        {
            using var ms = new MemoryStream(blob);

            var blobAlgorithm = ReadOpenSshString(ms);
            if (blobAlgorithm is null)
            {
                return null;
            }

            string algorithm;
            int keySize;
            byte[] publicKeyBlob = blob;

            if (blobAlgorithm == OpenSshRsaPrefix)
            {
                algorithm = "RSA";
                var exponent = ReadOpenSshBytes(ms);
                var modulus = ReadOpenSshBytes(ms);
                if (exponent is null || modulus is null)
                {
                    return null;
                }

                keySize = GetMpintBitLength(modulus);
            }
            else if (blobAlgorithm == OpenSshDsaPrefix)
            {
                algorithm = "DSA";
                var p = ReadOpenSshBytes(ms);
                if (p is null)
                {
                    return null;
                }

                keySize = GetMpintBitLength(p);
            }
            else if (blobAlgorithm == OpenSshEd25519Prefix)
            {
                algorithm = "Ed25519";
                keySize = Ed25519KeySize;
            }
            else if (blobAlgorithm == OpenSshSkEd25519Prefix)
            {
                algorithm = "Ed25519 (FIDO2)";
                keySize = Ed25519KeySize;
            }
            else if (blobAlgorithm == OpenSshSkEcdsaNistp256Prefix)
            {
                algorithm = "ECDSA (FIDO2)";
                var curve = ReadOpenSshString(ms);
                keySize = curve == "nistp256" ? 256 : 0;
                if (keySize == 0)
                {
                    return null;
                }
            }
            else if (blobAlgorithm.StartsWith("ecdsa-sha2-", StringComparison.Ordinal))
            {
                algorithm = "ECDSA";
                var curve = ReadOpenSshString(ms);
                keySize = curve switch
                {
                    "nistp256" => 256,
                    "nistp384" => 384,
                    "nistp521" => 521,
                    _ => 0
                };

                if (keySize == 0)
                {
                    return null;
                }
            }
            else
            {
                return null;
            }

            var fingerprint = ComputeSha256Fingerprint(publicKeyBlob);
            var findings = BuildFindings(algorithm, keySize, isPrivate, isEncrypted, isOldFormat, localize);
            var rating = DetermineRating(algorithm, keySize);

            return new SshKeyAuditResult
            {
                Algorithm = algorithm,
                KeySize = keySize,
                Fingerprint = fingerprint,
                Format = format,
                IsPrivateKey = isPrivate,
                IsEncrypted = isEncrypted,
                Rating = rating,
                Findings = findings
            };
        }
        catch (Exception ex) when (IsExpectedParseException(ex))
        {
            return null;
        }
    }

    private static SshKeyAuditResult? ParseOpenSshPrivateKey(string pem, Func<string, string>? localize)
    {
        try
        {
            var base64 = ExtractPemBase64(pem, "OPENSSH PRIVATE KEY");
            if (base64 is null)
            {
                return null;
            }

            var data = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(data);

            var magic = new byte[15];
            if (ms.Read(magic, 0, 15) != 15)
            {
                return null;
            }

            if (Encoding.ASCII.GetString(magic) != "openssh-key-v1\0")
            {
                return null;
            }

            var cipherName = ReadOpenSshString(ms);
            var kdfName = ReadOpenSshString(ms);
            var kdfOptions = ReadOpenSshBytes(ms);
            _ = kdfOptions;

            var numKeysBuf = new byte[4];
            if (ms.Read(numKeysBuf, 0, 4) != 4)
            {
                return null;
            }

            var numKeys = BinaryPrimitives.ReadInt32BigEndian(numKeysBuf);
            if (numKeys < 1)
            {
                return null;
            }

            var pubKeyBlob = ReadOpenSshBytes(ms);
            if (pubKeyBlob is null)
            {
                return null;
            }

            var isEncrypted = cipherName != "none" || kdfName == OpenSshKdfBcrypt;

            using var pubMs = new MemoryStream(pubKeyBlob);
            var algorithmTag = ReadOpenSshString(pubMs);
            if (algorithmTag is null)
            {
                return null;
            }

            return ParseOpenSshBlob(pubKeyBlob, algorithmTag, isPrivate: true, isEncrypted, "OpenSSH", localize);
        }
        catch (Exception ex) when (IsExpectedParseException(ex))
        {
            return null;
        }
    }

    private static SshKeyAuditResult? ParsePkcs1RsaPrivateKey(string pem, Func<string, string>? localize)
    {
        var isEncrypted = pem.Contains(ProcTypeEncrypted, StringComparison.Ordinal);
        var isOldFormat = true;

        if (isEncrypted)
        {
            return new SshKeyAuditResult
            {
                Algorithm = "RSA",
                KeySize = 0,
                Fingerprint = string.Empty,
                Format = "PEM (PKCS#1)",
                IsPrivateKey = true,
                IsEncrypted = true,
                Rating = SecurityRating.Acceptable,
                Findings = BuildFindings("RSA", 0, true, true, isOldFormat, localize)
            };
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            var keySize = rsa.KeySize;
            var publicKeyBlob = BuildRsaPublicKeyBlob(rsa);
            var fingerprint = ComputeSha256Fingerprint(publicKeyBlob);
            var rating = DetermineRating("RSA", keySize);

            return new SshKeyAuditResult
            {
                Algorithm = "RSA",
                KeySize = keySize,
                Fingerprint = fingerprint,
                Format = "PEM (PKCS#1)",
                IsPrivateKey = true,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("RSA", keySize, true, false, isOldFormat, localize)
            };
        }
        catch (Exception ex) when (IsExpectedParseException(ex))
        {
            return null;
        }
    }

    private static SshKeyAuditResult? ParseLegacyDsaPrivateKey(string pem, Func<string, string>? localize)
    {
        var isEncrypted = pem.Contains(ProcTypeEncrypted, StringComparison.Ordinal);

        if (isEncrypted)
        {
            return new SshKeyAuditResult
            {
                Algorithm = "DSA",
                KeySize = 0,
                Fingerprint = string.Empty,
                Format = "PEM (Legacy DSA)",
                IsPrivateKey = true,
                IsEncrypted = true,
                Rating = SecurityRating.Deprecated,
                Findings = BuildFindings("DSA", 0, true, true, true, localize)
            };
        }

        try
        {
            using var dsa = DSA.Create();
            dsa.ImportFromPem(pem);
            var keySize = dsa.KeySize;
            var rating = DetermineRating("DSA", keySize);

            return new SshKeyAuditResult
            {
                Algorithm = "DSA",
                KeySize = keySize,
                Fingerprint = string.Empty,
                Format = "PEM (Legacy DSA)",
                IsPrivateKey = true,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("DSA", keySize, true, false, true, localize)
            };
        }
        catch (Exception ex) when (IsExpectedParseException(ex))
        {
            return null;
        }
    }

    private static SshKeyAuditResult? ParseLegacyEcPrivateKey(string pem, Func<string, string>? localize)
    {
        var isEncrypted = pem.Contains(ProcTypeEncrypted, StringComparison.Ordinal);

        if (isEncrypted)
        {
            return new SshKeyAuditResult
            {
                Algorithm = "ECDSA",
                KeySize = 0,
                Fingerprint = string.Empty,
                Format = "PEM (Legacy EC)",
                IsPrivateKey = true,
                IsEncrypted = true,
                Rating = SecurityRating.Acceptable,
                Findings = BuildFindings("ECDSA", 0, true, true, true, localize)
            };
        }

        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            var keySize = ec.KeySize;
            var rating = DetermineRating("ECDSA", keySize);

            var parameters = ec.ExportParameters(false);
            var curveName = keySize switch
            {
                256 => OpenSshEcdsaNistp256Prefix,
                384 => OpenSshEcdsaNistp384Prefix,
                521 => OpenSshEcdsaNistp521Prefix,
                _ => null
            };

            var fingerprint = string.Empty;
            if (curveName is not null && parameters.Q.X is not null && parameters.Q.Y is not null)
            {
                var blob = BuildEcdsaPublicKeyBlob(curveName, keySize, parameters.Q.X, parameters.Q.Y);
                fingerprint = ComputeSha256Fingerprint(blob);
            }

            return new SshKeyAuditResult
            {
                Algorithm = "ECDSA",
                KeySize = keySize,
                Fingerprint = fingerprint,
                Format = "PEM (Legacy EC)",
                IsPrivateKey = true,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("ECDSA", keySize, true, false, true, localize)
            };
        }
        catch (Exception ex) when (IsExpectedParseException(ex))
        {
            return null;
        }
    }

    private static SshKeyAuditResult? ParsePkcs8PrivateKey(string pem, bool isEncrypted, Func<string, string>? localize)
    {
        if (isEncrypted)
        {
            return new SshKeyAuditResult
            {
                Algorithm = "Unknown",
                KeySize = 0,
                Fingerprint = string.Empty,
                Format = "PEM (PKCS#8, encrypted)",
                IsPrivateKey = true,
                IsEncrypted = true,
                Rating = SecurityRating.Acceptable,
                Findings = BuildFindings("Unknown", 0, true, true, false, localize)
            };
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            var keySize = rsa.KeySize;
            var publicKeyBlob = BuildRsaPublicKeyBlob(rsa);
            var fingerprint = ComputeSha256Fingerprint(publicKeyBlob);
            var rating = DetermineRating("RSA", keySize);

            return new SshKeyAuditResult
            {
                Algorithm = "RSA",
                KeySize = keySize,
                Fingerprint = fingerprint,
                Format = "PEM (PKCS#8)",
                IsPrivateKey = true,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("RSA", keySize, true, false, false, localize)
            };
        }
        catch (CryptographicException)
        {
            // Not RSA.
        }

        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            var keySize = ec.KeySize;
            var rating = DetermineRating("ECDSA", keySize);

            var parameters = ec.ExportParameters(false);
            var curveName = keySize switch
            {
                256 => OpenSshEcdsaNistp256Prefix,
                384 => OpenSshEcdsaNistp384Prefix,
                521 => OpenSshEcdsaNistp521Prefix,
                _ => null
            };

            var fingerprint = string.Empty;
            if (curveName is not null && parameters.Q.X is not null && parameters.Q.Y is not null)
            {
                var blob = BuildEcdsaPublicKeyBlob(curveName, keySize, parameters.Q.X, parameters.Q.Y);
                fingerprint = ComputeSha256Fingerprint(blob);
            }

            return new SshKeyAuditResult
            {
                Algorithm = "ECDSA",
                KeySize = keySize,
                Fingerprint = fingerprint,
                Format = "PEM (PKCS#8)",
                IsPrivateKey = true,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("ECDSA", keySize, true, false, false, localize)
            };
        }
        catch (CryptographicException)
        {
            // Not ECDSA.
        }

        try
        {
            using var dsa = DSA.Create();
            dsa.ImportFromPem(pem);
            var keySize = dsa.KeySize;
            var rating = DetermineRating("DSA", keySize);

            return new SshKeyAuditResult
            {
                Algorithm = "DSA",
                KeySize = keySize,
                Fingerprint = string.Empty,
                Format = "PEM (PKCS#8)",
                IsPrivateKey = true,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("DSA", keySize, true, false, false, localize)
            };
        }
        catch (CryptographicException)
        {
            // Not DSA.
        }

        return TryParseEd25519Pkcs8(pem, localize);
    }

    private static SshKeyAuditResult? TryParseEd25519Pkcs8(string pem, Func<string, string>? localize)
    {
        try
        {
            var base64 = ExtractPemBase64(pem, "PRIVATE KEY");
            if (base64 is null)
            {
                return null;
            }

            var derBytes = Convert.FromBase64String(base64);
            var ed25519Oid = new byte[] { 0x06, 0x03, 0x2B, 0x65, 0x70 };
            if (!ContainsSequence(derBytes, ed25519Oid))
            {
                return null;
            }

            var rating = DetermineRating("Ed25519", Ed25519KeySize);

            return new SshKeyAuditResult
            {
                Algorithm = "Ed25519",
                KeySize = Ed25519KeySize,
                Fingerprint = string.Empty,
                Format = "PEM (PKCS#8)",
                IsPrivateKey = true,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("Ed25519", Ed25519KeySize, true, false, false, localize)
            };
        }
        catch (Exception ex) when (IsExpectedParseException(ex))
        {
            return null;
        }
    }

    private static SshKeyAuditResult? ParseSpkiPublicKey(string pem, Func<string, string>? localize)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            var keySize = rsa.KeySize;
            var publicKeyBlob = BuildRsaPublicKeyBlob(rsa);
            var fingerprint = ComputeSha256Fingerprint(publicKeyBlob);
            var rating = DetermineRating("RSA", keySize);

            return new SshKeyAuditResult
            {
                Algorithm = "RSA",
                KeySize = keySize,
                Fingerprint = fingerprint,
                Format = "PEM (SPKI)",
                IsPrivateKey = false,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("RSA", keySize, false, false, false, localize)
            };
        }
        catch (CryptographicException)
        {
            // Not RSA.
        }

        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            var keySize = ec.KeySize;
            var rating = DetermineRating("ECDSA", keySize);

            var parameters = ec.ExportParameters(false);
            var curveName = keySize switch
            {
                256 => OpenSshEcdsaNistp256Prefix,
                384 => OpenSshEcdsaNistp384Prefix,
                521 => OpenSshEcdsaNistp521Prefix,
                _ => null
            };

            var fingerprint = string.Empty;
            if (curveName is not null && parameters.Q.X is not null && parameters.Q.Y is not null)
            {
                var blob = BuildEcdsaPublicKeyBlob(curveName, keySize, parameters.Q.X, parameters.Q.Y);
                fingerprint = ComputeSha256Fingerprint(blob);
            }

            return new SshKeyAuditResult
            {
                Algorithm = "ECDSA",
                KeySize = keySize,
                Fingerprint = fingerprint,
                Format = "PEM (SPKI)",
                IsPrivateKey = false,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("ECDSA", keySize, false, false, false, localize)
            };
        }
        catch (CryptographicException)
        {
            // Not ECDSA.
        }

        try
        {
            var base64 = ExtractPemBase64(pem, "PUBLIC KEY");
            if (base64 is null)
            {
                return null;
            }

            var derBytes = Convert.FromBase64String(base64);
            var ed25519Oid = new byte[] { 0x06, 0x03, 0x2B, 0x65, 0x70 };
            if (!ContainsSequence(derBytes, ed25519Oid))
            {
                return null;
            }

            var rating = DetermineRating("Ed25519", Ed25519KeySize);

            return new SshKeyAuditResult
            {
                Algorithm = "Ed25519",
                KeySize = Ed25519KeySize,
                Fingerprint = string.Empty,
                Format = "PEM (SPKI)",
                IsPrivateKey = false,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("Ed25519", Ed25519KeySize, false, false, false, localize)
            };
        }
        catch (Exception ex) when (IsExpectedParseException(ex))
        {
            // Not Ed25519.
        }

        try
        {
            using var dsa = DSA.Create();
            dsa.ImportFromPem(pem);
            var keySize = dsa.KeySize;
            var rating = DetermineRating("DSA", keySize);

            return new SshKeyAuditResult
            {
                Algorithm = "DSA",
                KeySize = keySize,
                Fingerprint = string.Empty,
                Format = "PEM (SPKI)",
                IsPrivateKey = false,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("DSA", keySize, false, false, false, localize)
            };
        }
        catch (CryptographicException)
        {
            // Not DSA.
        }

        return null;
    }

    private static SshKeyAuditResult? ParsePkcs1RsaPublicKey(string pem, Func<string, string>? localize)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            var keySize = rsa.KeySize;
            var publicKeyBlob = BuildRsaPublicKeyBlob(rsa);
            var fingerprint = ComputeSha256Fingerprint(publicKeyBlob);
            var rating = DetermineRating("RSA", keySize);

            return new SshKeyAuditResult
            {
                Algorithm = "RSA",
                KeySize = keySize,
                Fingerprint = fingerprint,
                Format = "PEM (PKCS#1)",
                IsPrivateKey = false,
                IsEncrypted = false,
                Rating = rating,
                Findings = BuildFindings("RSA", keySize, false, false, false, localize)
            };
        }
        catch (Exception ex) when (IsExpectedParseException(ex))
        {
            return null;
        }
    }

    internal static SecurityRating DetermineRating(string algorithm, int keySize) => algorithm switch
    {
        "DSA" => SecurityRating.Deprecated,
        "Ed25519" => SecurityRating.Strong,
        "Ed25519 (FIDO2)" => SecurityRating.Strong,
        "ECDSA (FIDO2)" => SecurityRating.Strong,
        "RSA" when keySize > 0 && keySize < MinRsaKeySize => SecurityRating.Weak,
        "RSA" when keySize >= StrongRsaKeySize => SecurityRating.Strong,
        "RSA" when keySize >= MinRsaKeySize => SecurityRating.Acceptable,
        "ECDSA" when keySize >= StrongEcdsaKeySize => SecurityRating.Strong,
        "ECDSA" when keySize > 0 => SecurityRating.Acceptable,
        "Unknown" => SecurityRating.Acceptable,
        _ => SecurityRating.Acceptable
    };

    internal static List<SshKeyAuditFinding> BuildFindings(
        string algorithm,
        int keySize,
        bool isPrivate,
        bool isEncrypted,
        bool isOldFormat,
        Func<string, string>? localize)
    {
        var findings = new List<SshKeyAuditFinding>();

        switch (algorithm)
        {
            case "Ed25519":
                findings.Add(new SshKeyAuditFinding
                {
                    Severity = FindingSeverity.Pass,
                    Text = L(localize, "ToolSshAuditFindingEd25519")
                });
                break;

            case "Ed25519 (FIDO2)":
            case "ECDSA (FIDO2)":
                findings.Add(new SshKeyAuditFinding
                {
                    Severity = FindingSeverity.Pass,
                    Text = L(localize, "ToolSshAuditFindingFido2")
                });
                break;

            case "RSA" when keySize >= StrongRsaKeySize:
                findings.Add(new SshKeyAuditFinding
                {
                    Severity = FindingSeverity.Pass,
                    Text = string.Format(L(localize, "ToolSshAuditFindingRsaStrong"), keySize)
                });
                break;

            case "RSA" when keySize >= MinRsaKeySize:
                findings.Add(new SshKeyAuditFinding
                {
                    Severity = FindingSeverity.Warning,
                    Text = L(localize, "ToolSshAuditFindingRsaOk")
                });
                break;

            case "RSA" when keySize > 0:
                findings.Add(new SshKeyAuditFinding
                {
                    Severity = FindingSeverity.Fail,
                    Text = string.Format(L(localize, "ToolSshAuditFindingRsaWeak"), keySize)
                });
                break;

            case "DSA":
                findings.Add(new SshKeyAuditFinding
                {
                    Severity = FindingSeverity.Fail,
                    Text = L(localize, "ToolSshAuditFindingDsa")
                });
                break;

            case "ECDSA" when keySize >= StrongEcdsaKeySize:
                findings.Add(new SshKeyAuditFinding
                {
                    Severity = FindingSeverity.Pass,
                    Text = string.Format(L(localize, "ToolSshAuditFindingEcdsaStrong"), keySize)
                });
                break;

            case "ECDSA" when keySize > 0:
                findings.Add(new SshKeyAuditFinding
                {
                    Severity = FindingSeverity.Warning,
                    Text = L(localize, "ToolSshAuditFindingEcdsaOk")
                });
                break;
        }

        if (isPrivate)
        {
            if (isEncrypted)
            {
                findings.Add(new SshKeyAuditFinding
                {
                    Severity = FindingSeverity.Pass,
                    Text = L(localize, "ToolSshAuditFindingEncrypted")
                });
            }
            else
            {
                findings.Add(new SshKeyAuditFinding
                {
                    Severity = FindingSeverity.Warning,
                    Text = L(localize, "ToolSshAuditFindingUnencrypted")
                });
            }
        }

        if (isOldFormat && isPrivate)
        {
            findings.Add(new SshKeyAuditFinding
            {
                Severity = FindingSeverity.Warning,
                Text = L(localize, "ToolSshAuditFindingOldFormat")
            });
        }

        return findings;
    }

    private static byte[] BuildRsaPublicKeyBlob(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        using var ms = new MemoryStream();
        WriteOpenSshString(ms, OpenSshRsaPrefix);
        WriteOpenSshMpint(ms, parameters.Exponent!);
        WriteOpenSshMpint(ms, parameters.Modulus!);
        return ms.ToArray();
    }

    private static byte[] BuildEcdsaPublicKeyBlob(string curveName, int keySize, byte[] x, byte[] y)
    {
        var curveId = keySize switch
        {
            256 => "nistp256",
            384 => "nistp384",
            521 => "nistp521",
            _ => "nistp256"
        };

        using var ms = new MemoryStream();
        WriteOpenSshString(ms, curveName);
        WriteOpenSshString(ms, curveId);

        var point = new byte[1 + x.Length + y.Length];
        point[0] = 0x04;
        Array.Copy(x, 0, point, 1, x.Length);
        Array.Copy(y, 0, point, 1 + x.Length, y.Length);
        WriteOpenSshBytes(ms, point);

        return ms.ToArray();
    }

    private static string ComputeSha256Fingerprint(byte[] publicKeyBlob)
    {
        var hash = SHA256.HashData(publicKeyBlob);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    private static string? ReadOpenSshString(Stream stream)
    {
        var bytes = ReadOpenSshBytes(stream);
        return bytes is null ? null : Encoding.ASCII.GetString(bytes);
    }

    private static byte[]? ReadOpenSshBytes(Stream stream)
    {
        Span<byte> lengthBuf = stackalloc byte[4];
        if (stream.Read(lengthBuf) != 4)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
        if (length < 0 || length > stream.Length - stream.Position)
        {
            return null;
        }

        var data = new byte[length];
        if (stream.Read(data) != length)
        {
            return null;
        }

        return data;
    }

    private static void WriteOpenSshString(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Span<byte> lengthBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuf, bytes.Length);
        stream.Write(lengthBuf);
        stream.Write(bytes);
    }

    private static void WriteOpenSshBytes(Stream stream, byte[] value)
    {
        Span<byte> lengthBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuf, value.Length);
        stream.Write(lengthBuf);
        stream.Write(value);
    }

    private static void WriteOpenSshMpint(Stream stream, byte[] value)
    {
        var needsPadding = value.Length > 0 && (value[0] & 0x80) != 0;
        var length = value.Length + (needsPadding ? 1 : 0);

        Span<byte> lengthBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuf, length);
        stream.Write(lengthBuf);

        if (needsPadding)
        {
            stream.WriteByte(0);
        }

        stream.Write(value);
    }

    private static int GetMpintBitLength(byte[] mpint)
    {
        var offset = 0;
        while (offset < mpint.Length && mpint[offset] == 0)
        {
            offset++;
        }

        if (offset >= mpint.Length)
        {
            return 0;
        }

        var byteCount = mpint.Length - offset;
        var topByte = mpint[offset];
        var topBits = 0;
        while (topByte > 0)
        {
            topBits++;
            topByte >>= 1;
        }

        return ((byteCount - 1) * 8) + topBits;
    }

    private static string? ExtractPemBase64(string pem, string label)
    {
        var beginMarker = $"-----BEGIN {label}-----";
        var endMarker = $"-----END {label}-----";

        var beginIdx = pem.IndexOf(beginMarker, StringComparison.Ordinal);
        if (beginIdx < 0)
        {
            return null;
        }

        var bodyStart = beginIdx + beginMarker.Length;
        var endIdx = pem.IndexOf(endMarker, bodyStart, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            return null;
        }

        var body = pem[bodyStart..endIdx];
        var sb = new StringBuilder(body.Length);
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            sb.Append(trimmed);
        }

        return sb.ToString();
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExpectedParseException(Exception ex) =>
        ex is FormatException or ArgumentException or CryptographicException or IOException or NotSupportedException;

    private static string L(Func<string, string>? localize, string key) =>
        localize?.Invoke(key) ?? key;
}
