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
    public void TheResolutionIsDeclaredExactlyOnce()
    {
        string root = FindRepositoryRoot();
        string[] sources = Directory
            .GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sources);

        List<string> recomputations = [];
        foreach (string source in sources)
        {
            string[] lines = File.ReadAllLines(source);
            for (int index = 0; index < lines.Length; index++)
            {
                // FirstLeaf over an arbitrary subtree is a different question and stays allowed;
                // only "the first leaf of a Second" is this definition.
                if (lines[index].Contains("FirstLeaf(", StringComparison.Ordinal)
                    && lines[index].Contains(".Second", StringComparison.Ordinal))
                {
                    recomputations.Add(
                        $"  {Path.GetRelativePath(root, source).Replace(Path.DirectorySeparatorChar, '/')}"
                        + $":{index + 1} - {lines[index].Trim()}");
                }
            }
        }

        Assert.True(
            recomputations.Count == 1,
            "The secondary pane must be resolved in exactly one place, and every consumer must ask "
            + "for it there:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, recomputations));
        Assert.Contains("SessionTabViewModel.cs", recomputations[0], StringComparison.Ordinal);
    }

    private static SessionPaneModel Pane(string paneId, string? title = null)
    {
        return new SessionPaneModel { PaneId = paneId, Title = title ?? paneId };
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
