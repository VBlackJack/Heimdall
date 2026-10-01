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
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.Core.Localization;

namespace Heimdall.Core.Discovery;

/// <summary>
/// Offline CVE lookup engine backed by an embedded product/version database.
/// </summary>
public static class CveLookupEngine
{
    private sealed record CveEntry(
        string Id,
        double CvssScore,
        CveSeverity Severity,
        string Summary,
        string AffectedVersions,
        Func<string, bool> IsAffected);

    private static readonly Dictionary<string, List<CveEntry>> CveDatabase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenSSH"] =
        [
            new("CVE-2024-6387", 8.1, ParseSeverity("High", 8.1), "regreSSHion: Race condition in signal handler allows unauthenticated RCE", "8.5p1 - 9.7p1", v => VersionInRange(v, "8.5", "9.7")),
            new("CVE-2023-38408", 9.8, ParseSeverity("Critical", 9.8), "PKCS#11 provider: RCE via forwarded ssh-agent", "< 9.3p2", v => VersionBelow(v, "9.3.2")),
            new("CVE-2023-48795", 5.9, ParseSeverity("Medium", 5.9), "Terrapin: Prefix truncation attack on SSH BPP (strict kex bypass)", "< 9.6", v => VersionBelow(v, "9.6")),
            new("CVE-2021-41617", 7.0, ParseSeverity("High", 7.0), "Privilege escalation via AuthorizedKeysCommand/AuthorizedPrincipalsCommand", "6.2 - 8.7", v => VersionInRange(v, "6.2", "8.7")),
            new("CVE-2020-15778", 7.8, ParseSeverity("High", 7.8), "Command injection via crafted filenames in scp", "< 8.8", v => VersionBelow(v, "8.8")),
        ],
        ["Apache"] =
        [
            new("CVE-2021-44228", 10.0, ParseSeverity("Critical", 10.0), "Log4Shell: RCE via JNDI lookup in log messages (affects mod_jk/Tomcat behind Apache)", "N/A (Log4j)", _ => false),
            new("CVE-2021-41773", 7.5, ParseSeverity("High", 7.5), "Path traversal and file disclosure in Apache HTTP Server 2.4.49", "2.4.49", v => VersionEquals(v, "2.4.49")),
            new("CVE-2021-42013", 9.8, ParseSeverity("Critical", 9.8), "RCE via path traversal (incomplete fix for CVE-2021-41773)", "2.4.49 - 2.4.50", v => VersionInRange(v, "2.4.49", "2.4.50")),
            new("CVE-2023-25690", 9.8, ParseSeverity("Critical", 9.8), "HTTP request smuggling via mod_proxy with RewriteRule", "< 2.4.56", v => VersionBelow(v, "2.4.56")),
            new("CVE-2023-43622", 7.5, ParseSeverity("High", 7.5), "HTTP/2 initial window zero DoS (rapid reset variant)", "< 2.4.58", v => VersionBelow(v, "2.4.58")),
        ],
        ["nginx"] =
        [
            new("CVE-2021-23017", 7.7, ParseSeverity("High", 7.7), "DNS resolver off-by-one heap write allows RCE", "0.6.18 - 1.20.0", v => VersionInRange(v, "0.6.18", "1.20.0")),
            new("CVE-2022-41741", 7.8, ParseSeverity("High", 7.8), "Memory corruption via crafted mp4 file (ngx_http_mp4_module)", "< 1.23.2", v => VersionBelow(v, "1.23.2")),
            new("CVE-2022-41742", 7.1, ParseSeverity("High", 7.1), "Memory disclosure via mp4 module (same root cause as CVE-2022-41741)", "< 1.23.2", v => VersionBelow(v, "1.23.2")),
            new("CVE-2024-7347", 4.7, ParseSeverity("Medium", 4.7), "Out-of-bounds read in ngx_http_mp4_module with crafted mp4", "< 1.27.1", v => VersionBelow(v, "1.27.1")),
        ],
        ["Apache Tomcat"] =
        [
            new("CVE-2024-50379", 9.8, ParseSeverity("Critical", 9.8), "RCE via partial PUT and concurrent read on case-insensitive FS", "< 11.0.2 / < 10.1.34 / < 9.0.98", v => VersionBelow(v, "11.0.2")),
            new("CVE-2024-52317", 6.5, ParseSeverity("Medium", 6.5), "Response mix-up due to incorrect HTTP/2 recycling", "< 11.0.1 / < 10.1.33 / < 9.0.97", v => VersionBelow(v, "11.0.1")),
            new("CVE-2023-46589", 7.5, ParseSeverity("High", 7.5), "HTTP request smuggling via malformed trailer headers", "< 10.1.18 / < 9.0.83 / < 8.5.96", v => VersionBelow(v, "10.1.18")),
            new("CVE-2023-44487", 7.5, ParseSeverity("High", 7.5), "HTTP/2 rapid reset DoS attack", "< 10.1.14 / < 9.0.81 / < 8.5.94", v => VersionBelow(v, "10.1.14")),
        ],
        ["MySQL"] =
        [
            new("CVE-2024-21047", 4.9, ParseSeverity("Medium", 4.9), "Server: InnoDB DoS via crafted queries", "< 8.0.37", v => VersionBelow(v, "8.0.37")),
            new("CVE-2023-22008", 4.9, ParseSeverity("Medium", 4.9), "Server optimizer DoS vulnerability", "< 8.0.34", v => VersionBelow(v, "8.0.34")),
            new("CVE-2023-21980", 7.1, ParseSeverity("High", 7.1), "Client: buffer overflow in C API", "< 8.0.33", v => VersionBelow(v, "8.0.33")),
            new("CVE-2024-20960", 6.5, ParseSeverity("Medium", 6.5), "Server: RAPID memory DoS in optimizer", "< 8.0.36", v => VersionBelow(v, "8.0.36")),
        ],
        ["PostgreSQL"] =
        [
            new("CVE-2024-10979", 8.8, ParseSeverity("High", 8.8), "Arbitrary code execution via environment variable manipulation in PL/Perl", "< 17.1 / < 16.5 / < 15.9", v => VersionBelow(v, "17.1")),
            new("CVE-2023-5868", 4.3, ParseSeverity("Medium", 4.3), "Memory disclosure via aggregate function calls", "< 16.1", v => VersionBelow(v, "16.1")),
            new("CVE-2023-5869", 8.8, ParseSeverity("High", 8.8), "Buffer overflow in integer overflow in array modification", "< 16.1", v => VersionBelow(v, "16.1")),
            new("CVE-2023-39417", 8.8, ParseSeverity("High", 8.8), "SQL injection in extension script @extowner@, @extschema@ replacements", "< 15.4", v => VersionBelow(v, "15.4")),
        ],
        ["Redis"] =
        [
            new("CVE-2024-31449", 8.8, ParseSeverity("High", 8.8), "Lua library: heap buffer overflow from crafted script", "< 7.4.1", v => VersionBelow(v, "7.4.1")),
            new("CVE-2023-45145", 3.6, ParseSeverity("Low", 3.6), "Unix socket permission race condition on startup", "< 7.2.4", v => VersionBelow(v, "7.2.4")),
            new("CVE-2023-41056", 8.1, ParseSeverity("High", 8.1), "Heap overflow in Cluster-mode pattern matching", "< 7.2.1", v => VersionBelow(v, "7.2.1")),
            new("CVE-2023-28856", 6.5, ParseSeverity("Medium", 6.5), "AUTH command crash allows authenticated DoS", "< 7.0.12", v => VersionBelow(v, "7.0.12")),
        ],
        ["MongoDB"] =
        [
            new("CVE-2024-1351", 8.1, ParseSeverity("High", 8.1), "Incorrect validation of SCRAM authentication leading to auth bypass", "< 7.0.5 / < 6.0.13", v => VersionBelow(v, "7.0.5")),
            new("CVE-2023-1409", 7.5, ParseSeverity("High", 7.5), "TLS certificate validation bypass on non-default configurations", "< 6.0.7 / < 5.0.18", v => VersionBelow(v, "6.0.7")),
            new("CVE-2024-3372", 7.5, ParseSeverity("High", 7.5), "Server crash via malformed BSON in validated collection", "< 7.0.9 / < 6.0.15", v => VersionBelow(v, "7.0.9")),
        ],
        ["Elasticsearch"] =
        [
            new("CVE-2023-31419", 7.5, ParseSeverity("High", 7.5), "Stack overflow DoS from malformed _search API query", "< 8.9.1 / < 7.17.13", v => VersionBelow(v, "8.9.1")),
            new("CVE-2023-46674", 8.8, ParseSeverity("High", 8.8), "File descriptor leak allows local file read", "< 8.11.1 / < 7.17.15", v => VersionBelow(v, "8.11.1")),
            new("CVE-2024-23450", 7.5, ParseSeverity("High", 7.5), "DoS via excessive recursion in nested document expansion", "< 8.13.0 / < 7.17.19", v => VersionBelow(v, "8.13.0")),
        ],
        ["Microsoft IIS"] =
        [
            new("CVE-2023-36899", 8.8, ParseSeverity("High", 8.8), "Elevation of privilege via ASP.NET identity spoofing", "IIS 10.0", v => v.StartsWith("10.", StringComparison.Ordinal) || v == "10"),
            new("CVE-2024-38169", 8.8, ParseSeverity("High", 8.8), "RCE via crafted HTTP request in ASP.NET IIS module", "IIS 10.0", v => v.StartsWith("10.", StringComparison.Ordinal) || v == "10"),
            new("CVE-2022-21907", 9.8, ParseSeverity("Critical", 9.8), "HTTP Protocol Stack (http.sys) RCE in trailer support", "IIS 10.0 (Win Server 2022)", v => v.StartsWith("10.", StringComparison.Ordinal) || v == "10"),
        ],
        ["ProFTPD"] =
        [
            new("CVE-2023-51713", 7.5, ParseSeverity("High", 7.5), "Out-of-bounds read in mod_sftp during key exchange", "< 1.3.8b", v => VersionBelow(v, "1.3.8.2")),
            new("CVE-2021-46854", 7.5, ParseSeverity("High", 7.5), "Memory disclosure in mod_radius via crafted RADIUS response", "< 1.3.7d", v => VersionBelow(v, "1.3.7.4")),
            new("CVE-2020-9273", 8.8, ParseSeverity("High", 8.8), "Use-after-free in memory pool transfer leading to RCE", "< 1.3.6c", v => VersionBelow(v, "1.3.6.3")),
        ],
        ["vsftpd"] =
        [
            new("CVE-2021-3618", 7.4, ParseSeverity("High", 7.4), "ALPACA: Application-layer protocol cross-protocol attack on TLS", "< 3.0.4", v => VersionBelow(v, "3.0.4")),
            new("CVE-2015-1419", 5.3, ParseSeverity("Medium", 5.3), "Config parsing allows directory listing bypass", "< 3.0.3", v => VersionBelow(v, "3.0.3")),
        ],
        ["Postfix"] =
        [
            new("CVE-2023-51764", 5.3, ParseSeverity("Medium", 5.3), "SMTP smuggling allows email spoofing via multi-line pipelining", "< 3.8.4", v => VersionBelow(v, "3.8.4")),
            new("CVE-2023-32182", 5.3, ParseSeverity("Medium", 5.3), "Incorrect permission on mail directories during creation", "< 3.7.5", v => VersionBelow(v, "3.7.5")),
            new("CVE-2021-28085", 4.3, ParseSeverity("Medium", 4.3), "Denial of service via large MIME attachment processing", "< 3.6.0", v => VersionBelow(v, "3.6.0")),
        ],
        ["Exim"] =
        [
            new("CVE-2023-42117", 8.1, ParseSeverity("High", 8.1), "Improper neutralization of special elements in SMTP AUTH", "< 4.96.2", v => VersionBelow(v, "4.96.2")),
            new("CVE-2023-42119", 3.1, ParseSeverity("Low", 3.1), "DNS name parsing out-of-bounds read", "< 4.96.2", v => VersionBelow(v, "4.96.2")),
            new("CVE-2023-51766", 5.3, ParseSeverity("Medium", 5.3), "SMTP smuggling via multi-line pipelining", "< 4.97.1", v => VersionBelow(v, "4.97.1")),
            new("CVE-2024-39929", 9.1, ParseSeverity("Critical", 9.1), "RFC 2231 header filename parsing bypass allows attachment filter evasion", "< 4.98", v => VersionBelow(v, "4.98")),
        ],
        ["PHP"] =
        [
            new("CVE-2024-4577", 9.8, ParseSeverity("Critical", 9.8), "CGI argument injection on Windows (Best-Fit mapping bypass)", "< 8.3.8 / < 8.2.20 / < 8.1.29", v => VersionBelow(v, "8.3.8")),
            new("CVE-2024-2961", 8.8, ParseSeverity("High", 8.8), "Buffer overflow in glibc iconv via PHP filters", "< 8.3.8", v => VersionBelow(v, "8.3.8")),
            new("CVE-2023-3824", 9.8, ParseSeverity("Critical", 9.8), "Buffer overflow in PHAR reading for tar/zip/phar", "< 8.2.10 / < 8.1.23", v => VersionBelow(v, "8.2.10")),
            new("CVE-2024-8932", 9.8, ParseSeverity("Critical", 9.8), "Heap overflow in ldap_escape on 32-bit systems", "< 8.3.14 / < 8.2.26 / < 8.1.31", v => VersionBelow(v, "8.3.14")),
        ],
        ["Node.js"] =
        [
            new("CVE-2024-22019", 7.5, ParseSeverity("High", 7.5), "Reading unprocessed HTTP request with unbounded chunk extension causes DoS", "< 21.6.1 / < 20.11.1 / < 18.19.1", v => VersionBelow(v, "21.6.1")),
            new("CVE-2024-22025", 6.5, ParseSeverity("Medium", 6.5), "Denial of service via fetch with long Content-Length in Undici", "< 21.6.1 / < 20.11.1", v => VersionBelow(v, "21.6.1")),
            new("CVE-2023-44487", 7.5, ParseSeverity("High", 7.5), "HTTP/2 rapid reset attack causing DoS", "< 20.8.1 / < 18.18.2", v => VersionBelow(v, "20.8.1")),
            new("CVE-2023-32002", 9.8, ParseSeverity("Critical", 9.8), "Policy bypass via Module._load", "< 20.5.1 / < 18.17.1", v => VersionBelow(v, "20.5.1")),
        ],
        ["Jenkins"] =
        [
            new("CVE-2024-23897", 9.8, ParseSeverity("Critical", 9.8), "Arbitrary file read via CLI command parser (args4j @-file expansion)", "< 2.442 / < 2.426.3", v => VersionBelow(v, "2.442")),
            new("CVE-2024-43044", 8.8, ParseSeverity("High", 8.8), "Arbitrary file read on agent via Remoting library ClassLoaderProxy", "< 2.471 / < 2.452.4", v => VersionBelow(v, "2.471")),
            new("CVE-2023-27898", 8.8, ParseSeverity("High", 8.8), "Stored XSS via crafted plugin name leading to RCE", "< 2.394 / < 2.375.4", v => VersionBelow(v, "2.394")),
        ],
        ["Plink"] =
        [
            new("CVE-2024-31497", 5.9, ParseSeverity("Medium", 5.9), "Biased ECDSA nonce generation allows private key recovery (P-521)", "< 0.81", v => VersionBelow(v, "0.81")),
            new("CVE-2023-48795", 5.9, ParseSeverity("Medium", 5.9), "Terrapin: Prefix truncation attack on SSH BPP (strict kex bypass)", "< 0.80", v => VersionBelow(v, "0.80")),
        ],
        ["PuTTY"] =
        [
            new("CVE-2024-31497", 5.9, ParseSeverity("Medium", 5.9), "Biased ECDSA nonce generation allows private key recovery (P-521)", "< 0.81", v => VersionBelow(v, "0.81")),
            new("CVE-2023-48795", 5.9, ParseSeverity("Medium", 5.9), "Terrapin: Prefix truncation attack on SSH BPP (strict kex bypass)", "< 0.80", v => VersionBelow(v, "0.80")),
            new("CVE-2021-36367", 7.4, ParseSeverity("High", 7.4), "Host key verification bypass allows MITM via fake authentication prompts", "< 0.76", v => VersionBelow(v, "0.76")),
        ],
    };

    private static readonly (Regex Pattern, string Software)[] BannerPatterns =
    [
        (new Regex(@"OpenSSH[_/ ]?(\d[\d.p]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "OpenSSH"),
        (new Regex(@"PuTTY[_/ ]?(?:Release[_/ ]?)?(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "PuTTY"),
        (new Regex(@"Plink[_/ ]?(?:Release[_/ ]?)?(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Plink"),
        (new Regex(@"Apache(?:/| HTTPD |httpd[ /])(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Apache"),
        (new Regex(@"Apache Tomcat/(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Apache Tomcat"),
        (new Regex(@"nginx/(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "nginx"),
        (new Regex(@"Microsoft-IIS/(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Microsoft IIS"),
        (new Regex(@"ProFTPD[/ ](\d[\d.a-z]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ProFTPD"),
        (new Regex(@"vsftpd[/ ](\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "vsftpd"),
        (new Regex(@"Postfix(?:.*?version )?(\d[\d.]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Postfix"),
        (new Regex(@"Exim[/ ](\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Exim"),
        (new Regex(@"MySQL(?:.*?)?(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "MySQL"),
        (new Regex(@"PostgreSQL[/ ](\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "PostgreSQL"),
        (new Regex(@"Redis(?:.*?v=?| server v=?)(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Redis"),
        (new Regex(@"MongoDB(?:.*?v?| server v?)(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "MongoDB"),
        (new Regex(@"Elasticsearch[/ ](\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Elasticsearch"),
        (new Regex(@"Jenkins[/ :]*(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Jenkins"),
        (new Regex(@"PHP[/ ](\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "PHP"),
        (new Regex(@"Node\.?js[/ ]v?(\d[\d.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Node.js"),
    ];

    private static readonly Regex SimpleNameVersion = new(
        @"^([A-Za-z][A-Za-z0-9 .+_-]*?)\s+v?(\d[\d.p]*\d)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Gets the total number of embedded CVE entries.
    /// </summary>
    public static int TotalCveCount { get; } = CveDatabase.Values.Sum(list => list.Count);

    /// <summary>
    /// Gets the total number of products in the embedded database.
    /// </summary>
    public static int TotalProductCount { get; } = CveDatabase.Count;

    /// <summary>
    /// Searches the embedded CVE database using a software name, version, or service banner.
    /// </summary>
    public static CveSearchResult Search(string input)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return new CveSearchResult(string.Empty, Array.Empty<CveMatch>());
        }

        string software;
        string version;

        var parsed = ParseBanner(trimmed);
        if (parsed is not null)
        {
            software = parsed.Value.Software;
            version = parsed.Value.Version;
        }
        else
        {
            var simpleMatch = SimpleNameVersion.Match(trimmed);
            if (simpleMatch.Success)
            {
                software = simpleMatch.Groups[1].Value.Trim();
                version = simpleMatch.Groups[2].Value.Trim();
            }
            else
            {
                software = trimmed;
                version = string.Empty;
            }
        }

        var resolvedQuery = string.IsNullOrEmpty(version) ? software : $"{software} {version}";

        if (CveDatabase.TryGetValue(software, out var entries))
        {
            return new CveSearchResult(resolvedQuery, BuildMatches(entries, version));
        }

        var fuzzyKey = CveDatabase.Keys.FirstOrDefault(k =>
            k.Contains(software, StringComparison.OrdinalIgnoreCase) ||
            software.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (fuzzyKey is not null)
        {
            var fuzzyResolved = string.IsNullOrEmpty(version) ? fuzzyKey : $"{fuzzyKey} {version}";
            return new CveSearchResult(fuzzyResolved, BuildMatches(CveDatabase[fuzzyKey], version));
        }

        return new CveSearchResult(resolvedQuery, Array.Empty<CveMatch>());
    }

    /// <summary>
    /// Derives a severity from a CVSS score using the tool's thresholds.
    /// </summary>
    public static CveSeverity SeverityFromCvss(double cvssScore) => cvssScore switch
    {
        >= 9.0 => CveSeverity.Critical,
        >= 7.0 => CveSeverity.High,
        >= 4.0 => CveSeverity.Medium,
        > 0.0 => CveSeverity.Low,
        _ => CveSeverity.None,
    };

    /// <summary>
    /// Parses a banner to a software/version tuple using the embedded regex list.
    /// </summary>
    public static (string Software, string Version)? ParseBanner(string input)
    {
        foreach (var (pattern, software) in BannerPatterns)
        {
            var match = pattern.Match(input);
            if (match.Success)
            {
                var version = match.Groups.Count > 1 && match.Groups[1].Success
                    ? NormalizeVersion(match.Groups[1].Value)
                    : string.Empty;
                return (software, version);
            }
        }

        return null;
    }

    /// <summary>
    /// Strips non-version suffixes like "p1" and keeps only leading numeric segments.
    /// </summary>
    public static string NormalizeVersion(string raw)
    {
        var sb = new StringBuilder();
        var lastWasDot = false;

        foreach (var c in raw)
        {
            if (char.IsDigit(c))
            {
                sb.Append(c);
                lastWasDot = false;
            }
            else if (c == '.' && !lastWasDot && sb.Length > 0)
            {
                sb.Append('.');
                lastWasDot = true;
            }
            else
            {
                break;
            }
        }

        return sb.ToString().TrimEnd('.');
    }

    /// <summary>
    /// Parses a version string into integer components.
    /// </summary>
    public static int[] ParseVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v))
        {
            return Array.Empty<int>();
        }

        var normalized = NormalizeVersion(v);
        if (string.IsNullOrEmpty(normalized))
        {
            return Array.Empty<int>();
        }

        var parts = normalized.Split('.');
        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out var number))
            {
                result.Add(number);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Returns true when a version is strictly below the supplied threshold.
    /// </summary>
    public static bool VersionBelow(string version, string threshold)
    {
        var current = ParseVersion(version);
        var target = ParseVersion(threshold);
        return current.Length > 0 && target.Length > 0 && CompareVersions(current, target) < 0;
    }

    /// <summary>
    /// Returns true when a version is within an inclusive range.
    /// </summary>
    public static bool VersionInRange(string version, string from, string to)
    {
        var current = ParseVersion(version);
        var lower = ParseVersion(from);
        var upper = ParseVersion(to);
        return current.Length > 0
            && lower.Length > 0
            && upper.Length > 0
            && CompareVersions(current, lower) >= 0
            && CompareVersions(current, upper) <= 0;
    }

    /// <summary>
    /// Returns true when a version equals the supplied target after normalization.
    /// </summary>
    public static bool VersionEquals(string version, string target)
    {
        var current = ParseVersion(version);
        var expected = ParseVersion(target);
        return current.Length > 0 && expected.Length > 0 && CompareVersions(current, expected) == 0;
    }

    /// <summary>
    /// Compares two version arrays component by component.
    /// </summary>
    public static int CompareVersions(int[] a, int[] b)
    {
        var maxLength = Math.Max(a.Length, b.Length);
        for (var i = 0; i < maxLength; i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    /// <summary>
    /// Builds the clipboard text report using the current search result.
    /// </summary>
    /// <param name="result">The search result to report.</param>
    /// <param name="localize">Maps a key to its text.</param>
    /// <param name="countLocalizer">
    /// Words the match count in the language of <paramref name="localize"/>; when omitted, through
    /// <paramref name="localize"/> under the English rule.
    /// </param>
    public static string BuildCopyText(
        CveSearchResult result,
        Func<string, string> localize,
        ICountLocalizer? countLocalizer = null)
    {
        var sb = new StringBuilder();
        ICountLocalizer counts = countLocalizer ?? DelegateCountLocalizer.English(localize);
        sb.AppendLine(counts.FormatCount(
            result.Matches.Count,
            "ToolCveSummaryOne",
            "ToolCveSummary",
            result.Matches.Count,
            result.ResolvedQuery));
        sb.AppendLine(new string('=', 72));
        sb.AppendLine();

        foreach (var match in result.Matches)
        {
            sb.AppendLine($"{match.Id}  [{SeverityToCanonicalLabel(match.Severity)}]  CVSS {match.CvssScore.ToString("F1", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"  {match.Summary}");
            sb.AppendLine($"  {localize("ToolCveColAffected")}: {match.AffectedVersions}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static IReadOnlyList<CveMatch> BuildMatches(List<CveEntry> entries, string version)
    {
        var matches = new List<CveMatch>();

        foreach (var entry in entries)
        {
            var isAffected = string.IsNullOrEmpty(version) || entry.IsAffected(version);
            if (!isAffected && !string.IsNullOrEmpty(version))
            {
                continue;
            }

            matches.Add(new CveMatch(
                entry.Id,
                entry.CvssScore,
                entry.Severity,
                entry.Summary,
                entry.AffectedVersions));
        }

        matches.Sort((a, b) => b.CvssScore.CompareTo(a.CvssScore));
        return matches;
    }

    private static CveSeverity ParseSeverity(string severity, double cvssScore) => severity switch
    {
        "Critical" => CveSeverity.Critical,
        "High" => CveSeverity.High,
        "Medium" => CveSeverity.Medium,
        "Low" => CveSeverity.Low,
        _ => SeverityFromCvss(cvssScore),
    };

    private static string SeverityToCanonicalLabel(CveSeverity severity) => severity switch
    {
        CveSeverity.Critical => "Critical",
        CveSeverity.High => "High",
        CveSeverity.Medium => "Medium",
        CveSeverity.Low => "Low",
        _ => "None",
    };
}
