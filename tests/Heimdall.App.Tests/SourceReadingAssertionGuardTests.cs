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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The guard over the guards: no test may claim a production statement is present by anchoring on
/// a bare fragment of source text.
/// </summary>
/// <remarks>
/// <para>Four rounds closed such sites one at a time and each round left a twin behind, twice in
/// the same file and once in the same test. What those rounds were missing is not diligence, it is
/// a rule with a name: an anchor used to assert presence over production source must be an anchor
/// the same test carried through <see cref="ViewSource.IsStatementOfTheMethodBody"/>. A bare
/// <c>Contains</c>, a bare <c>IndexOf</c> and a regex between bare anchors all survive the
/// statement being folded behind a term that is false by construction.</para>
/// <para><b>How exemptions are declared, and why this shape.</b> Through
/// <c>bare-source-assertions.baseline.txt</c> beside this file, one line per site, in the shape
/// this repository already uses for <c>dead-locale-keys.baseline.txt</c>: a frozen list that may
/// only SHRINK, with a companion test that fails when an entry no longer describes anything. An
/// attribute was the alternative and was rejected: it would live on the test that is wrong, where
/// a reader sees a permission rather than a debt, and it would spread the population across 48
/// files so that nobody could count it. The baseline holds the population in one place, in scan
/// order, and shrinks. Its size is the finding: this defect class was never five sites.</para>
/// <para><b>Absence assertions are exempt by rule.</b> They invert the risk. Folding a statement
/// can only keep an absence assertion passing, and the only way to break one is to add the text it
/// forbids, so a bare anchor there cannot fail open the way a presence anchor does. The exemption
/// is counted rather than silent, and its own floor fails if the scan stops recognising them.</para>
/// <para><b>What this guard cannot establish.</b> Precisely what the predicate it demands cannot: a
/// whole-statement match settles that a statement is WRITTEN as a step of a body, never that it
/// RUNS, because the predicate walks past every conditional early return above it.</para>
/// </remarks>
public sealed class SourceReadingAssertionGuardTests
{
    private const string BaselineFile = "bare-source-assertions.baseline.txt";
    private const string ArtifactDirectory = "meta-art";
    private const string ArtifactFile = "bare-source-assertions.actual.txt";

    /// <summary>
    /// The floor each discovery rule must still reach, so a rule that stops finding anything fails
    /// under its own name instead of being hidden inside a total that other rules keep up.
    /// </summary>
    /// <remarks>
    /// <para>Each floor sits below the count measured when this guard was written, with room for
    /// the ordinary deletion of a test or two. A floor is not a ceiling: adding tests raises the
    /// count and nothing here objects. What it catches is a pattern that silently stops matching -
    /// a renamed helper, a reader entry point nobody added here - which would otherwise turn this
    /// whole guard green by finding nothing.</para>
    /// <para>The measured counts on the day this was frozen, in the same order: 828, 7836, 10, 51,
    /// 132, 1, 82, 9, 71, 17. One of them is worth naming: <c>member-contains</c> has a population
    /// of one in this repository, so its floor is a tripwire on a single site rather than on a
    /// population. Repairing that site is meant to take its floor row with it, in the same edit as
    /// its baseline line.</para>
    /// </remarks>
    public static TheoryData<string, int> DiscoveryFloors => new()
    {
        { SourceReadingScan.FilesScannedRule, 700 },
        { SourceReadingScan.TestMembersRule, 7000 },
        { SourceReadingScan.ViewSourceReaderRule, 8 },
        { SourceReadingScan.FileReadReaderRule, 40 },
        { SourceReadingScan.PresenceRule, 100 },
        { SourceReadingScan.MemberContainsRule, 1 },
        { SourceReadingScan.IndexOfRule, 60 },
        { SourceReadingScan.RegexRule, 6 },
        { SourceReadingScan.AbsenceExemptionRule, 50 },
        { SourceReadingScan.SanctionedRule, 12 },
    };

    /// <summary>The whole population, so a new bare anchor cannot arrive unannounced.</summary>
    [Fact]
    public void NoTestAssertsOverProductionSourceWithABareFragment()
    {
        SourceReadingScanResult scan = ScanRepository();
        HashSet<string> baseline = ReadBaseline();

        List<string> arrivals = scan.Findings
            .Select(finding => finding.BaselineLine)
            .Where(line => !baseline.Contains(line))
            .ToList();

        if (arrivals.Count > 0)
        {
            WriteArtifact(scan);
        }

        Assert.True(
            arrivals.Count == 0,
            $"{arrivals.Count} assertion(s) over production source anchor on a fragment the test "
                + "never carried through ViewSource.IsStatementOfTheMethodBody. Fold the statement "
                + "behind a term that is false by construction and each of these stays green while "
                + "the behaviour is gone. Carry the whole statement or condition through the "
                + "predicate, or add the line to " + BaselineFile + " with a reason:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, arrivals.Take(40))
                + Environment.NewLine
                + "The full list was written beside the build output as " + ArtifactFile + ".");
    }

