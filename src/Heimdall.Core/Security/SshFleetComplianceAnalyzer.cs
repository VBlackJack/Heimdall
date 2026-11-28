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

using System.Globalization;
using System.IO;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Ssh;

namespace Heimdall.Core.Security;

/// <summary>Severity of an offline SSH fleet compliance finding.</summary>
public enum SshFleetFindingSeverity
{
    Critical,
    Warning,
    Info
}

/// <summary>Immutable snapshot of a pinned host key for offline fleet compliance analysis.</summary>
public sealed record SshFleetHostKeySnapshot(
    string Host,
    int Port,
    string Algorithm,
    string Fingerprint,
    bool IsRevoked);

/// <summary>Single compliance finding discovered in the local SSH fleet inventory.</summary>
public sealed record SshFleetFinding(
    SshFleetFindingSeverity Severity,
    string TargetName,
    string TargetKind,
    string Code,
    string Summary);

/// <summary>Offline SSH fleet compliance summary and findings.</summary>
public sealed record SshFleetComplianceReport(
    int ProfilesAudited,
    int GatewaysAudited,
    int HostKeysAudited,
    int ScorePercent,
    IReadOnlyList<SshFleetFinding> Findings);

/// <summary>
/// Pure offline compliance analyzer for saved local SSH profiles, gateways, local key/certificate
/// files, and pinned host keys. Performs zero network traffic and never decrypts passwords.
/// </summary>
public static class SshFleetComplianceAnalyzer
{
    private const int CertExpiringSoonDays = 30;
    private const long MaxKeyFileBytes = 256 * 1024;

    public static SshFleetComplianceReport Analyze(
        IReadOnlyList<ServerProfileDto> profiles,
        IReadOnlyList<SshGatewayDto> gateways,
        IReadOnlyList<SshFleetHostKeySnapshot> hostKeys,
        SshCryptoPolicy globalCryptoPolicy,
        Func<string, string?>? fileTextReader = null,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(gateways);
        ArgumentNullException.ThrowIfNull(hostKeys);

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var reader = fileTextReader ?? TryReadBoundedLocalFile;
        var findings = new List<SshFleetFinding>();

        var sshProfiles = profiles
            .Where(p => string.Equals(p.ConnectionType, "SSH", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.ConnectionType, "SFTP", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var profile in sshProfiles)
        {
            string label = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.RemoteServer : profile.DisplayName;
            SshCryptoPolicy effectivePolicy = profile.SshCryptoPolicy ?? globalCryptoPolicy;
            if (effectivePolicy == SshCryptoPolicy.LegacyCompatible)
            {
                findings.Add(new SshFleetFinding(
                    SshFleetFindingSeverity.Warning,
                    label,
                    "Profile",
                    "LEGACY_CRYPTO_POLICY",
                    "Profile allows legacy cryptographic algorithms (SHA-1 / CBC / ssh-rsa)."));
            }

            bool hasKey = !string.IsNullOrWhiteSpace(profile.SshKeyPath);
            bool hasAgentPin = !string.IsNullOrWhiteSpace(profile.SshAgentKeyFingerprint);
            bool hasPassword = !string.IsNullOrWhiteSpace(profile.SshPasswordEncrypted);

            if (!hasKey && !hasAgentPin && hasPassword)
            {
                findings.Add(new SshFleetFinding(
                    SshFleetFindingSeverity.Info,
                    label,
                    "Profile",
                    "PASSWORD_ONLY_AUTH",
                    "Profile authenticates with a stored password only (no SSH key or agent key configured)."));
            }

            if (hasKey)
            {
                AuditKeyFile(profile.SshKeyPath!, label, "Profile", reader, findings);
            }

            string? certPath = ResolveCertificatePath(profile.SshKeyPath, profile.SshCertificatePath, reader);
            if (!string.IsNullOrWhiteSpace(certPath))
            {
                AuditCertificateFile(certPath, label, "Profile", reader, now, findings);
            }
        }

        foreach (var gw in gateways)
        {
            string label = string.IsNullOrWhiteSpace(gw.Name) ? gw.Host : gw.Name;
            if (!string.IsNullOrWhiteSpace(gw.KeyPath))
            {
                AuditKeyFile(gw.KeyPath, label, "Gateway", reader, findings);
            }
            else if (string.IsNullOrWhiteSpace(gw.SshAgentKeyFingerprint) && !string.IsNullOrWhiteSpace(gw.SshPasswordEncrypted))
            {
                findings.Add(new SshFleetFinding(
                    SshFleetFindingSeverity.Info,
                    label,
                    "Gateway",
                    "PASSWORD_ONLY_AUTH",
                    "Gateway uses password-only authentication (prefer key or agent authentication)."));
            }
        }

        foreach (var hk in hostKeys)
        {
            string endpoint = $"{hk.Host}:{hk.Port}";
            if (hk.IsRevoked)
            {
                findings.Add(new SshFleetFinding(
                    SshFleetFindingSeverity.Critical,
                    endpoint,
                    "HostKey",
                    "HOSTKEY_REVOKED",
                    $"Pinned host key ({hk.Algorithm}) matches a local @revoked rule."));
            }
            else if (IsWeakHostKeyAlgorithm(hk.Algorithm))
            {
                findings.Add(new SshFleetFinding(
                    SshFleetFindingSeverity.Warning,
                    endpoint,
                    "HostKey",
                    "HOSTKEY_WEAK_ALGO",
                    $"Pinned host key uses legacy algorithm {hk.Algorithm} (prefer ssh-ed25519 or rsa-sha2-*)."));
            }
        }

        int penalty = findings.Sum(f => f.Severity switch
        {
            SshFleetFindingSeverity.Critical => 20,
            SshFleetFindingSeverity.Warning => 8,
            _ => 2
        });
        int totalItems = sshProfiles.Count + gateways.Count + hostKeys.Count;
        int score = totalItems == 0 ? 100 : Math.Clamp(100 - penalty, 0, 100);

        return new SshFleetComplianceReport(
            sshProfiles.Count,
            gateways.Count,
            hostKeys.Count,
            score,
            findings);
    }

    public static string FormatCsvReport(SshFleetComplianceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("Severity,TargetKind,Target,Code,Summary");
        foreach (var f in report.Findings)
        {
            sb.AppendLine(string.Join(",",
                SshServerAuditEngine.SanitizeCsvCell(f.Severity.ToString()),
                SshServerAuditEngine.SanitizeCsvCell(f.TargetKind),
                SshServerAuditEngine.SanitizeCsvCell(f.TargetName),
                SshServerAuditEngine.SanitizeCsvCell(f.Code),
                SshServerAuditEngine.SanitizeCsvCell(f.Summary)));
        }

        return sb.ToString();
    }

    private static void AuditKeyFile(
        string keyPath,
        string label,
        string targetKind,
        Func<string, string?> reader,
        List<SshFleetFinding> findings)
    {
        string? content = reader(keyPath);
        if (string.IsNullOrWhiteSpace(content))
        {
            findings.Add(new SshFleetFinding(
                SshFleetFindingSeverity.Warning,
                label,
                targetKind,
                "KEY_FILE_MISSING",
                "Configured SSH key file is missing or unreadable."));
            return;
        }

        var audit = SshKeyAuditEngine.Audit(content);
        if (audit is null)
        {
            return;
        }

        if (audit.Rating is SecurityRating.Weak or SecurityRating.Deprecated)
        {
            findings.Add(new SshFleetFinding(
                SshFleetFindingSeverity.Critical,
                label,
                targetKind,
                "WEAK_KEY_ALGORITHM",
                $"Configured SSH key uses weak algorithm or key size ({audit.Algorithm}, {audit.KeySize} bits)."));
        }

        if (audit.IsPrivateKey && !audit.IsEncrypted)
        {
            findings.Add(new SshFleetFinding(
                SshFleetFindingSeverity.Warning,
                label,
                targetKind,
                "UNENCRYPTED_PRIVATE_KEY",
                "Private SSH key file is stored without a passphrase."));
        }
    }

    private static string? ResolveCertificatePath(
        string? keyPath,
        string? explicitCertPath,
        Func<string, string?> reader)
    {
        if (!string.IsNullOrWhiteSpace(explicitCertPath))
        {
            return explicitCertPath;
        }

        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return null;
        }

        string sibling = keyPath + "-cert.pub";
        return reader(sibling) is not null ? sibling : null;
    }

