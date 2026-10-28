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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Heimdall.Core.Tests;

/// <summary>
/// Every in-page link of a public document must land on something GitHub renders.
/// </summary>
/// <remarks>
/// <para>A link such as <c>[text](#some-id)</c> works on GitHub in exactly two cases: the target is
/// the slug GitHub derives from a heading, or the document carries an explicit anchor
/// (<c>&lt;a id="some-id"&gt;&lt;/a&gt;</c>). The <c>{#some-id}</c> suffix some Markdown processors
/// accept after a heading is NOT one of them: GitHub prints it as literal text and creates no id.
/// The troubleshooting guide shipped with 130 table-of-contents links that resolved to neither, and
/// nothing noticed, because a Markdown link that goes nowhere raises no error.</para>
/// <para>The scan is recursive and asserts that it reached the French mirror, for the reason
/// <see cref="DocumentationTypographyGuardTests"/> gives: a sweep that silently skips a directory
/// reports a clean tree.</para>
/// </remarks>
public sealed class DocumentationAnchorGuardTests
{
    private static readonly Regex HeadingPattern = new(@"^ {0,3}(#{1,6})[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex FencePattern = new(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.Compiled);
    private static readonly Regex ExplicitIdPattern = new(@"<[a-zA-Z][^>]*?\b(?:id|name)\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled);
    private static readonly Regex InPageLinkPattern = new(@"\]\(#([^)\s]*)\)", RegexOptions.Compiled);
    private static readonly Regex InlineLinkPattern = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);

    [Fact]
    public void EveryInPageLinkOfThePublicDocumentationResolves()
    {
        List<string> broken = [];
        int documents = 0;
        int links = 0;

        foreach (string path in PublicDocuments())
        {
            documents++;
            AnchorReport report = Analyze(File.ReadAllText(path));
            links += report.LinkCount;
            broken.AddRange(report.Broken.Select(b => $"{Relative(path)}:{b.Line} links to #{b.Target}"));
        }

        Assert.True(documents >= 20, $"only {documents} public documents were scanned");
        Assert.True(links >= 100, $"only {links} in-page links were found; the pattern matches nothing");
        Assert.True(
            broken.Count == 0,
            $"{broken.Count} in-page link(s) resolve to neither a heading slug nor an explicit anchor:\n"
            + string.Join("\n", broken.Take(40)));
    }

    /// <summary>
    /// The sweep must read the French mirror, where the same table of contents lives.
    /// </summary>
    [Fact]
    public void TheScanReachesTheFrenchMirrorAndItsLinks()
    {
        string frenchGuide = Path.Combine(FindRepoRoot(), "docs", "fr", "TROUBLESHOOTING.md");

        Assert.Contains(frenchGuide, PublicDocuments());
        Assert.True(
            Analyze(File.ReadAllText(frenchGuide)).LinkCount >= 40,
            "the French troubleshooting guide was read but its table of contents produced no links");
    }

    [Fact]
    public void ABrokenLinkIsReported()
    {
        const string text = "# Title\n\n[ok](#title) [gone](#nowhere)\n";

        AnchorReport report = Analyze(text);

        Assert.Equal(2, report.LinkCount);
        AnchorProblem problem = Assert.Single(report.Broken);
        Assert.Equal("nowhere", problem.Target);
        Assert.Equal(3, problem.Line);
    }

    [Fact]
    public void AnExplicitAnchorResolvesALinkThatNoHeadingSlugMatches()
    {
        const string text = "[go](#custom-id)\n\n<a id=\"custom-id\"></a>\n## 4. A long heading\n";

        Assert.Empty(Analyze(text).Broken);
    }

    /// <summary>
    /// The attribute suffix is not an anchor on GitHub: it stays in the heading text, so it ends
    /// up inside the slug and the id the author meant is never created.
    /// </summary>
    [Fact]
    public void AnAttributeSuffixOnAHeadingDoesNotCreateTheAnchor()
    {
        const string text = "[go](#custom-id)\n\n## 4. A heading {#custom-id}\n";

        AnchorProblem problem = Assert.Single(Analyze(text).Broken);
        Assert.Equal("custom-id", problem.Target);
    }

    [Fact]
    public void CodeFencesNeitherDefineHeadingsNorCountAsLinks()
    {
        const string text = "```\n# Not a heading\n[x](#not-a-link)\n```\n\n[go](#not-a-heading)\n";

        AnchorProblem problem = Assert.Single(Analyze(text).Broken);
        Assert.Equal("not-a-heading", problem.Target);
        Assert.Equal(1, Analyze(text).LinkCount);
    }

    [Theory]
    [InlineData("RDP Embedded - White Screen", "rdp-embedded---white-screen")]
    [InlineData("1. RDP Embedded - White Screen", "1-rdp-embedded---white-screen")]
    [InlineData("Sub-chapter (with) `code`, and: punctuation!", "sub-chapter-with-code-and-punctuation")]
    [InlineData("Under_score kept", "under_score-kept")]
    [InlineData("Café Été", "café-été")]
    [InlineData("A [link](https://example.com/x) here", "a-link-here")]
    [InlineData("SFTP - Sudo Fallback (Auth Failure)", "sftp---sudo-fallback-auth-failure")]
    public void TheSlugFollowsGitHub(string heading, string expected)
    {
        Assert.Equal(expected, Slugify(heading));
    }

    [Fact]
    public void RepeatedHeadingsGetNumericSuffixes()
    {
        const string text = "# Same\n\n# Same\n\n# Same\n\n[a](#same) [b](#same-1) [c](#same-2) [d](#same-3)\n";

        AnchorProblem problem = Assert.Single(Analyze(text).Broken);
        Assert.Equal("same-3", problem.Target);
    }

    private static AnchorReport Analyze(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        HashSet<string> anchors = new(StringComparer.Ordinal);
        Dictionary<string, int> slugCounts = new(StringComparer.Ordinal);
        List<(int Line, string Target)> links = [];

        string? fence = null;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            Match fenceMatch = FencePattern.Match(line);
            if (fenceMatch.Success)
            {
                string marker = fenceMatch.Groups[1].Value;
                if (fence is null)
                {
                    fence = marker;
                }
                else if (marker[0] == fence[0] && marker.Length >= fence.Length)
                {
                    fence = null;
                }

                continue;
            }

            if (fence is not null)
            {
                continue;
            }

            foreach (Match id in ExplicitIdPattern.Matches(line))
            {
                anchors.Add(id.Groups[1].Value);
            }

            Match heading = HeadingPattern.Match(line);
            if (heading.Success)
            {
                string slug = Slugify(heading.Groups[2].Value);
                slugCounts.TryGetValue(slug, out int seen);
                slugCounts[slug] = seen + 1;
                anchors.Add(seen == 0 ? slug : $"{slug}-{seen}");
            }

            foreach (Match link in InPageLinkPattern.Matches(line))
            {
                links.Add((index + 1, Uri.UnescapeDataString(link.Groups[1].Value)));
            }
        }

        List<AnchorProblem> broken =
        [
            .. links.Where(l => !anchors.Contains(l.Target)).Select(l => new AnchorProblem(l.Line, l.Target)),
        ];
        return new AnchorReport(links.Count, broken);
    }

