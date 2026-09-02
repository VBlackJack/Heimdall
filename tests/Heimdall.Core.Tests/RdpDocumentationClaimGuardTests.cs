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
using System.Text.Json;

namespace Heimdall.Core.Tests;

/// <summary>
/// Pins the claims the RDP pages make about the repository, the user interface and the
/// certificate gate to the artefacts that settle them.
/// </summary>
/// <remarks>
/// <para><see cref="DocumentationTypographyGuardTests"/> already opens these files, but it reads
/// characters. A page can be perfectly punctuated and still send the reader to a script that was
/// never committed, to a tab that carries another name, or promise a capability the source says
/// the product cannot deliver. Each of those shipped at least once, and none of them could fail
/// a test.</para>
/// <para>Every assertion here compares two artefacts of the repository - a document against the
/// locale files, the file system or a source tree - never a restatement of the document. Each
/// forbidden phrase is paired with a required one, so the guard cannot be satisfied by deleting
/// the paragraph that carried the claim.</para>
/// </remarks>
public sealed class RdpDocumentationClaimGuardTests
{
    private const string EnglishCertificateHeading = "### RDP server certificate trust";
    private const string FrenchCertificateHeading =
        "### Confiance envers le certificat de serveur RDP";

    // The pages this guard owns. Documents outside the list carry unresolved path references of
    // their own - docs/CHANGELOG.md quotes paths that were valid when the entry was written - so
    // widening the sweep is a separate decision.
    private static readonly string[] GuardedDocuments =
    [
        "docs/RDP-PERFORMANCE.md",
        "docs/fr/RDP-PERFORMANCE.md",
        "docs/FEATURES.md",
        "docs/fr/FEATURES.md",
        "docs/SECURITY.md",
        "docs/fr/SECURITY.md",
    ];

    private static readonly string[] RepositoryPrefixes =
        ["local/", "scripts/", "src/", "tests/", "docs/"];

