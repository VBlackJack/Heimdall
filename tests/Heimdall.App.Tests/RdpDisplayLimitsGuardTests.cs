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
/// Keeps the fixed-resolution RDP bounds defined once.
/// </summary>
/// <remarks>
/// <para>
/// The pair 7680 x 4320 used to be written out in five places across three
/// projects. A test that merely asserts those places <em>equal</em>
/// <c>RdpDisplayLimits</c> cannot detect the regression that matters: a literal
/// <c>7680</c> written back into one of them is still equal, and the duplication
/// returns silently. Only a source-level check distinguishes derivation from
/// coincidence, so that is what this guard measures.
/// </para>
/// <para>
/// Reverting any wired site to a literal reddens
/// <see cref="Sources_DoNotRepeatTheFixedResolutionBounds" />.
/// </para>
/// </remarks>
public sealed class RdpDisplayLimitsGuardTests
{
    private const string SourceRelativePath = "src";

    /// <summary>The one file allowed to state the bounds.</summary>
    private static readonly string DefinitionFile =
        Path.Combine("Heimdall.Core", "Rdp", "RdpDisplayLimits.cs");

    /// <summary>
    /// A file deep inside the tree that the scan must reach. Without this, a
    /// non-recursive enumeration would report zero violations and read as a pass.
    /// </summary>
    private static readonly string SubdirectoryProbe =
        Path.Combine("Heimdall.Rdp", "Display", "RdpDisplayResolver.cs");

    private static readonly Regex BoundsLiteralRegex = new(
        @"\b(7680|4320)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Nothing is exempt any more.
    /// </summary>
    /// <remarks>
    /// <c>SchemaValidator.MaxResolution</c> used to be, on the grounds that a session's
    /// screen size is a different decision from a fixed desktop that happens to share a
    /// number. That is still true, but the decision grew two more holders on the settings
    /// panel, so it earned its own pair of constants beside the fixed ones and the
    /// validator now derives from them. An exemption that no longer covers anything can
    /// only hide the next regression.
    /// </remarks>
    private static bool IsUnrelatedDecision(string line) => false;

    [Fact]
    public void Sources_DoNotRepeatTheFixedResolutionBounds()
    {
        string sourceDir = Path.Combine(FindRepoRoot(), SourceRelativePath);
        Assert.True(Directory.Exists(sourceDir), $"Source directory not found: {sourceDir}");

        List<string> violations = new();
        bool reachedSubdirectoryProbe = false;

        foreach (string file in SourceFileEnumeration.EnumerateFiles(sourceDir, "*.cs"))
        {
            string relative = Path.GetRelativePath(sourceDir, file);

            if (relative.Equals(SubdirectoryProbe, StringComparison.OrdinalIgnoreCase))
            {
                reachedSubdirectoryProbe = true;
            }

            if (relative.Equals(DefinitionFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsUnrelatedDecision(lines[i]))
                {
                    continue;
                }

                Match match = BoundsLiteralRegex.Match(lines[i]);
                if (match.Success)
                {
                    violations.Add($"  {relative}:{i + 1} - {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            reachedSubdirectoryProbe,
            $"The scan never reached {SubdirectoryProbe}, so a green result here measures nothing."
            + " Fix the enumeration before trusting this guard.");

        Assert.True(
            violations.Count == 0,
            $"Found {violations.Count} literal RDP fixed-resolution bound(s) outside"
            + $" {DefinitionFile}. Use RdpDisplayLimits instead - the pair was duplicated"
            + " across five sites in three projects and this guard exists to stop it"
            + " coming back:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DefinitionFile_Exists()
    {
        string sourceDir = Path.Combine(FindRepoRoot(), SourceRelativePath);

        Assert.True(
            File.Exists(Path.Combine(sourceDir, DefinitionFile)),
            $"The exempt definition file no longer exists: {DefinitionFile}."
            + " If it moved, update this guard rather than deleting it.");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
                return dir;

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
