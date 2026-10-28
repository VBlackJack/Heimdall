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
using System.Text.Json;

namespace Heimdall.App.Tests;

/// <summary>
/// A locale key that no source file mentions is the marker of a half-delivered surface.
/// </summary>
/// <remarks>
/// Measured on three defects of the 2026-08-26 release: 39 of the 53 Project keys,
/// <c>ValidationInlineSshUserRequired</c>, and the four project-inheritance promises were all
/// translated into both languages and all dead. Every one of them was a feature wired half-way,
/// and none was visible to the compiler, to the suite, or to a review - only to the second human
/// who opened the application and looked for the thing the label promised.
///
/// The 2026-10-28 clean-up deleted 1304 of the 1332 keys that stood dead (17 percent of the file)
/// from every locale; the baseline beside this file keeps only the 14 that cannot go, and the reason is
/// written there. The baseline may only shrink, which the second test enforces - an allow-list
/// nobody prunes stops being a guard and becomes a permanent pardon.
///
/// <para>
/// Keys composed at runtime are exempt, and the exempt prefixes are derived from the source rather
/// than hand-listed, so a new dynamic family teaches the guard about itself (see
/// <see cref="DeadLocaleKeyDetector"/> for the shapes it recognises). Without that,
/// <c>ToolDescHTTPHEADERS</c> and 132 siblings read as dead while being resolved every time the
/// Tools tab is drawn, from the interpolation on descriptor.Id.
/// </para>
/// </remarks>
public sealed class DeadLocaleKeyGuardTests
{
    // Vacuity guards. A guard whose file discovery silently returns nothing passes forever and
    // measures nothing, which is a failure mode this repository has already been caught by.
    private const int MinExpectedSourceFiles = 1000;
    private const int MinExpectedKeys = 5000;

    [Fact]
    public void NoLocaleKeyBecomesDeadWithoutBeingDeclaredSo()
    {
        Analysis analysis = Analyse();

        HashSet<string> baseline = ReadBaseline();
        List<string> unexpected = analysis.Dead
            .Where(key => !baseline.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"{unexpected.Count} locale key(s) are translated but referenced nowhere under src/. "
            + DeadLocaleKeyDetector.NewDeadKeyAdvice
            + Environment.NewLine
            + string.Join(Environment.NewLine, unexpected.Take(40)));
    }

    // Without this, the baseline only ever grows: a key that gets wired up, or deleted outright,
    // leaves a line behind that quietly forgives the next key of the same name.
    [Fact]
    public void TheBaselineHoldsNothingThatIsNoLongerDead()
    {
        Analysis analysis = Analyse();

        HashSet<string> dead = new(analysis.Dead, StringComparer.Ordinal);
        List<string> stale = ReadBaseline()
            .Where(key => !dead.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} baseline entries are no longer dead - they are now referenced, or gone "
            + "from en.json. Delete these lines from dead-locale-keys.baseline.txt:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, stale.Take(40)));
    }

    private sealed record Analysis(IReadOnlyList<string> Dead, int SourceFileCount, int KeyCount);

    private static Analysis Analyse()
    {
        string root = RepoRoot();

        Dictionary<string, JsonElement> locale = JsonSerializer
            .Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(Path.Combine(root, "locales", "en.json")))!;
        List<string> keys = locale.Keys.ToList();

        List<string> files = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsGenerated(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            files.Count >= MinExpectedSourceFiles,
            $"Only {files.Count} source files found under src/. Discovery failed, so this guard "
            + "would pass while measuring nothing.");
        Assert.True(
            keys.Count >= MinExpectedKeys,
            $"Only {keys.Count} keys read from en.json. The locale read failed.");

        string source = string.Join("\n", files.Select(File.ReadAllText));

        // The detector reads the families composed at runtime from the source itself, so a new
        // dynamic family teaches the guard about itself instead of being hand-listed.
        List<string> dead = DeadLocaleKeyDetector.FindDeadKeys(keys, source);

        return new Analysis(dead, files.Count, keys.Count);
    }

    private static bool IsGenerated(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> ReadBaseline() =>
        File.ReadAllLines(Path.Combine(
                RepoRoot(), "tests", "Heimdall.App.Tests", "dead-locale-keys.baseline.txt"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
