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

using System.IO;
using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// Keeps docs/CI_FLAKY_TESTS.md (and its French mirror) in step with the tests that are really
/// tagged <c>[Trait("Category", "CIUnstable")]</c>, in both directions.
/// </summary>
/// <remarks>
/// <para>The page says it lists every site, and for a while it did not: ten tests of the
/// updater execution suite and the WinRM launch guard suite carried the tag and were absent
/// from it. A tagged test leaves the blocking CI pass, so an unlisted one is a test that
/// stopped gating without a trace anyone reads. The page is the only register of what the
/// informational lane holds.</para>
/// <para>The scan reads the test sources rather than reflecting over the assemblies, because
/// the assemblies of other test projects are not loaded here. It is therefore bounded by the
/// forms it recognises: a trait attribute on a line of its own, above a class or a method.
/// Any other appearance of the quoted category name is counted and refused, so a new way of
/// writing the tag cannot slip past by looking different.</para>
/// </remarks>
public sealed class CiUnstableInventoryGuardTests
{
    private const string EnglishDocument = "docs/CI_FLAKY_TESTS.md";
    private const string FrenchDocument = "docs/fr/CI_FLAKY_TESTS.md";
    private const string BuildScript = "Build.ps1";
    private const string TestsDirectory = "tests";
    private const string SolutionFile = "Heimdall.slnx";
    private const string ProjectFileFilter = "*.csproj";
    private const string SourceFileFilter = "*.cs";
    private const string QuotedCategoryName = "\"CIUnstable\"";
    private const string ThisFileName = "CiUnstableInventoryGuardTests.cs";

    /// <summary>Deepest-level proof the scan descends: tests/&lt;project&gt;/&lt;folder&gt;/&lt;file&gt;.</summary>
    private const int NestedSourceMinimumSegments = 4;

