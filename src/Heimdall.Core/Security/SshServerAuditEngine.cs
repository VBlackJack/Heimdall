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
using System.Net;
using System.Text;
using System.Text.Json;

namespace Heimdall.Core.Security;

/// <summary>Security rating assigned to an advertised SSH algorithm.</summary>
public enum SshAlgorithmRating
{
    PostQuantum,
    Strong,
    Acceptable,
    Weak,
    Insecure
}

/// <summary>Category of an SSH algorithm advertised in <c>SSH_MSG_KEXINIT</c>.</summary>
public enum SshAlgorithmCategory
{
    KeyExchange,
    HostKey,
    Encryption,
    Mac,
    Compression
}

/// <summary>Severity of an SSH server audit finding.</summary>
public enum SshAuditFindingSeverity
{
    Pass,
    Info,
    Warning,
    Critical
}

/// <summary>Overall letter grade for an audited SSH server.</summary>
public enum SshAuditGrade
{
    APlus,
    A,
    B,
    C,
    D,
    F
}

/// <summary>Raw algorithm name-lists extracted from an SSH server's <c>SSH_MSG_KEXINIT</c>.</summary>
public sealed record SshKexInitSnapshot(
    string Host,
    int Port,
    string ServerBanner,
    IReadOnlyList<string> KeyExchange,
    IReadOnlyList<string> HostKeys,
    IReadOnlyList<string> EncryptionC2S,
    IReadOnlyList<string> EncryptionS2C,
    IReadOnlyList<string> MacC2S,
    IReadOnlyList<string> MacS2C,
    IReadOnlyList<string> CompressionC2S,
    IReadOnlyList<string> CompressionS2C);

/// <summary>Security assessment of one advertised SSH algorithm.</summary>
public sealed record SshAlgorithmAssessment(
    string Name,
    SshAlgorithmCategory Category,
    SshAlgorithmRating Rating,
    string? Note = null);

/// <summary>Actionable finding produced during an SSH server audit.</summary>
public sealed record SshAuditFinding(
    SshAuditFindingSeverity Severity,
    string Code,
    string Message);

/// <summary>Non-sensitive snapshot persisted between fleet SSH audits to detect compliance drift.</summary>
public sealed record SshFleetAuditBaselineEntry(
    string Endpoint,
    string Grade,
    bool IsAnssiCompliant,
    bool HasPostQuantumKex,
    bool HasStrictKex,
    DateTimeOffset AuditedAtUtc);

/// <summary>Per-host comparison status between the current fleet audit and the previous baseline.</summary>
public enum SshFleetAuditDriftStatus
{
    New = 0,
    Unchanged = 1,
    Improved = 2,
    Regressed = 3,
    Error = 4
}

/// <summary>Summary of compliance drift between a current fleet audit and a previous baseline.</summary>
public sealed record SshFleetAuditDriftSummary(
    int Improved,
    int Regressed,
    int Unchanged,
    int NewHosts,
    IReadOnlyDictionary<string, SshFleetAuditDriftStatus> ByEndpoint);

/// <summary>Complete SSH server cryptographic audit report.</summary>
public sealed record SshServerAuditReport(
    SshKexInitSnapshot Snapshot,
    SshAuditGrade Grade,
    bool HasPostQuantumKex,
    bool HasStrictKex,
    bool IsAnssiCompliant,
    IReadOnlyList<SshAlgorithmAssessment> Algorithms,
    IReadOnlyList<SshAuditFinding> Findings,
    string RecommendedSshdConfig);

/// <summary>Outcome of auditing one saved SSH server during a batch fleet cryptographic scan.</summary>
public sealed record SshFleetServerAuditEntry(
    string DisplayName,
    string Host,
    int Port,
    string? GatewayName,
    SshServerAuditReport? Report,
    string? ErrorMessage = null);

/// <summary>
/// Evaluates an SSH server's advertised <c>SSH_MSG_KEXINIT</c> algorithms against modern
/// cryptographic standards (ANSSI / SecNumCloud / post-quantum readiness / Terrapin CVE-2023-48795)
/// and formats text, HTML, and CSV reports.
/// </summary>
public static class SshServerAuditEngine
{
    private const string StrictKexServerExtension = "kex-strict-s-v00@openssh.com";

