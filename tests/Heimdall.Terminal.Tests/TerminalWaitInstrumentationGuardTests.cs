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

namespace Heimdall.Terminal.Tests;

/// <summary>
/// Fails when a test in this assembly bounds a wait with the shared process backstop without going
/// through an instrumented helper.
/// </summary>
/// <remarks>
/// Instrumentation that exists but is bypassed measures nothing, and its absence is invisible: the
/// suite stays green either way, which is exactly the failure mode the observation was built to
/// end. Routing is therefore a checked property rather than a convention. The constant stays
/// reachable - the helpers are built from it - but reaching it from a test body is refused, because
/// that is the only shape that can reintroduce an unmeasured wait.
/// </remarks>
public sealed class TerminalWaitInstrumentationGuardTests
{
    private const string TestProjectRelativePath = "tests/Heimdall.Terminal.Tests";

    private const string BackstopToken = "ProcessStartupBackstop";

    /// <summary>
    /// The only file allowed to name the constant: it declares it and builds every instrumented
    /// helper from it.
    /// </summary>
    private const string InstrumentedHelpersFileName = "TerminalTestHelpers.cs";

    [Fact]
    public void NoTestBoundsAWaitOnTheBackstopWithoutInstrumentingIt()
    {
        string repoRoot = FindRepoRoot();
        string testRoot = Path.Combine(
            repoRoot,
            TestProjectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(Directory.Exists(testRoot), $"Test project directory not found: {testRoot}");

        string[] sources = Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, testRoot))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        // A sweep over an empty corpus passes for the wrong reason.
        Assert.NotEmpty(sources);

        List<string> violations = [];
        foreach (string source in sources)
        {
            string fileName = Path.GetFileName(source);
            if (fileName == InstrumentedHelpersFileName || fileName == ThisFileName)
            {
                continue;
            }

            string[] lines = File.ReadAllLines(source);
            for (int index = 0; index < lines.Length; index++)
            {
                if (lines[index].Contains(BackstopToken, StringComparison.Ordinal))
                {
                    string relativePath = Path.GetRelativePath(repoRoot, source)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    violations.Add($"  {relativePath}:{index + 1} - {lines[index].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"A wait is bounded by {BackstopToken} outside {InstrumentedHelpersFileName}, so how "
            + "long it really took is never published and a run that spends forty seconds there "
            + "is indistinguishable from one that spends forty milliseconds. Use "
            + "TerminalTestHelpers.AwaitProcessEventAsync, SpinUntilProcessEvent or "
            + "PollUntilProcessEventAsync instead:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TheGuardIsLookingForATokenThatStillExists()
    {
        string repoRoot = FindRepoRoot();
        string helpers = Path.Combine(
            repoRoot,
            TestProjectRelativePath.Replace('/', Path.DirectorySeparatorChar),
            InstrumentedHelpersFileName);

        // Non-vacuity of the sweep itself. Renaming the constant would leave the guard scanning for
        // a token that appears nowhere, which passes silently while policing nothing.
        Assert.True(File.Exists(helpers), $"Instrumented helpers not found: {helpers}");
        Assert.Contains(BackstopToken, File.ReadAllText(helpers), StringComparison.Ordinal);
    }

    private static string ThisFileName => Path.GetFileName(
        $"{nameof(TerminalWaitInstrumentationGuardTests)}.cs");

    private static bool IsBuildOutput(string path, string testRoot)
    {
        string relative = Path.GetRelativePath(testRoot, path)
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
