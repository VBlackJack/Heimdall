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
/// Fails when a shell tab is named by a bare string literal instead of through
/// <c>ShellTab</c>.
/// </summary>
/// <remarks>
/// The identifiers were repeated as literals at 27 sites across the shell view model and the main
/// window, every one of them compared with <see cref="StringComparison.Ordinal"/>. A typo in any
/// of them produced a tab that no comparison matched, silently, with nothing to fail. Naming them
/// once only helps while nobody writes the twenty-eighth literal, so that is what this refuses.
/// </remarks>
public sealed class ShellTabLiteralGuardTests
{
    private const string AppProjectRelativePath = "src/Heimdall.App";

    /// <summary>
    /// The file that is allowed to spell the identifiers: it defines them.
    /// </summary>
    private const string IdentifiersFileName = "ShellTab.cs";

    private static readonly Regex TabLiteral = new(
        "\"(?:Sessions|Tunnels|Scheduled|Settings|Tools|About)\"",
        RegexOptions.CultureInvariant);

    [Fact]
    public void NoShellCodeNamesATabWithABareLiteral()
    {
        string repoRoot = FindRepoRoot();
        string appRoot = Path.Combine(
            repoRoot,
            AppProjectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(Directory.Exists(appRoot), $"Application project not found: {appRoot}");

        string[] sources = Directory.GetFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, appRoot))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        // A sweep over an empty corpus passes for the wrong reason.
        Assert.NotEmpty(sources);

        List<string> violations = [];
        foreach (string source in sources)
        {
            if (Path.GetFileName(source) == IdentifiersFileName)
            {
                continue;
            }

            string[] lines = File.ReadAllLines(source);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];

                // Prose may name a tab: a comment that mentions the Tools page is documentation,
                // not an identifier the shell compares against.
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (TabLiteral.IsMatch(line))
                {
                    string relativePath = Path.GetRelativePath(repoRoot, source)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    violations.Add($"  {relativePath}:{index + 1} - {line.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "A shell tab is named by a bare string literal. Every comparison against it is "
            + "ordinal, so a typo yields a tab nothing matches and nothing reports. Use the "
            + $"constants in {IdentifiersFileName}:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TheGuardRecognisesTheShapesItClaimsToPolice()
    {
        // Non-vacuity of the pattern, checked without waiting for a regression.
        Assert.Matches(TabLiteral, "SwitchToTab(\"Sessions\");");
        Assert.Matches(TabLiteral, "if (vm.SelectedTab != \"Settings\") return true;");
        Assert.Matches(TabLiteral, "string.Equals(tabName, \"Tools\", StringComparison.Ordinal)");

        // And the corrected forms it must keep allowing.
        Assert.DoesNotMatch(TabLiteral, "SwitchToTab(ShellTab.Sessions);");
        Assert.DoesNotMatch(TabLiteral, "string.Equals(tabName, ShellTab.Tools, StringComparison.Ordinal)");

        // Neighbouring words are not tab identifiers and must not be swept up.
        Assert.DoesNotMatch(TabLiteral, "var x = \"SessionsPanel\";");
        Assert.DoesNotMatch(TabLiteral, "Log(\"About to connect\");");
    }

    [Fact]
    public void TheIdentifiersFileStillExists_SoTheExemptionIsNotHidingAnEmptySweep()
    {
        string repoRoot = FindRepoRoot();
        string[] identifiers = Directory.GetFiles(
            Path.Combine(repoRoot, AppProjectRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            IdentifiersFileName,
            SearchOption.AllDirectories);

        // The sweep exempts one file by name. If that file were renamed or removed, the exemption
        // would silently cover nothing while the guard still reported success.
        string identifiersPath = Assert.Single(identifiers);
        Assert.Matches(TabLiteral, File.ReadAllText(identifiersPath));
    }

    private static bool IsBuildOutput(string path, string appRoot)
    {
        string relative = Path.GetRelativePath(appRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
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
