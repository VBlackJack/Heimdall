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
using FluentAssertions;
using Heimdall.Core.Updates;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// No shipped code builds a failed update check without saying why.
/// </summary>
/// <remarks>
/// <para>
/// A-17's whole point is that a failure with no cause is what the user cannot act on.
/// <see cref="GitHubReleaseResult"/> makes that state unrepresentable at the client seam,
/// where it cost nothing; this is the seam the finding actually lives at, and a
/// <c>init</c> property cannot be validated against a positional parameter, so
/// <c>new UpdateCheckResult(UpdateCheckStatus.CheckFailed, null)</c> still compiles and
/// still leaves <c>Failure</c> at <c>None</c>.
/// </para>
/// <para>
/// A guard rather than a type change, because the type change would be a breaking rewrite
/// of a record five callers construct. What it costs is that it only covers shipped code:
/// a test may still build that value on purpose, and several do, to prove the fallback
/// wording is reachable.
/// </para>
/// </remarks>
public sealed class UpdateCheckResultConstructionGuardTests
{
    private const string FactoryFile = "UpdateCheckResult.cs";

    [Fact]
    public void NoShippedCode_BuildsAFailedCheckWithoutACause()
    {
        var rawConstruction = new Regex(
            @"new\s+UpdateCheckResult\s*\(\s*UpdateCheckStatus\.CheckFailed",
            RegexOptions.CultureInvariant);

        // The matcher is checked against a sample it must match, so this absence assertion
        // cannot pass because the pattern quietly stopped matching anything at all.
        rawConstruction.IsMatch("return new UpdateCheckResult(UpdateCheckStatus.CheckFailed, null);")
            .Should().BeTrue(because: "the guard's own pattern has to match the shape it bans");

        var offenders = new List<string>();
        int scanned = 0;

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            scanned++;
            if (string.Equals(Path.GetFileName(file), FactoryFile, StringComparison.Ordinal))
            {
                // The factory is where the state is built, once, behind its own throw.
                continue;
            }

            if (rawConstruction.IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot(), file));
            }
        }

        scanned.Should().BeGreaterThan(100, because: "a scan that reached nothing proves nothing");
        offenders.Should().BeEmpty(
            because: "a failed check must be built through UpdateCheckResult.Failed, which "
            + "refuses a failure that declines to name its cause");
    }

    [Fact]
    public void Failed_RefusesAFailureWithNoCause()
    {
        var refusal = Assert.Throws<ArgumentException>(
            () => UpdateCheckResult.Failed(UpdateCheckFailure.None));
        refusal.Message.Should().Contain("why");

        var failed = UpdateCheckResult.Failed(UpdateCheckFailure.RateLimited, TimeSpan.FromMinutes(9));
        failed.Status.Should().Be(UpdateCheckStatus.CheckFailed);
        failed.Failure.Should().Be(UpdateCheckFailure.RateLimited);
        failed.RetryAfter.Should().Be(TimeSpan.FromMinutes(9));
        failed.Update.Should().BeNull();
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull(because: "the walk up from the test output must reach the repository");
        return directory!.FullName;
    }
}
