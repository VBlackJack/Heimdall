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

using Heimdall.Core.Discovery;

namespace Heimdall.Core.Tests;

public sealed class CveLookupEngineTests
{
    [Theory]
    [InlineData("SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.6", "OpenSSH", "8.9")]
    [InlineData("SSH-2.0-PuTTY_Release_0.80", "PuTTY", "0.80")]
    [InlineData("SSH-2.0-Plink_Release_0.80", "Plink", "0.80")]
    [InlineData("Apache/2.4.52 (Ubuntu)", "Apache", "2.4.52")]
    [InlineData("Apache Tomcat/9.0.80", "Apache Tomcat", "9.0.80")]
    [InlineData("nginx/1.18.0", "nginx", "1.18.0")]
    [InlineData("Microsoft-IIS/10.0", "Microsoft IIS", "10.0")]
    [InlineData("220 ProFTPD 1.3.5 Server ready", "ProFTPD", "1.3.5")]
    [InlineData("220 (vsFTPd 3.0.3)", "vsftpd", "3.0.3")]
    [InlineData("220 mail.example.com ESMTP Postfix", "Postfix", "")]
    [InlineData("220 mail.example.com ESMTP Exim 4.94", "Exim", "4.94")]
    [InlineData("MySQL 8.0.33", "MySQL", "8.0.33")]
    [InlineData("PostgreSQL/15.3", "PostgreSQL", "15.3")]
    [InlineData("Redis server v=7.2.0", "Redis", "7.2.0")]
    [InlineData("MongoDB 7.0.5", "MongoDB", "7.0.5")]
    [InlineData("Elasticsearch/8.10.0", "Elasticsearch", "8.10.0")]
    [InlineData("X-Jenkins: 2.426.3", "Jenkins", "2.426.3")]
    [InlineData("PHP/8.2.10", "PHP", "8.2.10")]
    [InlineData("Node.js v20.8.0", "Node.js", "20.8.0")]
    public void ParseBanner_KnownPatterns_ReturnExpectedSoftwareAndVersion(string input, string expectedSoftware, string expectedVersion)
    {
        var parsed = CveLookupEngine.ParseBanner(input);

        Assert.NotNull(parsed);
        Assert.Equal(expectedSoftware, parsed.Value.Software);
        Assert.Equal(expectedVersion, parsed.Value.Version);
    }

    [Fact]
    public void ParseBanner_UnknownBanner_ReturnsNull()
    {
        Assert.Null(CveLookupEngine.ParseBanner("Completely Unknown Server 1.0"));
    }

    [Theory]
    [InlineData("8.9p1", "8.9")]
    [InlineData("1.3.8b", "1.3.8")]
    [InlineData("10.0", "10.0")]
    [InlineData("v9", "")]
    public void NormalizeVersion_StripsNonNumericSuffixes(string raw, string expected)
    {
        Assert.Equal(expected, CveLookupEngine.NormalizeVersion(raw));
    }

    [Theory]
    [InlineData(9.0, CveSeverity.Critical)]
    [InlineData(7.0, CveSeverity.High)]
    [InlineData(4.0, CveSeverity.Medium)]
    [InlineData(0.1, CveSeverity.Low)]
    [InlineData(0.0, CveSeverity.None)]
    public void SeverityFromCvss_UsesExpectedThresholds(double score, CveSeverity expected)
    {
        Assert.Equal(expected, CveLookupEngine.SeverityFromCvss(score));
    }

    [Theory]
    [InlineData("8.9", "9.6", true)]
    [InlineData("9.7", "9.6", false)]
    [InlineData("8.8", "8.8", false)]
    [InlineData("1.3.7d", "1.3.7.4", true)]
    public void VersionBelow_ReturnsExpectedValue(string version, string threshold, bool expected)
    {
        Assert.Equal(expected, CveLookupEngine.VersionBelow(version, threshold));
    }

    [Theory]
    [InlineData("8.5.1", "8.5", "9.7", true)]
    [InlineData("10.0", "8.5", "9.7", false)]
    [InlineData("2.4.50", "2.4.49", "2.4.50", true)]
    [InlineData("2.4.51", "2.4.49", "2.4.50", false)]
    public void VersionInRange_ReturnsExpectedValue(string version, string from, string to, bool expected)
    {
        Assert.Equal(expected, CveLookupEngine.VersionInRange(version, from, to));
    }

    [Theory]
    [InlineData("2.4.49", "2.4.49", true)]
    [InlineData("8.9p1", "8.9", true)]
    [InlineData("8.9", "8.9.1", false)]
    public void VersionEquals_ReturnsExpectedValue(string version, string target, bool expected)
    {
        Assert.Equal(expected, CveLookupEngine.VersionEquals(version, target));
    }

    [Fact]
    public void Search_EmptyInput_ReturnsEmptyResult()
    {
        var result = CveLookupEngine.Search("   ");

        Assert.Equal(string.Empty, result.ResolvedQuery);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Search_ExactApacheTomcatMatch_TakesPriorityOverApacheFuzzyMatch()
    {
        var result = CveLookupEngine.Search("Apache Tomcat 9.0.80");

        Assert.Equal("Apache Tomcat 9.0.80", result.ResolvedQuery);
        Assert.NotEmpty(result.Matches);
        Assert.Equal("CVE-2024-50379", result.Matches[0].Id);
    }

    [Fact]
    public void Search_FuzzyMicrosoftIisMatch_ResolvesCanonicalQuery()
    {
        var result = CveLookupEngine.Search("IIS 10.0");

        Assert.Equal("Microsoft IIS 10.0", result.ResolvedQuery);
        Assert.NotEmpty(result.Matches);
    }

    [Fact]
    public void Search_NoVersionProvided_ReturnsEntireProductCatalog()
    {
        var result = CveLookupEngine.Search("OpenSSH");

        Assert.Equal("OpenSSH", result.ResolvedQuery);
        Assert.Equal(5, result.Matches.Count);
    }

    [Fact]
    public void Search_UnknownProduct_ReturnsNoMatchesButPreservesQuery()
    {
        var result = CveLookupEngine.Search("UnknownSoft 1.0");

        Assert.Equal("UnknownSoft 1.0", result.ResolvedQuery);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Search_ResultsAreSortedByCvssDescending()
    {
        var result = CveLookupEngine.Search("PHP 8.2.0");

        Assert.NotEmpty(result.Matches);
        for (var i = 1; i < result.Matches.Count; i++)
        {
            Assert.True(result.Matches[i - 1].CvssScore >= result.Matches[i].CvssScore);
        }
    }

    [Fact]
    public void BuildCopyText_PreservesExistingFormat()
    {
        var result = new CveSearchResult(
            "OpenSSH 8.9",
            [
                new CveMatch("CVE-2024-6387", 8.1, CveSeverity.High, "Sample summary", "8.5 - 9.7"),
            ]);

        var text = CveLookupEngine.BuildCopyText(result, key => key switch
        {
            "ToolCveSummary" => "{0} CVEs found for {1}",
            "ToolCveSummaryOne" => "{0} CVE found for {1}",
            "ToolCveColAffected" => "Affected Versions",
            _ => key,
        });

        Assert.Contains("1 CVE found for OpenSSH 8.9", text, StringComparison.Ordinal);
        Assert.Contains(new string('=', 72), text, StringComparison.Ordinal);
        Assert.Contains("CVE-2024-6387  [High]  CVSS 8.1", text, StringComparison.Ordinal);
        Assert.Contains("  Sample summary", text, StringComparison.Ordinal);
        Assert.Contains("  Affected Versions: 8.5 - 9.7", text, StringComparison.Ordinal);
    }
}
