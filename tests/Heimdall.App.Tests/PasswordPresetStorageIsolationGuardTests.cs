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
/// Fails when a test in this assembly builds the password-generator view model or its preset
/// storage without supplying a storage path.
/// </summary>
/// <remarks>
/// Both types have a parameterless constructor that resolves the real user location:
/// <c>PasswordPresetStorage()</c> chains to <c>ApplicationDataPathResolver.Resolve()</c>, which
/// is <c>%LOCALAPPDATA%\Heimdall</c>. A test built that way reads and rewrites the operator's
/// own presets, outside any sandbox, and its teardown deletes a temporary directory that was
/// never the file it touched. That is what happened before the last unbound call site was fixed.
/// <para>
/// The production constructors stay: they are how the application resolves its own storage. What
/// is refused is reaching them from a test. The guard scans source rather than behaviour because
/// the damage is done the moment such a test runs, so an oracle that only observes the outcome
/// would have to write to the real file to detect the problem.
/// </para>
/// </remarks>
public sealed class PasswordPresetStorageIsolationGuardTests
{
    private const string TestProjectRelativePath = "tests/Heimdall.App.Tests";

    /// <summary>
    /// Parameterless construction of either type, in any of the forms C# accepts: <c>new T()</c>,
    /// and the target-typed <c>T x = new();</c> that the fixed call site itself uses.
    /// </summary>
    private static readonly Regex UnboundConstruction = new(
        @"new\s+(?:PasswordGeneratorViewModel|PasswordPresetStorage)\s*\(\s*\)"
        + @"|(?:PasswordGeneratorViewModel|PasswordPresetStorage)\s+\w+\s*=\s*new\s*\(\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void NoTestBuildsPresetStorageWithoutAnInjectedPath()
    {
        string repoRoot = FindRepoRoot();
        string testRoot = Path.Combine(repoRoot, TestProjectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(Directory.Exists(testRoot), $"Test project directory not found: {testRoot}");

        string[] sources = Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories);

        // A guard that scans nothing passes for the wrong reason, so the sweep asserts it found
        // the corpus it is supposed to police.
        Assert.NotEmpty(sources);

        List<string> violations = [];
        foreach (string source in sources.OrderBy(path => path, StringComparer.Ordinal))
        {
            // This file quotes the forbidden shapes in its own pattern and documentation.
            if (Path.GetFileName(source) == ThisFileName)
            {
                continue;
            }

            string[] lines = File.ReadAllLines(source);
            for (int index = 0; index < lines.Length; index++)
            {
                if (UnboundConstruction.IsMatch(lines[index]))
                {
                    string relativePath = Path.GetRelativePath(repoRoot, source)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    violations.Add($"  {relativePath}:{index + 1} - {lines[index].Trim()}");
                }
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
