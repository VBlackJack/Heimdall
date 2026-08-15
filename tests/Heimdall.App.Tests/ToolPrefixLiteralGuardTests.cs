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

namespace Heimdall.App.Tests;

/// <summary>
/// Fails when the tool-type prefix is spelled as a literal outside the type that owns it.
/// </summary>
/// <remarks>
/// The literal previously appeared at 28 sites across 17 files, and the four value converters
/// compared it with <see cref="StringComparison.Ordinal"/> while every logic site ignored case.
/// That divergence was latent rather than live - each producer emits the prefix upper-cased - but
/// it was one careless producer away from a tool tab that renders with the wrong icon while every
/// behavioural path still treats it as a tool. One spelling and one comparison remove the whole
/// class, and this refuses the twenty-ninth literal.
/// </remarks>
public sealed class ToolPrefixLiteralGuardTests
{
    private const string OwningFileName = "ConnectionTypeCatalog.cs";

    private static readonly string[] ScannedProjects =
    [
        "src/Heimdall.App",
        "src/Heimdall.Core",
    ];

    [Fact]
    public void NoSourceSpellsTheToolPrefixItself()
    {
        string repoRoot = FindRepoRoot();
        List<string> violations = [];
        int scanned = 0;

        foreach (string project in ScannedProjects)
        {
            string root = Path.Combine(
                repoRoot,
                project.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(root), $"Project directory not found: {root}");

            foreach (string source in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                         .Where(path => !IsBuildOutput(path, root))
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                scanned++;
                if (Path.GetFileName(source) == OwningFileName)
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(source);
                for (int index = 0; index < lines.Length; index++)
                {
                    string trimmed = lines[index].TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal)
                        || trimmed.StartsWith("*", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (lines[index].Contains("\"TOOL:\"", StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"  {Path.GetRelativePath(repoRoot, source).Replace(Path.DirectorySeparatorChar, '/')}"
                            + $":{index + 1} - {lines[index].Trim()}");
                    }
                }
            }
        }

        // A sweep over an empty corpus passes for the wrong reason.
        Assert.True(scanned > 100, $"Only {scanned} source files were scanned.");

        Assert.True(
            violations.Count == 0,
            "The tool-type prefix is spelled as a literal. Use ConnectionTypeCatalog.ToolPrefix, "
            + "IsToolConnectionType or StripToolPrefix, so one comparison serves the whole "
            + "application:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TheOwningFileStillSpellsIt_SoTheExemptionIsNotHidingAnEmptySweep()
    {
        string repoRoot = FindRepoRoot();
        string[] matches = Directory.GetFiles(
            Path.Combine(repoRoot, "src", "Heimdall.Core"),
            OwningFileName,
            SearchOption.AllDirectories);

        string owning = Assert.Single(matches);
        Assert.Contains("\"TOOL:\"", File.ReadAllText(owning), StringComparison.Ordinal);
    }

    private static bool IsBuildOutput(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path)
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