    [Fact]
    public void GuardedDocumentsOnlyPointAtRepositoryPathsThatExist()
    {
        string root = FindRepoRoot();
        List<string> violations = [];
        int candidates = 0;

        foreach (string document in GuardedDocuments)
        {
            string[] lines = File.ReadAllLines(Path.Combine(root, document));
            for (int index = 0; index < lines.Length; index++)
            {
                foreach (string reference in BacktickedRepositoryPaths(lines[index]))
                {
                    candidates++;
                    string absolute = Path.Combine(root, reference);
                    if (!File.Exists(absolute) && !Directory.Exists(absolute))
                    {
                        violations.Add($"{document}:{index + 1} -> {reference}");
                    }
                }
            }
        }

        // Positive control: a broken extraction reports zero violations over zero candidates.
        Assert.True(
            candidates >= 20,
            $"Only {candidates} repository paths were extracted; the extraction is broken.");

        Assert.True(
            violations.Count == 0,
            "Documentation points at repository paths that do not exist:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void RdpPerformancePageNamesTheTabsTheUserInterfaceShows()
    {
        string root = FindRepoRoot();
        IReadOnlyDictionary<string, string> english = Locale(root, "en");
        IReadOnlyDictionary<string, string> french = Locale(root, "fr");

        AssertNavigationParagraph(
            root,
            "docs/RDP-PERFORMANCE.md",
            marker: "off by default",
            required:
            [
                english["SettingsTabRdp"],
                english["SettingsRdpSubTabPerformance"],
                english["ServerDialogTabOptions"],
                english["ServerDialogRdpOptions"],
            ],
            forbidden: english["ServerDialogProtocolRdpName"]);

        AssertNavigationParagraph(
            root,
            "docs/fr/RDP-PERFORMANCE.md",
            marker: "désactivé par défaut",
            required:
            [
                french["SettingsTabRdp"],
                french["SettingsRdpSubTabPerformance"],
                french["ServerDialogTabOptions"],
                french["ServerDialogRdpOptions"],
            ],
            forbidden: french["ServerDialogProtocolRdpName"]);

        AssertNavigationParagraph(
            root,
            "docs/RDP-PERFORMANCE.md",
            marker: english["ServerDialogResolutionModeLabel"],
            required:
            [
                english["ServerDialogTabOptions"],
                english["ServerDialogRdpSubTabDisplayAudio"],
                english["ServerDialogResolutionModeFixed"],
            ],
            forbidden: english["ServerDialogProtocolRdpName"]);

        AssertNavigationParagraph(
            root,
            "docs/fr/RDP-PERFORMANCE.md",
            marker: french["ServerDialogResolutionModeLabel"],
            required:
            [
                french["ServerDialogTabOptions"],
                french["ServerDialogRdpSubTabDisplayAudio"],
                french["ServerDialogResolutionModeFixed"],
            ],
            forbidden: french["ServerDialogProtocolRdpName"]);
    }

    [Fact]
    public void FeatureListDoesNotAdvertiseATcpOnlyMode()
    {
        string root = FindRepoRoot();

        foreach (string document in new[] { "docs/FEATURES.md", "docs/fr/FEATURES.md" })
        {
            string text = File.ReadAllText(Path.Combine(root, document));

            foreach (string claim in new[] { "TCP-only", "TCP only" })
            {
                Assert.False(
                    text.Contains(claim, StringComparison.OrdinalIgnoreCase),
                    $"{document} advertises a \"{claim}\" capability. RdpRedirectionOptions.cs "
                        + "states the Remote Desktop client exposes no way for an application to "
                        + "disable UDP transport; only the machine policy fClientDisableUDP does.");
            }

            // Positive control: the bullet that carried the claim must still describe the feature
            // that does exist, so deleting it cannot satisfy this test.
            Assert.Contains("UDP", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SecurityPageDoesNotCallTheRdpProbeRaceHarmless()
    {
        string root = FindRepoRoot();

        AssertCertificateSection(
            root,
            "docs/SECURITY.md",
            EnglishCertificateHeading,
            forbidden: "harmless",
            required: "is never compared to anything");

        AssertCertificateSection(
            root,
            "docs/fr/SECURITY.md",
            FrenchCertificateHeading,
            forbidden: "inoffensif",
            required: "n'est jamais comparé à quoi que ce soit");
    }

    [Fact]
    public void SecurityPageScopesTheRdpCertificateGateToTheEmbeddedPath()
    {
        string root = FindRepoRoot();

        // The claim under test, read from the source: the gate has call sites on the embedded
        // path only, so an external launch gets no Heimdall-side certificate check.
        string[] gateCallSites = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path)
                .Contains("RdpCertificateGate.", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;

        Assert.Equal(["EmbeddedRdpView.xaml.cs"], gateCallSites);

        AssertCertificateSection(
            root,
            "docs/SECURITY.md",
            EnglishCertificateHeading,
            forbidden: null,
            required: "mstsc.exe");

        AssertCertificateSection(
            root,
            "docs/fr/SECURITY.md",
            FrenchCertificateHeading,
            forbidden: null,
            required: "mstsc.exe");
    }

    private static void AssertNavigationParagraph(
        string root,
        string document,
        string marker,
        string[] required,
        string forbidden)
    {
        string paragraph = ParagraphContaining(Path.Combine(root, document), marker);

        Assert.False(
            paragraph.Contains(forbidden, StringComparison.Ordinal),
            $"{document} sends the reader to a \"{forbidden}\" tab. No tab carries that label - "
                + $"it is the protocol name in the picker. Paragraph: {paragraph}");

        foreach (string label in required)
        {
            Assert.True(
                paragraph.Contains(label, StringComparison.Ordinal),
                $"{document} does not name the \"{label}\" the user interface shows on this "
                    + $"path. Paragraph: {paragraph}");
        }
    }

    private static void AssertCertificateSection(
        string root,
        string document,
        string heading,
        string? forbidden,
        string required)
    {
        string section = SectionUnder(Path.Combine(root, document), heading);

        if (forbidden is not null)
        {
            Assert.False(
                section.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"{document} still calls the probe-versus-session race \"{forbidden}\". Nothing "
                    + "compares the session's certificate to the trust set, so a pool member the "
                    + "probe never landed on is accepted with no prompt and no record.");
        }

        Assert.True(
            section.Contains(required, StringComparison.Ordinal),
            $"{document} no longer states \"{required}\" in its RDP certificate section. The "
                + "section must say what the code does, not go quiet about it.");
    }

    private static string ParagraphContaining(string path, string marker)
    {
        string[] paragraphs = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.None);

        string? found = paragraphs.FirstOrDefault(
            paragraph => paragraph.Contains(marker, StringComparison.Ordinal));

        Assert.True(
            found is not null,
            $"{path} has no paragraph containing \"{marker}\"; the anchor this guard reads is gone.");

        return found!.Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string SectionUnder(string path, string heading)
    {
        string[] lines = File.ReadAllLines(path);
        int start = Array.FindIndex(
            lines,
            line => line.StartsWith(heading, StringComparison.Ordinal));

        Assert.True(start >= 0, $"{path} has no \"{heading}\" section.");

        int end = Array.FindIndex(
            lines,
            start + 1,
            line => line.StartsWith("### ", StringComparison.Ordinal));

        return string.Join(' ', lines[(start + 1)..(end < 0 ? lines.Length : end)]);
    }

    private static IEnumerable<string> BacktickedRepositoryPaths(string line)
    {
        foreach (string span in line.Split('`').Where((_, index) => index % 2 == 1))
        {
            string candidate = span.Trim();
            if (candidate.Length == 0
                || candidate.Contains(' ', StringComparison.Ordinal)
                || candidate.Contains('*', StringComparison.Ordinal)
                || candidate.Contains('<', StringComparison.Ordinal))
            {
                continue;
            }

            if (RepositoryPrefixes.Any(
                prefix => candidate.StartsWith(prefix, StringComparison.Ordinal)))
            {
                yield return candidate;
            }
        }
    }

    private static IReadOnlyDictionary<string, string> Locale(string root, string language)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "locales", $"{language}.json")));

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                values[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return values;
    }

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
}