    /// <summary>
    /// The id GitHub gives a heading: the rendered text lower-cased, every character that is not a
    /// letter, a digit, an underscore, a hyphen or a space dropped, and each space turned into a
    /// hyphen. Hyphens are kept one for one, so "A - B" becomes "a---b".
    /// </summary>
    private static string Slugify(string heading)
    {
        string text = InlineLinkPattern.Replace(heading, "$1");
        text = HtmlTagPattern.Replace(text, string.Empty);

        StringBuilder slug = new(text.Length);
        foreach (char character in text.ToLowerInvariant())
        {
            if (character == ' ')
            {
                slug.Append('-');
            }
            else if (char.IsLetterOrDigit(character) || character is '-' or '_'
                || char.GetUnicodeCategory(character) is
                    System.Globalization.UnicodeCategory.NonSpacingMark
                    or System.Globalization.UnicodeCategory.SpacingCombiningMark)
            {
                slug.Append(character);
            }
        }

        return slug.ToString();
    }

    private static IReadOnlyList<string> PublicDocuments()
    {
        string root = FindRepoRoot();
        return
        [
            .. Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly),
            .. Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories),
        ];
    }

    private static string Relative(string path)
        => Path.GetRelativePath(FindRepoRoot(), path);

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root from test binary directory: {AppContext.BaseDirectory}");
    }

    private sealed record AnchorProblem(int Line, string Target);

    private sealed record AnchorReport(int LinkCount, IReadOnlyList<AnchorProblem> Broken);
}