    /// <summary>
    /// Without this the baseline only ever grows, and a repaired site keeps its permission.
    /// </summary>
    [Fact]
    public void TheBaselineHoldsNothingThatIsNoLongerFound()
    {
        SourceReadingScanResult scan = ScanRepository();
        HashSet<string> found = scan.Findings
            .Select(finding => finding.BaselineLine)
            .ToHashSet(StringComparer.Ordinal);

        List<string> stale = ReadBaseline()
            .Where(line => !found.Contains(line))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} baseline entries no longer describe anything - the assertion was "
                + "repaired, renamed or deleted. Delete these lines from " + BaselineFile + ":"
                + Environment.NewLine
                + string.Join(Environment.NewLine, stale.Take(40)));
    }

    /// <summary>A rule that stops finding anything must fail under its own name.</summary>
    [Theory]
    [MemberData(nameof(DiscoveryFloors))]
    public void EveryDiscoveryRuleStillReachesItsFloor(string rule, int floor)
    {
        SourceReadingScanResult scan = ScanRepository();
        int reached = scan.Count(rule);

        Assert.True(
            reached >= floor,
            $"The '{rule}' rule reached {reached} sites, below its floor of {floor}. A discovery "
                + "rule that stops matching makes this whole guard pass by finding nothing, and a "
                + "single total would hide it behind the rules that still work.");
    }

    /// <summary>Blanking is what every reading here stands on.</summary>
    [Fact]
    public void BlankingLeavesEveryScannedTestFileBalanced()
    {
        SourceReadingScanResult scan = ScanRepository();

        Assert.True(
            scan.UnbalancedFiles.Count == 0,
            "Blanking left these files with unbalanced braces, so the member split and every "
                + "anchor read out of them is meaningless:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, scan.UnbalancedFiles.Take(20)));
    }

    /// <summary>
    /// The positive control: the defect this guard exists for, in the shape it was found in.
    /// </summary>
    [Fact]
    public void ABareContainsOverAHandlerBodyIsReported()
    {
        SourceReadingScanResult scan = ScanFixture(
            "        string body = ViewSource.HandlerLogic(Member);"
            + "\n        Assert.Contains(\"TryReset();\", body, StringComparison.Ordinal);");

        BareSourceAssertion finding = Assert.Single(scan.Findings);
        Assert.Equal(SourceReadingScan.PresenceRule, finding.Rule);
        Assert.Equal("\"TryReset();\"", finding.Needle);
    }

    /// <summary>The same anchor, carried through the predicate, is what the rule asks for.</summary>
    [Fact]
    public void AnAnchorCarriedThroughThePredicateIsNotReported()
    {
        SourceReadingScanResult scan = ScanFixture(
            "        string body = ViewSource.HandlerLogic(Member);"
            + "\n        Assert.True(ViewSource.IsStatementOfTheMethodBody(body, \"TryReset();\"));"
            + "\n        int reset = body.IndexOf(\"TryReset();\", StringComparison.Ordinal);"
            + "\n        Assert.True(reset > 0);");

        Assert.Empty(scan.Findings);
        Assert.Equal(1, scan.Count(SourceReadingScan.SanctionedRule));
        Assert.Equal(1, scan.Count(SourceReadingScan.IndexOfRule));
    }

    /// <summary>
    /// The same defect written as a bare <c>Contains</c> on the source itself.
    /// </summary>
    /// <remarks>
    /// This rule has a population of one in the repository, so its floor is a tripwire on a single
    /// site. The control is what keeps the rule honest if that site is ever repaired away.
    /// </remarks>
    [Fact]
    public void ABareMemberContainsOverAHandlerBodyIsReported()
    {
        SourceReadingScanResult scan = ScanFixture(
            "        string body = ViewSource.HandlerLogic(Member);"
            + "\n        Assert.True(body.Contains(\"TryReset();\", StringComparison.Ordinal));");

        BareSourceAssertion finding = Assert.Single(scan.Findings);
        Assert.Equal(SourceReadingScan.MemberContainsRule, finding.Rule);
        Assert.Equal("\"TryReset();\"", finding.Needle);
    }

    /// <summary>The absence exemption, exercised rather than assumed.</summary>
    [Fact]
    public void AnAbsenceAssertionIsExemptAndCounted()
    {
        SourceReadingScanResult scan = ScanFixture(
            "        string body = ViewSource.HandlerLogic(Member);"
            + "\n        Assert.DoesNotContain(\"TryReset();\", body, StringComparison.Ordinal);");

        Assert.Empty(scan.Findings);
        Assert.Equal(1, scan.Count(SourceReadingScan.AbsenceExemptionRule));
    }

    /// <summary>A read that is not production source is not this guard's business.</summary>
    [Fact]
    public void AReadThatIsNotProductionSourceIsNotInTheDomain()
    {
        SourceReadingScanResult scan = SourceReadingScan.Scan(new[]
        {
            ("tests/Fixture.cs",
                "namespace Fixture;\n\npublic sealed class Fixture\n{\n"
                + "    [Fact]\n    public void ATest()\n    {\n"
                + "        string log = File.ReadAllText(_sandbox.LogPath);\n"
                + "        Assert.Contains(\"[INFO]\", log, StringComparison.Ordinal);\n"
                + "    }\n}\n"),
        });

        Assert.Empty(scan.Findings);
        Assert.Equal(0, scan.Count(SourceReadingScan.PresenceRule));
    }

    /// <summary>
    /// The scan reads its own controls as data, not as code.
    /// </summary>
    /// <remarks>
    /// The fixtures above are string literals in this file, and the scan blanks literals before it
    /// reads structure. Without that, this file would report its own controls as violations and
    /// the baseline would grow to cover them, which is how a guard starts describing itself.
    /// </remarks>
    [Fact]
    public void ThisGuardsOwnFileReportsNothing()
    {
        SourceReadingScanResult scan = ScanRepository();

        Assert.DoesNotContain(
            scan.Findings,
            finding => finding.TestFile.EndsWith(
                "SourceReadingAssertionGuardTests.cs", StringComparison.Ordinal));
    }

    private static SourceReadingScanResult ScanFixture(string testBody) =>
        SourceReadingScan.Scan(new[] { ("tests/Fixture.cs", FixtureClass(testBody)) });

    /// <summary>
    /// A whole test class around one body, so the scan sees what it sees in the repository: a
    /// production path, a member split and an attribute.
    /// </summary>
    private static string FixtureClass(string testBody) =>
        "namespace Fixture;\n\npublic sealed class Fixture\n{\n"
        + "    private const string Path = @\"src\\Heimdall.App\\Views\\View.xaml.cs\";\n\n"
        + "    private static string ReadSource() => File.ReadAllText(Path);\n\n"
        + "    [Fact]\n    public void ATest()\n    {\n"
        + testBody
        + "\n    }\n}\n";

    private static readonly Lazy<SourceReadingScanResult> s_repository = new(ScanRepositoryCore);

    /// <summary>The one repository pass every test here shares.</summary>
    private static SourceReadingScanResult ScanRepository() => s_repository.Value;

    private static SourceReadingScanResult ScanRepositoryCore()
    {
        string root = ViewSource.RepoRoot();
        string tests = Path.Combine(root, "tests");

        List<(string Path, string Text)> files = Directory
            .EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, tests))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Relative(root, path), File.ReadAllText(path)))
            .ToList();

        Assert.True(
            files.Count > 0,
            $"No test sources were found under {tests}, so this guard measured nothing.");

        return SourceReadingScan.Scan(files);
    }

    private static bool IsBuildOutput(string path, string tests)
    {
        string relative = path[tests.Length..];
        return relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static HashSet<string> ReadBaseline()
    {
        string path = Path.Combine(
            ViewSource.RepoRoot(), "tests", "Heimdall.App.Tests", BaselineFile);

        Assert.True(File.Exists(path), $"The exemption baseline is missing: {path}");

        return File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes the whole measured population beside the build output when the guard fails.
    /// </summary>
    /// <remarks>
    /// A failure message truncates, and the first thing anyone repairing this needs is the exact
    /// lines, in the exact shape the baseline parses.
    /// </remarks>
    private static void WriteArtifact(SourceReadingScanResult scan)
    {
        try
        {
            string directory = Path.Combine(ViewSource.RepoRoot(), "obj", ArtifactDirectory);
            _ = Directory.CreateDirectory(directory);

            IEnumerable<string> counters = scan.Counters
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"# {entry.Key} = {entry.Value}");

            File.WriteAllLines(
                Path.Combine(directory, ArtifactFile),
                counters.Concat(scan.Findings.Select(finding => finding.BaselineLine)));
        }
        catch (IOException)
        {
            // A diagnostic that cannot be written must not replace the failure it describes.
        }
    }
}
