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
/// xUnit collection that serializes App-side test classes mutating the static
/// state of <c>CredentialProtector</c> (the vault DEK / enabled slots), so they
/// never run concurrently and cross-contaminate each other.
/// </summary>
/// <remarks>
/// Reading that state is as much a reason to join as writing it. A class that only protects a
/// value inherits whatever another class left behind, or has it replaced mid-test: the legacy
/// integrity key is process-global, so a value protected with one key and read back with another
/// simply fails to decrypt, and the failure surfaces far from its cause.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class CredentialProtectorAppCollection
{
    /// <summary>The collection name shared by all members.</summary>
    public const string Name = "CredentialProtectorAppState";
}

/// <summary>
/// Guards the two rules of the collection. Membership: omitting a single class is invisible in
/// review and produces a failure that depends on which other tests share the run; on 2026-08-14
/// one omitted class failed four runs out of four when paired with a writer, and passed alone.
/// Baseline: membership removes concurrency and nothing else, so every member must also own a
/// <see cref="CredentialProtectorStateScope"/>, or it inherits the vault mode the previously
/// scheduled member left behind (BL-0099).
/// </summary>
/// <remarks>
/// Each rule also asserts that it walked at least the members known when it was written, so a
/// scan that stops matching fails under its own name instead of turning green by finding
/// nothing.
/// </remarks>
public sealed class CredentialProtectorCollectionMembershipTests
{
    /// <summary>
    /// The members counted on 2026-09-05: five writers and three readers. Below the measured
    /// count on purpose; a floor is not a ceiling.
    /// </summary>
    private const int MemberFloor = 7;

    private const string CollectionMembership = $"Collection({nameof(CredentialProtectorAppCollection)}.Name)";

    private const string ScopeTypeName = nameof(CredentialProtectorStateScope);

    /// <summary>
    /// Production types that reach the protector without the test having to name it. Building one
    /// seals and unseals through the same process-global slots as calling the protector directly,
    /// and it fails the same way: a value written under one key and read back under another.
    /// </summary>
    /// <remarks>
    /// <para>The census counted only files spelling <c>CredentialProtector.</c> until 2026-09-20.
    /// <c>PasswordGeneratorViewModelTests</c> spells only <c>PasswordPresetStorage</c>, so it sat
    /// outside the collection unseen, and lost its remembered settings whenever a member flipped
    /// the legacy HMAC key between its write and its read. A type belongs on this list once it is
    /// measured, not once it looks plausible; the entries are checked by
    /// <see cref="TheScanReachesAClassThatOnlyNamesAnIndirectSealingType"/>.</para>
    /// <para><b>This list is written by hand, and a green run here is not evidence that no other
    /// indirect sealer exists.</b> It covers the names on it and nothing else. A future type that
    /// calls the protector internally - and a test class that builds one of the other production
    /// types already doing so - passes through exactly the hole this entry was added to close,
    /// and the guard then certifies an absence it never looked for. Read a failure here as real
    /// and a pass as silent.</para>
    /// <para>Deriving the list instead of maintaining it was measured on 2026-09-20 and rejected
    /// as unsound rather than rejected as hard. Walking <c>src/</c> for types naming the protector
    /// does yield the set with no maintenance, 21 of them; but the census built on it flags 13
    /// test classes, and instrumenting <c>Protect</c>/<c>Unprotect</c> and running each in
    /// isolation showed <b>zero of the 13</b> ever reach the protector. The derived form would
    /// demand 13 serializations that protect nothing, and invite a hand-written exclusion list in
    /// their place. The fact this guard needs is a runtime fact, and source cannot supply it: what
    /// closes the class of defect is removing the process-global state from the protector, not a
    /// wider grep.</para>
    /// </remarks>
    private static readonly string[] IndirectSealingTypes = ["PasswordPresetStorage"];

    /// <summary>
    /// A test class that only names an indirect sealing type is still a member. Dies if the
    /// census is narrowed back to files that spell the protector themselves.
    /// </summary>
    [Fact]
    public void TheScanReachesAClassThatOnlyNamesAnIndirectSealingType()
    {
        List<MemberSource> members = MembersTouchingTheProtector();

        Assert.Contains(
            members,
            member => Path.GetFileName(member.FilePath) == "PasswordGeneratorViewModelTests.cs");

        // And that it is reached for the stated reason rather than by having since acquired a
        // direct call, which would make the widened census look load-bearing when it is not.
        MemberSource reached = members.Single(
            member => Path.GetFileName(member.FilePath) == "PasswordGeneratorViewModelTests.cs");

        Assert.DoesNotContain(
            reached.Lines,
            line => line.Contains("CredentialProtector.", StringComparison.Ordinal));
    }

    private static bool TouchesTheProtector(string line)
        => line.Contains("CredentialProtector.", StringComparison.Ordinal)
            || IndirectSealingTypes.Any(type => line.Contains($"new {type}(", StringComparison.Ordinal));

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
            "These test classes touch the CredentialProtector process-global state without joining " +
            $"the {Name} collection, so their result depends on what else is in the run:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
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
            $"These members of the {Name} collection do not own a {ScopeTypeName}, " +
            "so they inherit whatever vault mode the previously scheduled member left behind:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private const string Name = CredentialProtectorAppCollection.Name;

    private static readonly Regex s_classDeclaration = new(@"^\s*(?:public|internal).*\bclass\s+(\w+)", RegexOptions.Compiled);

    private static List<MemberSource> MembersTouchingTheProtector()
    {
        string testsDirectory = Path.GetDirectoryName(FindThisFile())!;
        List<MemberSource> members = [];

        foreach (string path in Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories))
        {
            // The collection, the scope and this guard name the protector in their own prose
            // and code, so they would flag themselves.
            string name = Path.GetFileName(path);
            if (name is "CredentialProtectorAppCollection.cs"
                or "CredentialProtectorStateScope.cs"
                // Quotes the constructions it refuses inside its own pattern and its assertions,
                // and performs none of them, so it seals nothing.
                or "PasswordPresetStorageIsolationGuardTests.cs"
                // Guard machinery rather than a test class: it arms the runtime observer that
                // watches the calls this census can only guess at.
                or "CredentialProtectorRuntimeGuard.cs")
            {
                continue;
            }

            // Matched line by line: the repository stores CRLF, and an anchored multiline regex
            // over the raw text silently never matches.
            string[] lines = File.ReadAllLines(path);
            if (!lines.Any(TouchesTheProtector))
            {
                continue;
            }

            members.Add(new MemberSource(path, lines));
        }

        return members;
    }

    private sealed record MemberSource(string FilePath, string[] Lines)
    {
        public string Describe()
        {
            string? declared = Lines
                .Select(line => s_classDeclaration.Match(line))
                .FirstOrDefault(match => match.Success)?.Groups[1].Value;

            return $"{Path.GetFileName(FilePath)} ({declared ?? "unknown class"})";
        }
    }

    private static string FindThisFile()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "CredentialProtectorAppCollection.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string nested = Path.Combine(directory, "tests", "Heimdall.App.Tests", "CredentialProtectorAppCollection.cs");
            if (File.Exists(nested))
            {
                return nested;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException("Cannot locate CredentialProtectorAppCollection.cs from the test binary directory.");
    }
}