    private static readonly Regex TraitAttribute = new(
        @"^\[\s*(?:[\w, ]+,\s*)?Trait\(\s*""Category""\s*,\s*""CIUnstable""\s*\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex DeclarationStart = new(
        @"^(?:public|internal|private|protected|static|sealed|abstract|partial|async|file|unsafe)\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex TypeDeclaration = new(
        @"\b(?:class|record|struct)\s+(?<name>\w+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex MethodDeclaration = new(
        @"(?<name>\w+)\s*(?:<[^>(]*>)?\s*\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex DocumentCountsEnglish = new(
        @"There are (?<sites>\d+)\s+`\[Trait[^:]*:\s*(?<methods>\d+)\s+on\s+individual\s+test\s+methods\s+and\s+(?<classes>\d+)\s+on\s+whole\s+classes",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex DocumentCountsFrench = new(
        @"comporte (?<sites>\d+) emplacements[^:]*:\s*(?<methods>\d+)\s+sur des m\S+thodes[^0-9]*(?<classes>\d+)\s+sur des classes",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private sealed record Site(string Key, string File, bool IsClass);

    private sealed record Scan(IReadOnlyList<Site> Sites, int QuotedMentions);

    [Fact]
    public void EveryCiUnstableSite_IsListedInTheEnglishInventory_AndNothingElseIs()
    {
        AssertInventoryMatchesSources(EnglishDocument);
    }

    [Fact]
    public void EveryCiUnstableSite_IsListedInTheFrenchInventory_AndNothingElseIs()
    {
        AssertInventoryMatchesSources(FrenchDocument);
    }

    [Fact]
    public void TheStatedSiteCounts_MatchTheSources_InBothLanguages()
    {
        IReadOnlyList<Site> sites = ScanTestSources().Sites;
        int classes = sites.Count(site => site.IsClass);
        int methods = sites.Count - classes;

        foreach ((string document, Regex pattern) in new[]
        {
            (EnglishDocument, DocumentCountsEnglish),
            (FrenchDocument, DocumentCountsFrench),
        })
        {
            Match match = pattern.Match(ReadRepositoryFile(document));
            Assert.True(match.Success, $"{document} no longer states how many CIUnstable sites there are.");
            Assert.Equal(sites.Count, int.Parse(match.Groups["sites"].Value));
            Assert.Equal(methods, int.Parse(match.Groups["methods"].Value));
            Assert.Equal(classes, int.Parse(match.Groups["classes"].Value));
        }
    }

    /// <summary>
    /// A scan that found nothing would also "match" an inventory emptied by mistake, and a scan
    /// that missed a project would match a document that omits it. Both are refused by checking
    /// what the scan covered, not only what it found.
    /// </summary>
    [Fact]
    public void TheScan_ReachesEveryTestProject_AndADirectoryBelowIt()
    {
        string root = RepositoryRoot();
        string testsRoot = Path.Combine(root, TestsDirectory);
        List<string> scanned = EnumerateTestSources(root).Select(path => RelativePath(root, path)).ToList();

        string[] projectDirectories = Directory
            .EnumerateFiles(testsRoot, ProjectFileFilter, SearchOption.AllDirectories)
            .Select(path => Path.GetDirectoryName(path)!)
            .Where(directory => !IsBuildOutput(directory))
            .Select(directory => RelativePath(root, directory))
            .ToArray();

        Assert.True(projectDirectories.Length >= 7, $"Found only {projectDirectories.Length} test project(s) under {TestsDirectory}.");
        foreach (string project in projectDirectories)
        {
            Assert.True(
                scanned.Any(path => path.StartsWith(project + "/", StringComparison.Ordinal)),
                $"The CIUnstable scan visited no source file of {project}.");
        }

        Assert.Contains(
            scanned,
            path => path.Split('/').Length >= NestedSourceMinimumSegments);
        Assert.Contains(
            ScanTestSources().Sites,
            site => site.File.Split('/').Length >= NestedSourceMinimumSegments);
    }

    /// <summary>
    /// The scanner itself, on text whose answer is known: a class tag, a method tag behind other
    /// attributes, a tag split over a stacked attribute list, and three things that must NOT
    /// count (a comment, another category, a quoted mention in code).
    /// </summary>
    [Fact]
    public void TheScanner_FindsClassAndMethodTags_AndNothingThatOnlyLooksLikeOne()
    {
        string[] lines =
        [
            "namespace Fixture;",
            "",
            "[Trait(\"Category\", \"CIUnstable\")]",
            "public sealed class TaggedClass",
            "{",
            "    // [Trait(\"Category\", \"CIUnstable\")]",
            "    [Trait(\"Category\", \"RequiresDesktop\")]",
            "    [Fact]",
            "    public void OtherCategory() { }",
            "",
            "    private sealed class Nested",
            "    {",
            "        public void Helper() { }",
            "    }",
            "",
            "    [Theory]",
            "    [Trait(\"Category\", \"CIUnstable\")]",
            "    [InlineData(1,",
            "        2)]",
            "    public async Task<int> TaggedMethod(int a, int b)",
            "    {",
            "        return a + b;",
            "    }",
            "",
            "    private const string Mention = \"CIUnstable\";",
            "}",
        ];

        Scan scan = ScanText(string.Join("\n", lines), "tests/Fixture/Fixture.cs");

        Assert.Equal(
            new[] { "TaggedClass", "TaggedClass.TaggedMethod" },
            scan.Sites.Select(site => site.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.True(scan.Sites.Single(site => site.Key == "TaggedClass").IsClass);
        Assert.False(scan.Sites.Single(site => site.Key == "TaggedClass.TaggedMethod").IsClass);
        Assert.True(
            scan.QuotedMentions > scan.Sites.Count,
            "The quoted mention in code must be counted, so that the real scan can refuse it.");
    }

    /// <summary>
    /// The guard recognises one spelling. A second spelling of the same category (a constant, a
    /// custom attribute carrying the quoted name) is a tag the inventory comparison cannot see.
    /// </summary>
    [Fact]
    public void EveryQuotedMentionOfTheCategory_IsASiteTheScanRecognises()
    {
        Scan scan = ScanTestSources();

        Assert.Equal(scan.Sites.Count, scan.QuotedMentions);
    }

    /// <summary>
    /// The page tells its reader that the tagged tests still run on the release box. That holds
    /// only while the build script's test run carries no category filter.
    /// </summary>
    [Fact]
    public void TheReleaseBuildTestRun_CarriesNoCategoryFilter()
    {
        string[] testRuns = ReadRepositoryFile(BuildScript)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("dotnet test", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(testRuns);
        Assert.All(testRuns, run => Assert.DoesNotContain("--filter", run, StringComparison.Ordinal));
    }

    private static void AssertInventoryMatchesSources(string document)
    {
        IReadOnlyList<Site> sites = ScanTestSources().Sites;
        Assert.NotEmpty(sites);

        Dictionary<string, string> listed = ReadInventory(document);
        Dictionary<string, string> expected = sites.ToDictionary(site => site.Key, site => site.File, StringComparer.Ordinal);

        string[] unlisted = expected.Keys.Except(listed.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] unknown = listed.Keys.Except(expected.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] wrongFile = expected
            .Where(pair => listed.TryGetValue(pair.Key, out string? file) && file != pair.Value)
            .Select(pair => $"{pair.Key}: source {pair.Value}, {document} says {listed[pair.Key]}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(unlisted.Length == 0, $"Tagged CIUnstable in the sources, missing from {document}: {string.Join(", ", unlisted)}");
        Assert.True(unknown.Length == 0, $"Listed in {document}, not tagged CIUnstable in the sources: {string.Join(", ", unknown)}");
        Assert.True(wrongFile.Length == 0, $"{document} names the wrong file for: {string.Join("; ", wrongFile)}");
    }

    /// <summary>Reads the inventory table: rows whose second cell is a backticked path under tests/.</summary>
    private static Dictionary<string, string> ReadInventory(string document)
    {
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in ReadRepositoryFile(document).Split('\n'))
        {
            string[] cells = line.TrimEnd('\r').Split('|');
            if (cells.Length < 4 || !cells[2].TrimStart().StartsWith("`" + TestsDirectory + "/", StringComparison.Ordinal))
            {
                continue;
            }

            Match key = Regex.Match(cells[1], "`(?<value>[^`]+)`", RegexOptions.CultureInvariant);
            Match file = Regex.Match(cells[2], "`(?<value>[^`]+)`", RegexOptions.CultureInvariant);
            Assert.True(key.Success && file.Success, $"Unreadable inventory row in {document}: {line}");
            Assert.True(
                listed.TryAdd(key.Groups["value"].Value, file.Groups["value"].Value),
                $"{document} lists {key.Groups["value"].Value} twice.");
        }

        Assert.NotEmpty(listed);
        return listed;
    }

    private static Scan ScanTestSources()
    {
        string root = RepositoryRoot();
        var sites = new List<Site>();
        int mentions = 0;
        foreach (string path in EnumerateTestSources(root))
        {
            if (string.Equals(Path.GetFileName(path), ThisFileName, StringComparison.Ordinal))
            {
                continue;
            }

            Scan scan = ScanText(File.ReadAllText(path), RelativePath(root, path));
            sites.AddRange(scan.Sites);
            mentions += scan.QuotedMentions;
        }

        return new Scan(sites, mentions);
    }

    private static Scan ScanText(string text, string relativePath)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sites = new List<Site>();
        var typeStack = new List<(int Indent, string Name)>();
        int mentions = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            string trimmed = lines[index].Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.Contains(QuotedCategoryName, StringComparison.Ordinal))
            {
                mentions++;
            }

            if (!TraitAttribute.IsMatch(trimmed))
            {
                RememberType(lines[index], trimmed, typeStack);
                continue;
            }

            int declarationIndex = NextDeclarationIndex(lines, index + 1);
            if (declarationIndex < 0)
            {
                continue;
            }

            string declaration = lines[declarationIndex].Trim();
            Match type = TypeDeclaration.Match(declaration);
            if (type.Success)
            {
                sites.Add(new Site(type.Groups["name"].Value, relativePath, IsClass: true));
                continue;
            }

            Match method = MethodDeclaration.Match(declaration);
            if (!method.Success)
            {
                continue;
            }

            int indent = IndentOf(lines[declarationIndex]);
            string owner = typeStack.LastOrDefault(entry => entry.Indent < indent).Name ?? string.Empty;
            sites.Add(new Site($"{owner}.{method.Groups["name"].Value}", relativePath, IsClass: false));
        }

        return new Scan(sites, mentions);
    }

    private static void RememberType(string line, string trimmed, List<(int Indent, string Name)> typeStack)
    {
        if (!DeclarationStart.IsMatch(trimmed))
        {
            return;
        }

        Match type = TypeDeclaration.Match(trimmed);
        if (!type.Success)
        {
            return;
        }

        int indent = IndentOf(line);
        typeStack.RemoveAll(entry => entry.Indent >= indent);
        typeStack.Add((indent, type.Groups["name"].Value));
    }

    /// <summary>The first line after an attribute that starts a declaration, past stacked attributes and comments.</summary>
    private static int NextDeclarationIndex(string[] lines, int from)
    {
        for (int index = from; index < lines.Length; index++)
        {
            string trimmed = lines[index].Trim();
            if (DeclarationStart.IsMatch(trimmed))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    private static IEnumerable<string> EnumerateTestSources(string root) =>
        Directory
            .EnumerateFiles(Path.Combine(root, TestsDirectory), SourceFileFilter, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));

    private static bool IsBuildOutput(string path)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string ReadRepositoryFile(string relativePath)
    {
        string full = Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Expected file not found: {full}");
        return File.ReadAllText(full);
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, SolutionFile)))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing {SolutionFile} from test binary directory: {AppContext.BaseDirectory}");
    }
}