    private static void AuditCertificateFile(
        string certPath,
        string label,
        string targetKind,
        Func<string, string?> reader,
        DateTimeOffset nowUtc,
        List<SshFleetFinding> findings)
    {
        string? content = reader(certPath);
        if (string.IsNullOrWhiteSpace(content))
        {
            findings.Add(new SshFleetFinding(
                SshFleetFindingSeverity.Warning,
                label,
                targetKind,
                "CERT_FILE_MISSING",
                "Configured OpenSSH user certificate file is missing or unreadable."));
            return;
        }

        if (!OpenSshCertificateParser.TryParsePublicLine(content, out var cert) || cert is null)
        {
            findings.Add(new SshFleetFinding(
                SshFleetFindingSeverity.Warning,
                label,
                targetKind,
                "CERT_INVALID",
                "OpenSSH user certificate file could not be parsed."));
            return;
        }

        if (cert.ValidBefore is { } validBefore)
        {
            if (validBefore < nowUtc)
            {
                findings.Add(new SshFleetFinding(
                    SshFleetFindingSeverity.Critical,
                    label,
                    targetKind,
                    "CERT_EXPIRED",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "OpenSSH user certificate expired on {0:yyyy-MM-dd}.",
                        validBefore)));
            }
            else if (validBefore <= nowUtc.AddDays(CertExpiringSoonDays))
            {
                int daysLeft = Math.Max(0, (int)Math.Ceiling((validBefore - nowUtc).TotalDays));
                findings.Add(new SshFleetFinding(
                    SshFleetFindingSeverity.Warning,
                    label,
                    targetKind,
                    "CERT_EXPIRING_SOON",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "OpenSSH user certificate expires in {0} day(s) ({1:yyyy-MM-dd}).",
                        daysLeft,
                        validBefore)));
            }
        }
    }

    private static bool IsWeakHostKeyAlgorithm(string? algorithm) =>
        string.Equals(algorithm, "ssh-rsa", StringComparison.OrdinalIgnoreCase)
        || string.Equals(algorithm, "ssh-dss", StringComparison.OrdinalIgnoreCase)
        || string.Equals(algorithm, "ssh-rsa-cert-v01@openssh.com", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadBoundedLocalFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxKeyFileBytes)
            {
                return null;
            }

            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
