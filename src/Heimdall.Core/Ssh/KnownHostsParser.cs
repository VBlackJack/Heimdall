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

using System.Net;

namespace Heimdall.Core.Ssh;

/// <summary>
/// Pure parser for clear OpenSSH known_hosts entries.
/// </summary>
public static class KnownHostsParser
{
    /// <summary>
    /// Hard cap on the length of a single known_hosts line.
    /// Lines longer than this are rejected with a <see cref="KnownHostsDiagnosticCode.MalformedLine"/>
    /// diagnostic, defending against malicious or corrupted files attempting
    /// to exhaust memory with one giant line.
    /// </summary>
    public const int MaxLineLength = 65_536;

    /// <summary>
    /// Hard cap on the size in bytes of a known_hosts file accepted by the
    /// streaming importer. Files larger than this are rejected entirely.
    /// </summary>
    public const long MaxFileSizeBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Diagnostic context value used when a single line exceeds <see cref="MaxLineLength"/>.
    /// </summary>
    public const string LineTooLongContext = "line too long";

    /// <summary>
    /// Set of host key algorithms recognized by the importer. Kept internal
    /// to prevent ad-hoc mutation; consumers in other assemblies use
    /// <see cref="IsSupportedKeyType"/> to validate against the same allow-list.
    /// </summary>
    private static readonly IReadOnlySet<string> SupportedKeyTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "ssh-rsa",
        "ssh-dss",
        "sk-ecdsa-sha2-nistp256@openssh.com",
        "sk-ssh-ed25519@openssh.com"
    };

    /// <summary>
    /// Canonicalizes an SSH host key algorithm name to its underlying key type.
    /// The RFC 8332 RSA SHA-2 signature algorithm names (<c>rsa-sha2-256</c>,
    /// <c>rsa-sha2-512</c>) are negotiated over an <c>ssh-rsa</c> key and are
    /// mapped to it. A null value yields an empty string; any other value is
    /// returned unchanged.
    /// </summary>
    public static string CanonicalizeKeyType(string? algorithm)
    {
        return algorithm switch
        {
            "rsa-sha2-256" or "rsa-sha2-512" => "ssh-rsa",
            null => string.Empty,
            _ => algorithm,
        };
    }

    /// <summary>
    /// Returns whether the supplied SSH host key algorithm name is recognized.
    /// RFC 8332 RSA SHA-2 signature algorithm names are accepted as their
    /// underlying <c>ssh-rsa</c> key type. Used by trust services to flag
    /// entries with unrecognized algorithms.
    /// </summary>
    public static bool IsSupportedKeyType(string? algorithm)
    {
        return !string.IsNullOrWhiteSpace(algorithm)
            && SupportedKeyTypes.Contains(CanonicalizeKeyType(algorithm));
    }

    public static KnownHostsParseResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return Parse(content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n'));
    }

    /// <summary>
    /// Extracts <c>@cert-authority</c> and <c>@revoked</c> policy rules from a <c>known_hosts</c> document.
    /// </summary>
    public static KnownHostsPolicyEntries ParsePolicyEntries(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var certAuthorities = new List<KnownHostsCertAuthorityEntry>();
        var revokedKeys = new List<KnownHostsRevokedEntry>();

        using var reader = new StringReader(content);
        string? rawLine;
        var lineNumber = 0;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (rawLine.Length > MaxLineLength)
            {
                continue;
            }

            var trimmed = rawLine.TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var isCa = trimmed.StartsWith("@cert-authority ", StringComparison.Ordinal)
                || trimmed.StartsWith("@cert-authority\t", StringComparison.Ordinal);
            var isRevoked = !isCa && (trimmed.StartsWith("@revoked ", StringComparison.Ordinal)
                || trimmed.StartsWith("@revoked\t", StringComparison.Ordinal));
            if (!isCa && !isRevoked)
            {
                continue;
            }

            var fields = trimmed.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4)
            {
                continue;
            }

            var hostPatterns = fields[1];
            var keyType = fields[2];
            var base64 = fields[3];

            if (!SupportedKeyTypes.Contains(keyType))
            {
                continue;
            }

            byte[] keyBlob;
            try
            {
                keyBlob = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                continue;
            }

            if (keyBlob.Length < 16)
            {
                continue;
            }

            var fingerprint = HostKeyFormats.ComputeSha256Fingerprint(keyBlob);
            var canonicalType = CanonicalizeKeyType(keyType);
            if (isCa)
            {
                certAuthorities.Add(new KnownHostsCertAuthorityEntry(
                    hostPatterns,
                    canonicalType,
                    fingerprint,
                    keyBlob,
                    lineNumber));
            }
            else
            {
                revokedKeys.Add(new KnownHostsRevokedEntry(
                    hostPatterns,
                    canonicalType,
                    fingerprint,
                    lineNumber));
            }
        }

        return new KnownHostsPolicyEntries(certAuthorities, revokedKeys);
    }

    /// <summary>
    /// Stream-parse known_hosts from a <see cref="TextReader"/>, line by line.
    /// Use this overload when reading from a file to avoid allocating the
    /// whole content as a managed string.
    /// </summary>
    public static KnownHostsParseResult Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Parse(EnumerateLines(reader));
    }

    private static IEnumerable<string> EnumerateLines(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    public static KnownHostsParseResult Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var entries = new List<KnownHostsRawEntry>();
        var diagnostics = new List<KnownHostsImportDiagnostic>();

        var lineNumber = 0;
        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine ?? string.Empty;

            if (line.Length > MaxLineLength)
            {
                diagnostics.Add(new KnownHostsImportDiagnostic(
                    KnownHostsDiagnosticLevel.Warning,
                    lineNumber,
                    KnownHostsDiagnosticCode.MalformedLine,
                    LineTooLongContext));
                continue;
            }

            var trimmedStart = line.TrimStart();
            if (string.IsNullOrWhiteSpace(trimmedStart) || trimmedStart.StartsWith('#'))
            {
                continue;
            }

            if (trimmedStart.StartsWith("@cert-authority ", StringComparison.Ordinal) ||
                trimmedStart.StartsWith("@cert-authority\t", StringComparison.Ordinal))
            {
                diagnostics.Add(new KnownHostsImportDiagnostic(
                    KnownHostsDiagnosticLevel.Info,
                    lineNumber,
                    KnownHostsDiagnosticCode.CertAuthorityNotSupported));
                continue;
            }

            if (trimmedStart.StartsWith("@revoked ", StringComparison.Ordinal) ||
                trimmedStart.StartsWith("@revoked\t", StringComparison.Ordinal))
            {
                diagnostics.Add(new KnownHostsImportDiagnostic(
                    KnownHostsDiagnosticLevel.Info,
                    lineNumber,
                    KnownHostsDiagnosticCode.RevokedEntryNotSupported));
                continue;
            }

            var fields = trimmedStart.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3)
            {
                diagnostics.Add(new KnownHostsImportDiagnostic(
                    KnownHostsDiagnosticLevel.Warning,
                    lineNumber,
                    KnownHostsDiagnosticCode.MalformedLine,
                    $"{fields.Length} fields"));
                continue;
            }

            var hostsField = fields[0];
            var keyType = fields[1];
            var base64 = fields[2];

            if (!SupportedKeyTypes.Contains(keyType))
            {
                diagnostics.Add(new KnownHostsImportDiagnostic(
                    KnownHostsDiagnosticLevel.Info,
                    lineNumber,
                    KnownHostsDiagnosticCode.UnsupportedKeyType,
                    keyType));
                continue;
            }

            if (string.Equals(keyType, "ssh-dss", StringComparison.Ordinal))
            {
                diagnostics.Add(new KnownHostsImportDiagnostic(
                    KnownHostsDiagnosticLevel.Warning,
                    lineNumber,
                    KnownHostsDiagnosticCode.LegacyKeyType,
                    keyType));
            }

            byte[] rawKeyBlob;
            try
            {
                rawKeyBlob = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                diagnostics.Add(new KnownHostsImportDiagnostic(
                    KnownHostsDiagnosticLevel.Warning,
                    lineNumber,
                    KnownHostsDiagnosticCode.MalformedLine,
                    "bad base64"));
                continue;
            }

            foreach (var token in hostsField.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (token.StartsWith("|1|", StringComparison.Ordinal))
                {
                    if (!KnownHostsHash.TryParse(token, out _, out _))
                    {
                        diagnostics.Add(new KnownHostsImportDiagnostic(
                            KnownHostsDiagnosticLevel.Warning,
                            lineNumber,
                            KnownHostsDiagnosticCode.MalformedLine,
                            "bad hashed host"));
                        continue;
                    }

                    entries.Add(new KnownHostsRawEntry
                    {
                        Host = token,
                        Port = 22,
                        IsHashedHost = true,
                        KeyType = keyType,
                        Base64Key = rawKeyBlob,
                        SourceLineNumber = lineNumber
                    });
                    continue;
                }

                if (token.IndexOfAny(['*', '?', '!', '%']) >= 0)
                {
                    diagnostics.Add(new KnownHostsImportDiagnostic(
                        KnownHostsDiagnosticLevel.Info,
                        lineNumber,
                        KnownHostsDiagnosticCode.UnsupportedHostPattern,
                        token));
                    continue;
                }

                if (!TryParseHostToken(token, out var host, out var port, out var context))
                {
                    diagnostics.Add(new KnownHostsImportDiagnostic(
                        KnownHostsDiagnosticLevel.Info,
                        lineNumber,
                        KnownHostsDiagnosticCode.UnsupportedHostPattern,
                        context));
                    continue;
                }

                entries.Add(new KnownHostsRawEntry
                {
                    Host = host,
                    Port = port,
                    IsHashedHost = false,
                    KeyType = keyType,
                    Base64Key = rawKeyBlob,
                    SourceLineNumber = lineNumber
                });
            }
        }

        return new KnownHostsParseResult(entries, diagnostics);
    }

    private static bool TryParseHostToken(
        string token,
        out string host,
        out int port,
        out string context)
    {
        host = string.Empty;
        port = 22;
        context = token;

        if (token.StartsWith("[", StringComparison.Ordinal))
        {
            var closing = token.IndexOf(']');
            if (closing <= 0)
            {
                return false;
            }

            host = token[1..closing];
            if (host.Length == 0 || host.Contains("%", StringComparison.Ordinal))
            {
                context = token;
                return false;
            }

            if (closing == token.Length - 1)
            {
                return true;
            }

            if (token[closing + 1] != ':')
            {
                context = token;
                return false;
            }

            var portToken = token[(closing + 2)..];
            if (int.TryParse(portToken, out port) && port is >= 1 and <= 65535)
            {
                return true;
            }

            context = token;
            return false;
        }

        var colonCount = token.Count(ch => ch == ':');
        if (colonCount >= 2)
        {
            if (!IPAddress.TryParse(token, out _))
            {
                context = "invalid multi-colon host token";
                return false;
            }

            host = token;
            port = 22;
            return true;
        }

        if (colonCount == 1)
        {
            context = "bare colon non-IPv6";
            return false;
        }

        host = token;
        port = 22;
        return true;
    }
}