    private static readonly HashSet<string> PostQuantumKexAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "mlkem768x25519-sha256",
        "sntrup761x25519-sha512",
        "sntrup761x25519-sha512@openssh.com"
    };

    private static readonly HashSet<string> StrongKexAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "curve25519-sha256",
        "curve25519-sha256@libssh.org",
        "diffie-hellman-group16-sha512",
        "diffie-hellman-group18-sha512",
        "diffie-hellman-group-exchange-sha256",
        "ecdh-sha2-nistp256",
        "ecdh-sha2-nistp384",
        "ecdh-sha2-nistp521"
    };

    private static readonly HashSet<string> InsecureKexAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "diffie-hellman-group1-sha1",
        "diffie-hellman-group14-sha1",
        "diffie-hellman-group-exchange-sha1",
        "gss-gex-sha1-toWM5Slw5Ew8Mqkay+al2g==",
        "gss-group1-sha1-toWM5Slw5Ew8Mqkay+al2g==",
        "gss-group14-sha1-toWM5Slw5Ew8Mqkay+al2g=="
    };

    private static readonly HashSet<string> StrongHostKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssh-ed25519",
        "ssh-ed25519-cert-v01@openssh.com",
        "sk-ssh-ed25519@openssh.com",
        "sk-ssh-ed25519-cert-v01@openssh.com",
        "rsa-sha2-512",
        "rsa-sha2-512-cert-v01@openssh.com",
        "rsa-sha2-256",
        "rsa-sha2-256-cert-v01@openssh.com",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "ecdsa-sha2-nistp256-cert-v01@openssh.com",
        "ecdsa-sha2-nistp384-cert-v01@openssh.com",
        "ecdsa-sha2-nistp521-cert-v01@openssh.com",
        "sk-ecdsa-sha2-nistp256@openssh.com",
        "sk-ecdsa-sha2-nistp256-cert-v01@openssh.com"
    };

    private static readonly HashSet<string> InsecureHostKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssh-dss",
        "ssh-dss-cert-v01@openssh.com",
        "ssh-rsa",
        "ssh-rsa-cert-v01@openssh.com"
    };

    private static readonly HashSet<string> StrongCiphers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chacha20-poly1305@openssh.com",
        "aes256-gcm@openssh.com",
        "aes128-gcm@openssh.com",
        "aes256-ctr",
        "aes192-ctr",
        "aes128-ctr"
    };

    private static readonly HashSet<string> StrongMacs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hmac-sha2-512-etm@openssh.com",
        "hmac-sha2-256-etm@openssh.com",
        "umac-128-etm@openssh.com"
    };

    private static readonly HashSet<string> AcceptableMacs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hmac-sha2-512",
        "hmac-sha2-256",
        "umac-128@openssh.com"
    };

    /// <summary>Evaluates the raw <see cref="SshKexInitSnapshot"/> into a graded audit report.</summary>
    public static SshServerAuditReport Evaluate(SshKexInitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var assessments = new List<SshAlgorithmAssessment>();
        bool hasStrictKex = snapshot.KeyExchange.Any(k =>
            string.Equals(k, StrictKexServerExtension, StringComparison.OrdinalIgnoreCase));

        foreach (string kex in snapshot.KeyExchange)
        {
            if (kex.StartsWith("ext-info-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kex, StrictKexServerExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            assessments.Add(AssessKex(kex));
        }

        foreach (string hk in snapshot.HostKeys)
        {
            assessments.Add(AssessHostKey(hk));
        }

        var ciphers = snapshot.EncryptionC2S
            .Concat(snapshot.EncryptionS2C)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string cipher in ciphers)
        {
            assessments.Add(AssessCipher(cipher));
        }

        var macs = snapshot.MacC2S
            .Concat(snapshot.MacS2C)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string mac in macs)
        {
            assessments.Add(AssessMac(mac));
        }

        var compressions = snapshot.CompressionC2S
            .Concat(snapshot.CompressionS2C)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string comp in compressions)
        {
            assessments.Add(AssessCompression(comp));
        }

        bool hasPostQuantumKex = assessments.Any(a =>
            a.Category == SshAlgorithmCategory.KeyExchange && a.Rating == SshAlgorithmRating.PostQuantum);
        int insecureCount = assessments.Count(a => a.Rating == SshAlgorithmRating.Insecure);
        int weakCount = assessments.Count(a => a.Rating == SshAlgorithmRating.Weak);
        bool hasCbcCipher = assessments.Any(a =>
            a.Category == SshAlgorithmCategory.Encryption && a.Name.Contains("-cbc", StringComparison.OrdinalIgnoreCase));
        bool hasChacha = assessments.Any(a =>
            a.Category == SshAlgorithmCategory.Encryption
            && string.Equals(a.Name, "chacha20-poly1305@openssh.com", StringComparison.OrdinalIgnoreCase));
        bool hasEtmMac = assessments.Any(a =>
            a.Category == SshAlgorithmCategory.Mac && a.Name.Contains("-etm@", StringComparison.OrdinalIgnoreCase));
        bool terrapinExposed = !hasStrictKex && (hasChacha || (hasCbcCipher && hasEtmMac));
        bool hasEd25519 = assessments.Any(a =>
            a.Category == SshAlgorithmCategory.HostKey
            && a.Name.Contains("ed25519", StringComparison.OrdinalIgnoreCase));

        var findings = new List<SshAuditFinding>();

        if (snapshot.ServerBanner.StartsWith("SSH-1.", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new SshAuditFinding(
                SshAuditFindingSeverity.Critical,
                "SSH_PROTO_V1",
                "Legacy SSH-1 protocol advertised. Disable Protocol 1 immediately."));
        }

        if (hasPostQuantumKex)
        {
            findings.Add(new SshAuditFinding(
                SshAuditFindingSeverity.Pass,
                "PQ_KEX_SUPPORTED",
                "Hybrid post-quantum key exchange is advertised."));
        }
        else
        {
            findings.Add(new SshAuditFinding(
                SshAuditFindingSeverity.Info,
                "PQ_KEX_MISSING",
                "No hybrid post-quantum KEX advertised (mlkem768x25519-sha256 / sntrup761x25519-sha512)."));
        }

        if (hasStrictKex)
        {
            findings.Add(new SshAuditFinding(
                SshAuditFindingSeverity.Pass,
                "STRICT_KEX_SUPPORTED",
                "Strict KEX extension (CVE-2023-48795 Terrapin mitigation) is advertised."));
        }
        else if (terrapinExposed)
        {
            findings.Add(new SshAuditFinding(
                SshAuditFindingSeverity.Warning,
                "TERRAPIN_EXPOSED",
                "Strict KEX extension missing while ChaCha20-Poly1305 or CBC+EtM is offered (CVE-2023-48795 Terrapin)."));
        }

        if (hasEd25519)
        {
            findings.Add(new SshAuditFinding(
                SshAuditFindingSeverity.Pass,
                "ED25519_HOSTKEY",
                "Modern Ed25519 host key algorithm is available."));
        }
        else
        {
            findings.Add(new SshAuditFinding(
                SshAuditFindingSeverity.Warning,
                "ED25519_MISSING",
                "Server does not advertise an ssh-ed25519 host key."));
        }

        if (insecureCount > 0)
        {
            findings.Add(new SshAuditFinding(
                SshAuditFindingSeverity.Critical,
                "INSECURE_ALGORITHMS",
                $"{insecureCount} insecure algorithm(s) advertised (SHA-1, MD5, CBC-legacy, RC4, 3DES, Blowfish, or ssh-dss/ssh-rsa)."));
        }

        if (weakCount > 0)
        {
            findings.Add(new SshAuditFinding(
                SshAuditFindingSeverity.Warning,
                "WEAK_ALGORITHMS",
                $"{weakCount} weak or deprecated algorithm(s) advertised (CBC ciphers, non-EtM/short MACs, or pre-auth zlib)."));
        }

        bool allCoreCategoriesStrong =
            assessments.Any(a => a.Category == SshAlgorithmCategory.KeyExchange)
            && assessments.Any(a => a.Category == SshAlgorithmCategory.HostKey)
            && assessments.Any(a => a.Category == SshAlgorithmCategory.Encryption)
            && assessments.Any(a => a.Category == SshAlgorithmCategory.Mac)
            && assessments
                .Where(a => a.Category != SshAlgorithmCategory.Compression)
                .All(a => a.Rating is SshAlgorithmRating.PostQuantum or SshAlgorithmRating.Strong);

        bool isAnssiCompliant = !snapshot.ServerBanner.StartsWith("SSH-1.", StringComparison.OrdinalIgnoreCase)
            && insecureCount == 0
            && weakCount == 0
            && !terrapinExposed
            && allCoreCategoriesStrong;

        if (isAnssiCompliant)
        {
            findings.Add(new SshAuditFinding(
                SshAuditFindingSeverity.Pass,
                "ANSSI_COMPLIANT",
                "Advertised algorithms meet ANSSI / SecNumCloud baseline requirements."));
        }

        SshAuditGrade grade = ComputeGrade(
            snapshot.ServerBanner.StartsWith("SSH-1.", StringComparison.OrdinalIgnoreCase),
            insecureCount,
            weakCount,
            terrapinExposed,
            hasPostQuantumKex,
            hasStrictKex,
            allCoreCategoriesStrong);

        string recommendedConfig = BuildRecommendedSshdConfig();
        return new SshServerAuditReport(
            snapshot,
            grade,
            hasPostQuantumKex,
            hasStrictKex,
            isAnssiCompliant,
            assessments,
            findings,
            recommendedConfig);
    }

    public static string GradeToString(SshAuditGrade grade) => grade switch
    {
        SshAuditGrade.APlus => "A+",
        SshAuditGrade.A => "A",
        SshAuditGrade.B => "B",
        SshAuditGrade.C => "C",
        SshAuditGrade.D => "D",
        _ => "F"
    };

    /// <summary>Neutralizes CSV formula injection and escapes quotes per RFC 4180.</summary>
    public static string SanitizeCsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        string safe = value;
        char first = safe[0];
        if (first is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            safe = "'" + safe;
        }

        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string FormatTextReport(SshServerAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine($"SSH Server Audit - {report.Snapshot.Host}:{report.Snapshot.Port}");
        sb.AppendLine($"Banner: {report.Snapshot.ServerBanner}");
        sb.AppendLine($"Grade: {GradeToString(report.Grade)}");
        sb.AppendLine($"ANSSI / SecNumCloud: {(report.IsAnssiCompliant ? "PASS" : "FAIL")}");
        sb.AppendLine($"Post-Quantum KEX: {(report.HasPostQuantumKex ? "YES" : "NO")}");
        sb.AppendLine($"Strict KEX (Terrapin): {(report.HasStrictKex ? "YES" : "NO")}");
        sb.AppendLine();
        sb.AppendLine("Findings:");
        foreach (var f in report.Findings)
        {
            sb.AppendLine($"  [{f.Severity}] {f.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("Advertised Algorithms:");
        foreach (var alg in report.Algorithms)
        {
            sb.AppendLine($"  [{alg.Category}] {alg.Name} => {alg.Rating}{(string.IsNullOrWhiteSpace(alg.Note) ? string.Empty : $" ({alg.Note})")}");
        }

        sb.AppendLine();
        sb.AppendLine("Recommended sshd_config hardening:");
        sb.AppendLine(report.RecommendedSshdConfig);
        return sb.ToString();
    }

    public static string FormatCsvReport(SshServerAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("Host,Port,Banner,Grade,AnssiCompliant,PostQuantumKex,StrictKex,Category,Algorithm,Rating,Note");
        foreach (var alg in report.Algorithms)
        {
            sb.AppendLine(string.Join(",",
                SanitizeCsvCell(report.Snapshot.Host),
                report.Snapshot.Port.ToString(CultureInfo.InvariantCulture),
                SanitizeCsvCell(report.Snapshot.ServerBanner),
                SanitizeCsvCell(GradeToString(report.Grade)),
                report.IsAnssiCompliant ? "true" : "false",
                report.HasPostQuantumKex ? "true" : "false",
                report.HasStrictKex ? "true" : "false",
                SanitizeCsvCell(alg.Category.ToString()),
                SanitizeCsvCell(alg.Name),
                SanitizeCsvCell(alg.Rating.ToString()),
                SanitizeCsvCell(alg.Note ?? string.Empty)));
        }

        return sb.ToString();
    }

    public static string FormatHtmlReport(SshServerAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>SSH Audit - {WebUtility.HtmlEncode(report.Snapshot.Host)}:{report.Snapshot.Port}</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#1e1e2e;color:#cdd6f4;margin:24px;}h1,h2{color:#89b4fa;}table{border-collapse:collapse;width:100%;margin:12px 0;}th,td{border:1px solid #45475a;padding:6px 10px;text-align:left;}th{background:#313244;}pre{background:#181825;padding:12px;border-radius:6px;overflow-x:auto;}.badge{display:inline-block;padding:4px 10px;border-radius:4px;font-weight:bold;background:#313244;margin-right:8px;}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<h1>SSH Server Audit: {WebUtility.HtmlEncode(report.Snapshot.Host)}:{report.Snapshot.Port}</h1>");
        sb.AppendLine($"<p><span class=\"badge\">Grade: {WebUtility.HtmlEncode(GradeToString(report.Grade))}</span>");
        sb.AppendLine($"<span class=\"badge\">ANSSI/SecNumCloud: {(report.IsAnssiCompliant ? "PASS" : "FAIL")}</span>");
        sb.AppendLine($"<span class=\"badge\">Post-Quantum KEX: {(report.HasPostQuantumKex ? "YES" : "NO")}</span>");
        sb.AppendLine($"<span class=\"badge\">Strict KEX: {(report.HasStrictKex ? "YES" : "NO")}</span></p>");
        sb.AppendLine($"<p><strong>Banner:</strong> <code>{WebUtility.HtmlEncode(report.Snapshot.ServerBanner)}</code></p>");
        sb.AppendLine("<h2>Findings</h2><ul>");
        foreach (var f in report.Findings)
        {
            sb.AppendLine($"<li><strong>[{WebUtility.HtmlEncode(f.Severity.ToString())}]</strong> {WebUtility.HtmlEncode(f.Message)}</li>");
        }

        sb.AppendLine("</ul><h2>Advertised Algorithms</h2><table><thead><tr><th>Category</th><th>Algorithm</th><th>Rating</th><th>Notes</th></tr></thead><tbody>");
        foreach (var alg in report.Algorithms)
        {
            sb.AppendLine($"<tr><td>{WebUtility.HtmlEncode(alg.Category.ToString())}</td><td><code>{WebUtility.HtmlEncode(alg.Name)}</code></td><td>{WebUtility.HtmlEncode(alg.Rating.ToString())}</td><td>{WebUtility.HtmlEncode(alg.Note ?? string.Empty)}</td></tr>");
        }

        sb.AppendLine("</tbody></table><h2>Recommended sshd_config</h2>");
        sb.AppendLine($"<pre>{WebUtility.HtmlEncode(report.RecommendedSshdConfig)}</pre>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    /// <summary>Exports a consolidated multi-host batch SSH audit report to CSV (RFC 4180 + formula-safe).</summary>
    public static string FormatBatchCsvReport(IReadOnlyList<SshFleetServerAuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var sb = new StringBuilder();
        sb.AppendLine("Name,Host,Port,Gateway,Status,Grade,AnssiCompliant,PostQuantumKex,StrictKex,Banner,FindingsCount,Error");
        foreach (var e in entries)
        {
            if (e.Report is { } r)
            {
                sb.AppendLine(string.Join(",",
                    SanitizeCsvCell(e.DisplayName),
                    SanitizeCsvCell(e.Host),
                    e.Port.ToString(CultureInfo.InvariantCulture),
                    SanitizeCsvCell(e.GatewayName ?? string.Empty),
                    SanitizeCsvCell("OK"),
                    SanitizeCsvCell(GradeToString(r.Grade)),
                    r.IsAnssiCompliant ? "true" : "false",
                    r.HasPostQuantumKex ? "true" : "false",
                    r.HasStrictKex ? "true" : "false",
                    SanitizeCsvCell(r.Snapshot.ServerBanner),
                    r.Findings.Count.ToString(CultureInfo.InvariantCulture),
                    SanitizeCsvCell(string.Empty)));
            }
            else
            {
                sb.AppendLine(string.Join(",",
                    SanitizeCsvCell(e.DisplayName),
                    SanitizeCsvCell(e.Host),
                    e.Port.ToString(CultureInfo.InvariantCulture),
                    SanitizeCsvCell(e.GatewayName ?? string.Empty),
                    SanitizeCsvCell("ERROR"),
                    SanitizeCsvCell(string.Empty),
                    "false",
                    "false",
                    "false",
                    SanitizeCsvCell(string.Empty),
                    "0",
                    SanitizeCsvCell(e.ErrorMessage ?? string.Empty)));
            }
        }

        return sb.ToString();
    }

    /// <summary>Exports a consolidated multi-host batch SSH audit report to standalone HTML.</summary>
    public static string FormatBatchHtmlReport(IReadOnlyList<SshFleetServerAuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        int succeeded = entries.Count(e => e.Report is not null);
        int anssiPass = entries.Count(e => e.Report?.IsAnssiCompliant == true);
        int pqReady = entries.Count(e => e.Report?.HasPostQuantumKex == true);
        int failed = entries.Count - succeeded;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>SSH Fleet Cryptographic Audit</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#1e1e2e;color:#cdd6f4;margin:24px;}h1{color:#89b4fa;}table{border-collapse:collapse;width:100%;margin:12px 0;}th,td{border:1px solid #45475a;padding:6px 10px;text-align:left;}th{background:#313244;}.badge{display:inline-block;padding:4px 10px;border-radius:4px;font-weight:bold;background:#313244;margin-right:8px;}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>SSH Fleet Cryptographic Audit</h1>");
        sb.AppendLine($"<p><span class=\"badge\">Hosts: {entries.Count}</span><span class=\"badge\">Audited: {succeeded}</span><span class=\"badge\">ANSSI PASS: {anssiPass}</span><span class=\"badge\">PQ KEX: {pqReady}</span><span class=\"badge\">Errors: {failed}</span></p>");
        sb.AppendLine("<table><thead><tr><th>Name</th><th>Endpoint</th><th>Gateway</th><th>Grade</th><th>ANSSI</th><th>PQ KEX</th><th>Strict KEX</th><th>Banner / Error</th></tr></thead><tbody>");
        foreach (var e in entries)
        {
            string endpoint = $"{e.Host}:{e.Port}";
            if (e.Report is { } r)
            {
                sb.AppendLine(
                    $"<tr><td>{WebUtility.HtmlEncode(e.DisplayName)}</td>" +
                    $"<td><code>{WebUtility.HtmlEncode(endpoint)}</code></td>" +
                    $"<td>{WebUtility.HtmlEncode(e.GatewayName ?? "-")}</td>" +
                    $"<td><strong>{WebUtility.HtmlEncode(GradeToString(r.Grade))}</strong></td>" +
                    $"<td>{(r.IsAnssiCompliant ? "PASS" : "FAIL")}</td>" +
                    $"<td>{(r.HasPostQuantumKex ? "YES" : "NO")}</td>" +
                    $"<td>{(r.HasStrictKex ? "YES" : "NO")}</td>" +
                    $"<td><code>{WebUtility.HtmlEncode(r.Snapshot.ServerBanner)}</code></td></tr>");
            }
            else
            {
                sb.AppendLine(
                    $"<tr><td>{WebUtility.HtmlEncode(e.DisplayName)}</td>" +
                    $"<td><code>{WebUtility.HtmlEncode(endpoint)}</code></td>" +
                    $"<td>{WebUtility.HtmlEncode(e.GatewayName ?? "-")}</td>" +
                    $"<td>-</td><td>-</td><td>-</td><td>-</td>" +
                    $"<td>{WebUtility.HtmlEncode(e.ErrorMessage ?? "Error")}</td></tr>");
            }
        }

        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// Generates a ready-to-deploy <c>/etc/ssh/sshd_config.d/99-heimdall-hardening.conf</c> drop-in file
    /// targeting ANSSI/SecNumCloud and post-quantum readiness, with a safe <c>sshd -t</c> validation header.
    /// </summary>
    public static string GenerateHardeningDropIn(SshServerAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        string safeHost = SanitizeSingleLineComment(report.Snapshot.Host);
        string safeBanner = SanitizeSingleLineComment(report.Snapshot.ServerBanner);

        var sb = new StringBuilder();
        sb.AppendLine("# /etc/ssh/sshd_config.d/99-heimdall-hardening.conf");
        sb.AppendLine($"# Target audited: {safeHost}:{report.Snapshot.Port} (Current grade: {GradeToString(report.Grade)})");
        sb.AppendLine($"# Server banner: {safeBanner}");
        sb.AppendLine("# Validate before reload: sudo sshd -t -f /etc/ssh/sshd_config && sudo systemctl reload sshd");
        sb.AppendLine();
        sb.AppendLine("KexAlgorithms mlkem768x25519-sha256,sntrup761x25519-sha512@openssh.com,curve25519-sha256,curve25519-sha256@libssh.org,diffie-hellman-group16-sha512,diffie-hellman-group18-sha512");
        sb.AppendLine("HostKeyAlgorithms ssh-ed25519,rsa-sha2-512,rsa-sha2-256");
        sb.AppendLine("PubkeyAcceptedAlgorithms ssh-ed25519,ssh-ed25519-cert-v01@openssh.com,sk-ssh-ed25519@openssh.com,sk-ssh-ed25519-cert-v01@openssh.com,rsa-sha2-512,rsa-sha2-256");
        sb.AppendLine("Ciphers chacha20-poly1305@openssh.com,aes256-gcm@openssh.com,aes128-gcm@openssh.com,aes256-ctr,aes192-ctr,aes128-ctr");
        sb.AppendLine("MACs hmac-sha2-512-etm@openssh.com,hmac-sha2-256-etm@openssh.com,umac-128-etm@openssh.com");
        sb.AppendLine("Compression delayed");
        return sb.ToString();
    }

    public static int GradeRank(SshAuditGrade grade) => grade switch
    {
        SshAuditGrade.F => 0,
        SshAuditGrade.D => 1,
        SshAuditGrade.C => 2,
        SshAuditGrade.B => 3,
        SshAuditGrade.A => 4,
        SshAuditGrade.APlus => 5,
        _ => 0
    };

    public static bool TryParseGrade(string? text, out SshAuditGrade grade)
    {
        grade = SshAuditGrade.F;
        switch (text?.Trim().ToUpperInvariant())
        {
            case "A+":
                grade = SshAuditGrade.APlus;
                return true;
            case "A":
                grade = SshAuditGrade.A;
                return true;
            case "B":
                grade = SshAuditGrade.B;
                return true;
            case "C":
                grade = SshAuditGrade.C;
                return true;
            case "D":
                grade = SshAuditGrade.D;
                return true;
            case "F":
                grade = SshAuditGrade.F;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Builds a normalized baseline snapshot from all succeeded entries of a fleet audit.</summary>
    public static IReadOnlyList<SshFleetAuditBaselineEntry> BuildBaseline(
        IReadOnlyList<SshFleetServerAuditEntry> entries,
        DateTimeOffset? auditedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        DateTimeOffset timestamp = auditedAtUtc ?? DateTimeOffset.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SshFleetAuditBaselineEntry>();

        foreach (var e in entries)
        {
            if (e.Report is not { } r)
            {
                continue;
            }

            string endpoint = FormatEndpointKey(e.Host, e.Port);
            if (!seen.Add(endpoint))
            {
                continue;
            }

            result.Add(new SshFleetAuditBaselineEntry(
                endpoint,
                GradeToString(r.Grade),
                r.IsAnssiCompliant,
                r.HasPostQuantumKex,
                r.HasStrictKex,
                timestamp));
        }

        return result;
    }

    /// <summary>Compares the current fleet audit against a previous baseline to classify per-host drift.</summary>
    public static SshFleetAuditDriftSummary CompareAgainstBaseline(
        IReadOnlyList<SshFleetServerAuditEntry> current,
        IReadOnlyList<SshFleetAuditBaselineEntry>? previous)
    {
        ArgumentNullException.ThrowIfNull(current);

        var prevByEndpoint = (previous ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Endpoint))
            .DistinctBy(p => p.Endpoint, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Endpoint, StringComparer.OrdinalIgnoreCase);

        var byEndpoint = new Dictionary<string, SshFleetAuditDriftStatus>(StringComparer.OrdinalIgnoreCase);
        int improved = 0;
        int regressed = 0;
        int unchanged = 0;
        int newHosts = 0;

        // Deduplicate endpoints before counting so summary counters match ByEndpoint 1:1.
        // When multiple profiles share the same host:port, prefer a succeeded audit over an error.
        var deduped = new Dictionary<string, SshFleetServerAuditEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in current)
        {
            string endpoint = FormatEndpointKey(entry.Host, entry.Port);
            if (!deduped.TryGetValue(endpoint, out var existing) || (existing.Report is null && entry.Report is not null))
            {
                deduped[endpoint] = entry;
            }
        }

        foreach (var (endpoint, entry) in deduped)
        {
            if (entry.Report is not { } report)
            {
                byEndpoint[endpoint] = SshFleetAuditDriftStatus.Error;
                continue;
            }

            if (!prevByEndpoint.TryGetValue(endpoint, out var prev) || !TryParseGrade(prev.Grade, out var prevGrade))
            {
                byEndpoint[endpoint] = SshFleetAuditDriftStatus.New;
                newHosts++;
                continue;
            }

            int delta = GradeRank(report.Grade) - GradeRank(prevGrade);
            if (delta == 0)
            {
                int prevScore = (prev.IsAnssiCompliant ? 2 : 0) + (prev.HasPostQuantumKex ? 1 : 0) + (prev.HasStrictKex ? 1 : 0);
                int currScore = (report.IsAnssiCompliant ? 2 : 0) + (report.HasPostQuantumKex ? 1 : 0) + (report.HasStrictKex ? 1 : 0);
                delta = currScore - prevScore;
            }

            if (delta > 0)
            {
                byEndpoint[endpoint] = SshFleetAuditDriftStatus.Improved;
                improved++;
            }
            else if (delta < 0)
            {
                byEndpoint[endpoint] = SshFleetAuditDriftStatus.Regressed;
                regressed++;
            }
            else
            {
                byEndpoint[endpoint] = SshFleetAuditDriftStatus.Unchanged;
                unchanged++;
            }
        }

        return new SshFleetAuditDriftSummary(improved, regressed, unchanged, newHosts, byEndpoint);
    }

    public static string SerializeBaselineJson(IReadOnlyList<SshFleetAuditBaselineEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
    }

    public static IReadOnlyList<SshFleetAuditBaselineEntry> DeserializeBaselineJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 256 * 1024)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SshFleetAuditBaselineEntry>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string FormatEndpointKey(string host, int port)
        => $"{host.Trim()}:{port.ToString(CultureInfo.InvariantCulture)}";

    private static string SanitizeSingleLineComment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '\r' or '\n' || char.IsControl(c))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    private static SshAuditGrade ComputeGrade(
        bool isSsh1,
        int insecureCount,
        int weakCount,
        bool terrapinExposed,
        bool hasPostQuantumKex,
        bool hasStrictKex,
        bool allCoreCategoriesStrong)
    {
        if (isSsh1 || insecureCount >= 3)
        {
            return SshAuditGrade.F;
        }

        if (insecureCount > 0)
        {
            return SshAuditGrade.D;
        }

        if (weakCount >= 3 || (weakCount > 0 && terrapinExposed))
        {
            return SshAuditGrade.C;
        }

        if (weakCount > 0 || terrapinExposed)
        {
            return SshAuditGrade.B;
        }

        return hasPostQuantumKex && hasStrictKex && allCoreCategoriesStrong
            ? SshAuditGrade.APlus
            : SshAuditGrade.A;
    }

    private static SshAlgorithmAssessment AssessKex(string name)
    {
        if (PostQuantumKexAlgorithms.Contains(name))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.KeyExchange, SshAlgorithmRating.PostQuantum, "Hybrid post-quantum KEX");
        }

        if (StrongKexAlgorithms.Contains(name))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.KeyExchange, SshAlgorithmRating.Strong);
        }

        if (InsecureKexAlgorithms.Contains(name) || name.Contains("sha1", StringComparison.OrdinalIgnoreCase))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.KeyExchange, SshAlgorithmRating.Insecure, "Weak SHA-1 or small DH group");
        }

        if (name.Contains("group14-sha256", StringComparison.OrdinalIgnoreCase))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.KeyExchange, SshAlgorithmRating.Acceptable, "2048-bit DH group; prefer Curve25519 or ML-KEM");
        }

        return new SshAlgorithmAssessment(name, SshAlgorithmCategory.KeyExchange, SshAlgorithmRating.Acceptable);
    }

    private static SshAlgorithmAssessment AssessHostKey(string name)
    {
        if (StrongHostKeys.Contains(name))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.HostKey, SshAlgorithmRating.Strong);
        }

        if (InsecureHostKeys.Contains(name))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.HostKey, SshAlgorithmRating.Insecure, "Legacy SHA-1 or DSA signature");
        }

        return new SshAlgorithmAssessment(name, SshAlgorithmCategory.HostKey, SshAlgorithmRating.Acceptable);
    }

    private static SshAlgorithmAssessment AssessCipher(string name)
    {
        if (StrongCiphers.Contains(name))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Encryption, SshAlgorithmRating.Strong);
        }

        if (name.Contains("arcfour", StringComparison.OrdinalIgnoreCase)
            || name.Contains("3des", StringComparison.OrdinalIgnoreCase)
            || name.Contains("blowfish", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cast128", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Encryption, SshAlgorithmRating.Insecure, "Broken or legacy cipher");
        }

        if (name.Contains("-cbc", StringComparison.OrdinalIgnoreCase))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Encryption, SshAlgorithmRating.Weak, "CBC mode deprecated; prefer AEAD or CTR");
        }

        return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Encryption, SshAlgorithmRating.Acceptable);
    }

    private static SshAlgorithmAssessment AssessMac(string name)
    {
        if (StrongMacs.Contains(name))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Mac, SshAlgorithmRating.Strong);
        }

        if (AcceptableMacs.Contains(name))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Mac, SshAlgorithmRating.Acceptable, "Non-EtM MAC; prefer -etm@openssh.com");
        }

        if (name.Contains("md5", StringComparison.OrdinalIgnoreCase)
            || name.Contains("sha1", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-96", StringComparison.OrdinalIgnoreCase)
            || name.Contains("umac-64", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Mac, SshAlgorithmRating.Insecure, "Weak or truncated MAC");
        }

        return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Mac, SshAlgorithmRating.Weak);
    }

    private static SshAlgorithmAssessment AssessCompression(string name)
    {
        if (string.Equals(name, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "zlib@openssh.com", StringComparison.OrdinalIgnoreCase))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Compression, SshAlgorithmRating.Strong);
        }

        if (string.Equals(name, "zlib", StringComparison.OrdinalIgnoreCase))
        {
            return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Compression, SshAlgorithmRating.Weak, "Pre-authentication zlib; prefer zlib@openssh.com or none");
        }

        return new SshAlgorithmAssessment(name, SshAlgorithmCategory.Compression, SshAlgorithmRating.Acceptable);
    }

    private static string BuildRecommendedSshdConfig() =>
        """
        # Hardened OpenSSH server profile (ANSSI / SecNumCloud + PQ-ready)
        KexAlgorithms mlkem768x25519-sha256,sntrup761x25519-sha512@openssh.com,curve25519-sha256,curve25519-sha256@libssh.org,diffie-hellman-group16-sha512,diffie-hellman-group18-sha512
        HostKeyAlgorithms ssh-ed25519,rsa-sha2-512,rsa-sha2-256
        Ciphers chacha20-poly1305@openssh.com,aes256-gcm@openssh.com,aes128-gcm@openssh.com,aes256-ctr,aes192-ctr,aes128-ctr
        MACs hmac-sha2-512-etm@openssh.com,hmac-sha2-256-etm@openssh.com,umac-128-etm@openssh.com
        """;
}
