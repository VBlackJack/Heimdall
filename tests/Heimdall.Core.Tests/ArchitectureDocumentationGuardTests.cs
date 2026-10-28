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
using System.Text.RegularExpressions;

namespace Heimdall.Core.Tests;

/// <summary>
/// Keeps <c>docs/ARCHITECTURE.md</c> and its French mirror honest about the source files they name.
/// </summary>
/// <remarks>
/// <para>The architecture document names classes and the files that hold them. A partial class that
/// was folded back into its parent kept being described for months, with its line count, because
/// nothing compared the prose to the tree. This guard resolves every backticked <c>*.cs</c> name in
/// both languages against the files of the repository, and refuses the parenthesized line counts
/// that go stale on the next commit.</para>
/// <para>The two languages are held to the same set of file names: a mirror that names a file the
/// English version does not, or the reverse, has drifted.</para>
/// </remarks>
public sealed class ArchitectureDocumentationGuardTests
{
    private static readonly string[] Documents = ["docs/ARCHITECTURE.md", "docs/fr/ARCHITECTURE.md"];

    /// <summary>A backticked token that ends in <c>.cs</c>: a bare file name or a path.</summary>
    private static readonly Regex SourceFileReference = new(@"`(?<name>[^`\s]*\.cs)`", RegexOptions.Compiled);

    /// <summary>A size in lines written in parentheses, as in <c>(647 lines)</c> or <c>(647 lignes)</c>.</summary>
    private static readonly Regex ParenthesizedLineCount = new(
        @"\(\d[\d,.   ]*\s(?:lines|lignes)\)", RegexOptions.Compiled);

    /// <summary>Lower bound on the references one document holds; a regex that matched nothing would pass.</summary>
    private const int MinimumReferencesPerDocument = 10;

    [Fact]
    public void EveryBacktickedSourceFileExists()
    {
        string root = FindRepoRoot();
        List<string> repositoryFiles = RepositorySourceFiles(root);
        HashSet<string> names = repositoryFiles
            .Select(path => Path.GetFileName(path))
            .ToHashSet(System.StringComparer.Ordinal);

        List<string> missing = [];
        foreach (string document in Documents)
        {
            foreach (string reference in References(root, document))
            {
                bool found = reference.Contains('/')
                    ? repositoryFiles.Any(path => path.EndsWith("/" + reference, System.StringComparison.Ordinal))
                    : names.Contains(reference);
                if (!found)
                    missing.Add($"{document} names {reference}, which is not a file of the repository");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void BothLanguagesNameTheSameSourceFiles()
    {
        string root = FindRepoRoot();
        List<string>[] sets = [.. Documents.Select(document => References(root, document))];

        foreach (string document in Documents)
        {
            Assert.True(
                References(root, document).Count >= MinimumReferencesPerDocument,
                $"{document} holds fewer than {MinimumReferencesPerDocument} source file references, so the scan read nothing");
        }

        Assert.Equal(sets[0].Distinct().Order(), sets[1].Distinct().Order());
    }

    [Fact]
    public void NoDocumentWritesALineCountInParentheses()
    {
        string root = FindRepoRoot();
        List<string> found = [];

        foreach (string document in Documents)
        {
            string[] lines = File.ReadAllLines(Path.Combine(root, document));
            for (int index = 0; index < lines.Length; index++)
            {
                foreach (Match match in ParenthesizedLineCount.Matches(lines[index]))
                    found.Add($"{document}:{index + 1} writes the class size {match.Value}");
            }
        }

        Assert.True(found.Count == 0, string.Join("\n", found));
    }

    /// <summary>The detector fires on the shapes it exists for, in both languages.</summary>
    [Theory]
    [InlineData("`ContextMenuFactory` (647 lines) - builds")]
    [InlineData("`ContextMenuFactory` (647 lignes) - construit")]
    [InlineData("`MainWindow.Localization.cs` (1,519 lines)")]
    public void TheLineCountDetectorRecognizesTheShapesItRefuses(string text)
        => Assert.Matches(ParenthesizedLineCount, text);

    private static List<string> References(string root, string document)
        => [.. SourceFileReference.Matches(File.ReadAllText(Path.Combine(root, document)))
            .Select(match => match.Groups["name"].Value)];

    private static List<string> RepositorySourceFiles(string root)
        => [.. new[] { "src", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.Contains("/bin/", System.StringComparison.Ordinal) && !path.Contains("/obj/", System.StringComparison.Ordinal))];

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
