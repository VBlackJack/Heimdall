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
using Heimdall.App.ViewModels;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Which pane the shell means by "the secondary one", and that every consumer asks the same
/// question.
/// </summary>
/// <remarks>
/// The resolution had five consumers and four of them recomputed the expression independently.
/// That is not duplication of code so much as duplication of a DEFINITION: four places each free
/// to disagree about which pane the user is pointing at, with nothing to notice if one drifted.
/// </remarks>
public sealed class SecondaryPaneResolutionTests
{
    [Fact]
    public void ASessionThatIsNotSplit_HasNoSecondaryPane()
    {
        SessionTabViewModel session = new() { RootContent = Pane("only") };

        Assert.Null(session.SecondaryPaneOrNull);
    }

    [Fact]
    public void ASimpleSplit_ResolvesTheSecondPane()
    {
        SessionTabViewModel session = new()
        {
            RootContent = new SplitContainerModel
            {
                First = Pane("primary"),
                Second = Pane("secondary"),
            },
        };

        Assert.Equal("secondary", session.SecondaryPaneOrNull?.PaneId);
    }

    [Fact]
    public void ANestedSecondarySubtree_ResolvesItsFirstLeafInDepth()
    {
        // The secondary side is itself a split. The pane a user points at is the first leaf DOWN
        // that subtree, not the container and not the primary side of the outer split.
        SessionTabViewModel session = new()
        {
            RootContent = new SplitContainerModel
            {
                First = Pane("primary"),
                Second = new SplitContainerModel
                {
                    First = new SplitContainerModel
                    {
                        First = Pane("nested-deep"),
                        Second = Pane("nested-deep-sibling"),
                    },
                    Second = Pane("nested-second"),
                },
            },
        };

        Assert.Equal("nested-deep", session.SecondaryPaneOrNull?.PaneId);
    }

    [Fact]
    public void TheSecondaryShimProperties_ReadTheSameResolution()
    {
        SessionTabViewModel session = new()
        {
            RootContent = new SplitContainerModel
            {
                First = Pane("primary", "primary title"),
                Second = Pane("secondary", "secondary title"),
            },
        };

        // The shims were already built on this resolution; pinning one of them keeps the promotion
        // from quietly changing what they report.
        Assert.Equal("secondary title", session.SecondaryTitle);
        Assert.Equal(session.SecondaryPaneOrNull?.Title, session.SecondaryTitle);
    }

