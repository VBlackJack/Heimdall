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

using System.Text.RegularExpressions;
using Heimdall.Core.Tests.Vault;

namespace Heimdall.Core.Tests;

/// <summary>
/// Keeps the two rules of the <see cref="CredentialProtectorStaticCollection"/> closed over the
/// Core test sources: every class that touches <c>CredentialProtector</c> joins the collection,
/// and every member owns a <see cref="CredentialProtectorStateScope"/>.
/// </summary>
/// <remarks>
/// <para>The first rule removes concurrency: without it, one class installs a DEK while another
/// asserts legacy output. The second removes ordering: without it, a class inherits the mode the
/// previously scheduled member left behind, and its outcome depends on which writer forgot which
/// reset. Both omissions are invisible in review and produce a failure that depends on what else
/// is in the run.</para>
/// <para>Each rule also asserts that it walked at least the members known when it was written,
/// so a scan that stops matching fails under its own name instead of turning green by finding
/// nothing.</para>
/// </remarks>
public sealed class CredentialProtectorCollectionGuardTests
{
    /// <summary>
    /// The members counted on 2026-09-05: four writers under <c>Vault/</c> and two readers at
    /// the root. Below the measured count on purpose; a floor is not a ceiling.
    /// </summary>
    private const int MemberFloor = 5;

    private const string CollectionMembership = "[Collection(CredentialProtectorStaticCollection.Name)]";

    private const string ScopeTypeName = "CredentialProtectorStateScope";

    private static readonly Regex s_classDeclaration = new(@"^\s*(?:public|internal).*\bclass\s+(\w+)", RegexOptions.Compiled);

    [Fact]
    public void EveryTestClassTouchingTheProtectorJoinsTheSerializingCollection()
    {
        List<MemberSource> members = MembersTouchingTheProtector();

        List<string> offenders = members
            .Where(member => !member.Lines.Any(line => line.Contains(CollectionMembership, StringComparison.Ordinal)))
            .Select(member => member.Describe())
            .ToList();

        Assert.True(
            members.Count >= MemberFloor,
            $"Only {members.Count} test classes touch CredentialProtector; at least {MemberFloor} did when this guard was written, so the scan is no longer reaching them.");
        Assert.True(
            offenders.Count == 0,
            "These test classes touch the CredentialProtector process-global state without joining "
            + $"the {CredentialProtectorStaticCollection.Name} collection, so their result depends on what else is in the run:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryMemberOfTheCollectionOwnsAStateScope()
    {
        List<MemberSource> members = MembersTouchingTheProtector()
            .Where(member => member.Lines.Any(line => line.Contains(CollectionMembership, StringComparison.Ordinal)))
            .ToList();

        List<string> offenders = members
            .Where(member => !member.Lines.Any(line => line.Contains(ScopeTypeName, StringComparison.Ordinal)))
            .Select(member => member.Describe())
            .ToList();

        Assert.True(
            members.Count >= MemberFloor,
            $"Only {members.Count} members of the collection were found; at least {MemberFloor} existed when this guard was written, so the scan is no longer reaching them.");
        Assert.True(
            offenders.Count == 0,
            $"These members of the {CredentialProtectorStaticCollection.Name} collection do not own a {ScopeTypeName}, "
            + "so they inherit whatever vault mode the previously scheduled member left behind:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static List<MemberSource> MembersTouchingTheProtector()
    {
        string testsDirectory = Path.GetDirectoryName(FindThisFile())!;
        List<MemberSource> members = [];

        foreach (string path in Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(path, testsDirectory) || IsInfrastructure(path))
            {
                continue;
            }

            // Matched line by line: the repository stores CRLF, and an anchored multiline regex
            // over the raw text silently never matches.
            string[] lines = File.ReadAllLines(path);
            if (!lines.Any(line => line.Contains("CredentialProtector.", StringComparison.Ordinal)))
            {
                continue;
            }

            members.Add(new MemberSource(path, lines));
        }

        return members;
    }

    /// <summary>
    /// The collection definition, the scope and this guard name the protector in their own
    /// prose and code, so they would flag themselves.
    /// </summary>
    private static bool IsInfrastructure(string path)
    {
        string name = Path.GetFileName(path);
        return name is "CredentialProtectorStaticCollection.cs"
            or "CredentialProtectorStateScope.cs"
            or "CredentialProtectorCollectionGuardTests.cs";
    }

    private static bool IsBuildOutput(string path, string testsDirectory)
    {
        string relative = path[testsDirectory.Length..];
        return relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string FindThisFile()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "CredentialProtectorCollectionGuardTests.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string nested = Path.Combine(directory, "tests", "Heimdall.Core.Tests", "CredentialProtectorCollectionGuardTests.cs");
            if (File.Exists(nested))
            {
                return nested;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException("Cannot locate CredentialProtectorCollectionGuardTests.cs from the test binary directory.");
    }

    private sealed record MemberSource(string Path, string[] Lines)
    {
        public string Describe()
        {
            string? declared = Lines
                .Select(line => s_classDeclaration.Match(line))
                .FirstOrDefault(match => match.Success)?.Groups[1].Value;

            return $"{System.IO.Path.GetFileName(Path)} ({declared ?? "unknown class"})";
        }
    }
}
