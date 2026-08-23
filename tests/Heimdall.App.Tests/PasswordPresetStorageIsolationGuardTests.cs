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
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;

namespace Heimdall.App.Tests;

/// <summary>
/// Fails when a test in this assembly builds the password-generator view model or its preset
/// storage without supplying a storage path.
/// </summary>
/// <remarks>
/// Both types once had a parameterless constructor that resolved the real user location:
/// <c>PasswordPresetStorage()</c> chained to <c>ApplicationDataPathResolver.Resolve()</c>, which
/// is <c>%LOCALAPPDATA%\Heimdall</c>. A test built that way read and rewrote the operator's own
/// presets, outside any sandbox, and its teardown deleted a temporary directory that was never
/// the file it touched. That is what happened before the last unbound call site was fixed.
/// <para>
/// <b>Those two constructors are gone since 2026-08-23</b>, and the production location is
/// resolved once in the composition root. The compiler now refuses what this sweep used to
/// police by pattern - see <see cref="NeitherType_OffersAWayToReachTheRealUserLocation"/>,
/// which is the oracle that would notice one coming back. This source sweep stays as a belt:
/// it also catches an argument DERIVED from the production resolver, which compiles perfectly
/// well and is just as damaging.
/// </para>
/// </remarks>
public sealed class PasswordPresetStorageIsolationGuardTests
{
    private const string TestRootRelativePath = "tests";

    /// <summary>
    /// Test projects the sweep must reach. Named rather than merely counted, so that renaming
    /// or moving one is a failure here instead of a silent loss of coverage.
    /// </summary>
    private static readonly string[] ExpectedTestProjects =
    [
        "Heimdall.App.Tests",
        "Heimdall.App.UiTests"
    ];

    /// <summary>
    /// Parameterless construction of either type, in any of the forms C# accepts: <c>new T()</c>,
    /// and the target-typed <c>T x = new();</c> that the fixed call site itself uses.
    /// </summary>
    private const char LineFeed = '\n';

    private static readonly Regex UnboundConstruction = new(
        @"new\s+(?:PasswordGeneratorViewModel|PasswordPresetStorage)\s*\(\s*\)"
        + @"|(?:PasswordGeneratorViewModel|PasswordPresetStorage)\s+\w+\s*=\s*new\s*\(\s*\)"
        // An argument is not on its own proof of isolation: one derived from the production
        // resolver points at the operator's own directory, which is the very thing refused.
        + @"|new\s+(?:PasswordGeneratorViewModel|PasswordPresetStorage)\s*\([^)]*ApplicationDataPathResolver[^)]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void NeitherType_OffersAWayToReachTheRealUserLocation()
    {
        // The substitution decided on 2026-08-23 and delivered here. The sweep below polices
        // by pattern; this one is the compiler. A parameterless constructor put back on either
        // type would restore the only two ways a test could reach %LOCALAPPDATA%\Heimdall, and
        // the sweep would go on passing until somebody actually wrote the call.
        Assert.Null(ParameterlessConstructorOf(typeof(PasswordPresetStorage)));
        Assert.Null(ParameterlessConstructorOf(typeof(PasswordGeneratorViewModel)));
    }

    private static ConstructorInfo? ParameterlessConstructorOf(Type type)
        => type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

    [Fact]
    public void NoTestBuildsPresetStorageWithoutAnInjectedPath()
    {
        string repoRoot = FindRepoRoot();
        string testRoot = Path.Combine(repoRoot, TestRootRelativePath);

        Assert.True(Directory.Exists(testRoot), $"Test root directory not found: {testRoot}");

        string[] sources = Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        // A guard that scans nothing passes for the wrong reason, so the sweep asserts it found
        // the corpus it is supposed to police.
        Assert.NotEmpty(sources);

        // And that it reached every project that can reach these types. Scoping this sweep to a
        // single assembly is what let a second one construct them unwatched: Heimdall.App.UiTests
        // references Heimdall.App too.
        foreach (string project in ExpectedTestProjects)
        {
            string marker = $"{Path.DirectorySeparatorChar}{project}{Path.DirectorySeparatorChar}";
            Assert.Contains(
                sources,
                path => path.Contains(marker, StringComparison.Ordinal));
        }

        List<string> violations = [];
        foreach (string source in sources.OrderBy(path => path, StringComparer.Ordinal))
        {
            // This file quotes the forbidden shapes in its own pattern and documentation.
            if (Path.GetFileName(source) == ThisFileName)
            {
                continue;
            }

            // Scanned as one string rather than line by line, so a construction split across
            // lines cannot slip through the gap between two reads.
            string text = File.ReadAllText(source);
            foreach (Match match in UnboundConstruction.Matches(text))
            {
                string relativePath = Path.GetRelativePath(repoRoot, source)
                    .Replace(Path.DirectorySeparatorChar, '/');
                int line = text.Take(match.Index).Count(character => character == LineFeed) + 1;
                string quoted = match.Value.ReplaceLineEndings(" ").Trim();
                violations.Add($"  {relativePath}:{line} - {quoted}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "A test builds the password preset storage without an injected path, so it reads and "
            + "rewrites the real user file under %LOCALAPPDATA%\\Heimdall instead of a temporary "
            + "directory. Pass a storage rooted in a test-owned temporary path, as "
            + "PasswordGeneratorViewModelTests does:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TheGuardPatternMatchesEveryFormItClaimsToCover()
    {
        // Non-vacuity of the pattern itself, checked without waiting for a real regression: the
        // sweep above can only be trusted if these shapes are recognised.
        Assert.Matches(UnboundConstruction, "var sut = new PasswordGeneratorViewModel();");
        Assert.Matches(UnboundConstruction, "PasswordGeneratorViewModel sut = new();");
        Assert.Matches(UnboundConstruction, "var store = new PasswordPresetStorage();");
        Assert.Matches(UnboundConstruction, "PasswordPresetStorage store = new();");

        // The injected forms are exactly what the guard must keep allowing.
        Assert.DoesNotMatch(UnboundConstruction, "new PasswordGeneratorViewModel(new PasswordPresetStorage(path))");
        Assert.DoesNotMatch(UnboundConstruction, "PasswordGeneratorViewModel sut = new(storage);");
        Assert.DoesNotMatch(UnboundConstruction, "new PasswordPresetStorage(_presetsDirectoryPath)");

        // Split across lines, which the previous line-by-line sweep could not see.
        Assert.Matches(
            UnboundConstruction,
            "var store = new PasswordPresetStorage(" + Environment.NewLine + "            );");

        // An argument that resolves the production location is not isolation.
        Assert.Matches(
            UnboundConstruction,
            "new PasswordPresetStorage(ApplicationDataPathResolver.Resolve())");
    }

    private static string ThisFileName => Path.GetFileName(
        $"{nameof(PasswordPresetStorageIsolationGuardTests)}.cs");

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
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