    [Theory]
    [InlineData("ViewModels/MainViewModel.cs", "CloseSecondaryPane")]
    [InlineData("ViewModels/MainViewModel.cs", "ReconnectSecondaryAsync")]
    [InlineData("Services/SessionWindowService.cs", "DetachSecondaryToFloatingWindow")]
    [InlineData("Services/SessionWindowService.cs", "UnsplitSession")]
    public void EveryConsumerAsksTheSessionInsteadOfRecomputing(string relativePath, string methodName)
    {
        string body = ExtractMethodBody(ReadAppSource(relativePath), methodName);

        Assert.Contains("SecondaryPaneOrNull", body, StringComparison.Ordinal);

        // The point of the lot: a consumer that still spells the expression out has its own
        // definition of the secondary pane, and the four could drift apart without a single test
        // noticing.
        Assert.DoesNotContain("FirstLeaf", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScannerFindsARecompositionSplitAcrossLines()
    {
        // The reason the scan is not line-by-line. An earlier revision required both tokens on one
        // line, so this exact shape walked straight past it and the repo-wide claim was false.
        Assert.Equal(
            1,
            SecondaryResolutionScanner.Count(
                "var pane = SplitTreeHelper.FirstLeaf(\r\n            container.Second);"));

        Assert.Equal(
            1,
            SecondaryResolutionScanner.Count("SplitTreeHelper.FirstLeaf(  c.Second )"));
    }

    [Fact]
    public void TheScannerCountsEveryResolution_NotJustWhetherOneExists()
    {
        // Two definitions inside a single file. The sweep aggregates counts rather than files
        // precisely because of this shape: an earlier revision added one entry per file and then
        // asserted one entry, so a second definition beside the first passed unseen.
        const string twoInOneFile =
            "internal SessionPaneModel? SecondaryPaneOrNull =>\r\n"
            + "    RootContent is SplitContainerModel c ? SplitTreeHelper.FirstLeaf(c.Second) : null;\r\n"
            + "\r\n"
            + "private SessionPaneModel? OtherSecondary =>\r\n"
            + "    RootContent is SplitContainerModel d ? SplitTreeHelper.FirstLeaf(\r\n"
            + "        d.Second) : null;";

        Assert.Equal(2, SecondaryResolutionScanner.Count(twoInOneFile));
    }

    [Theory]
    [InlineData("SplitTreeHelper.FirstLeaf(sourceContent)")]
    [InlineData("SplitTreeHelper.FirstLeaf(RootContent) ?? _emptyPane")]
    [InlineData("var a = SplitTreeHelper.FirstLeaf(x);\r\nvar b = container.Second;")]
    public void TheScannerIgnoresFirstLeafOverAnArbitrarySubtree(string source)
    {
        // FirstLeaf over some other subtree is a different question and stays allowed - the two
        // SplitService call sites and the PRIMARY pane resolution both look like this. The last
        // case matters most: two unrelated statements must not combine into a false positive, so
        // the match is bounded inside the argument list.
        Assert.Equal(0, SecondaryResolutionScanner.Count(source));
    }

    [Fact]
    public void TheResolutionIsDeclaredExactlyOnce()
    {
        string root = FindRepositoryRoot();
        string[] sources = Directory
            .GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sources);

        // The invariant is on the TOTAL number of resolutions, not on the number of files holding
        // one. Counting files lets a second definition sit beside the first in the same file and
        // still report a single entry, which is exactly what an earlier revision of this test did.
        int totalRecompositions = 0;
        List<string> perFile = [];
        foreach (string source in sources)
        {
            // Whole file, not line by line: the resolution can be recomposed across a line break
            // and stay exactly as much of a second definition as the one-line form.
            int count = SecondaryResolutionScanner.Count(File.ReadAllText(source));
            if (count > 0)
            {
                totalRecompositions += count;
                perFile.Add(
                    $"  {Path.GetRelativePath(root, source).Replace(Path.DirectorySeparatorChar, '/')}"
                    + $" ({count})");
            }
        }

        Assert.True(
            totalRecompositions == 1,
            $"The secondary pane must be resolved exactly once across src/, found {totalRecompositions}. "
            + "Every consumer must ask SessionTabViewModel.SecondaryPaneOrNull instead:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, perFile));
        Assert.Contains("SessionTabViewModel.cs", Assert.Single(perFile), StringComparison.Ordinal);
    }

    private static SessionPaneModel Pane(string paneId, string? title = null)
    {
        return new SessionPaneModel { PaneId = paneId, Title = title ?? paneId };
    }

    /// <summary>
    /// Finds every place that resolves the first leaf of a <c>Second</c> subtree.
    /// </summary>
    /// <remarks>
    /// Extracted so the detection can be tested on synthetic input instead of only on the tree it
    /// polices. Its first revision compared line by line, which a call recomposed across a line
    /// break walked straight past - so the sweep reported one declaration while a second could sit
    /// beside it unseen.
    /// </remarks>
    internal static class SecondaryResolutionScanner
    {
        /// <summary>
        /// <c>FirstLeaf(</c> whose argument mentions <c>.Second</c>, over any amount of
        /// whitespace including newlines.
        /// </summary>
        /// <remarks>
        /// <c>[^()]*?</c> keeps the match inside one argument list, so two unrelated statements -
        /// a <c>FirstLeaf</c> here and a <c>.Second</c> further down - cannot combine into a false
        /// positive.
        /// </remarks>
        private static readonly Regex Recomposition = new(
            @"FirstLeaf\s*\(\s*[^()]*?\.Second\b",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        internal static int Count(string content)
        {
            ArgumentNullException.ThrowIfNull(content);
            return Recomposition.Matches(content).Count;
        }
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        System.Text.RegularExpressions.Match declaration = System.Text.RegularExpressions.Regex.Match(
            source,
            $@"(?:void|Task|Task<[^>\r\n]+>)\s+{System.Text.RegularExpressions.Regex.Escape(methodName)}\s*\(");
        Assert.True(declaration.Success, $"Declaration not found: {methodName}");

        int open = source.IndexOf('{', declaration.Index);
        Assert.True(open > 0, $"Body not found for {methodName}");

        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(index + 1)];
                }
            }
        }

        Assert.Fail($"Unbalanced braces for {methodName}");
        return string.Empty;
    }

    private static string ReadAppSource(string relativePath)
    {
        string full = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Heimdall.App",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            "Cannot find repository root containing Heimdall.slnx from test binary directory: "
            + AppContext.BaseDirectory);
    }
}
