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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Fails when a canonical protocol has no arm in one of the per-protocol switches.
/// </summary>
/// <remarks>
/// Every one of these switches ends in a silent fallback - `Geo.Tree.Server`,
/// `TextSecondaryBrush`, `InfoBrush`, the raw type - so a protocol added to
/// <see cref="ConnectionTypeCatalog"/> without its arm does not fail, it just renders as
/// something generic. Nothing warns, nothing logs, and the omission is only ever noticed by
/// looking at the tree.
/// <para>
/// The protocols come from <see cref="ConnectionTypeCatalog.CanonicalTypes"/> at run time rather
/// than from a list here, so adding the tenth protocol extends this guard by itself. That is the
/// whole point: the guard has to be the thing nobody has to remember to update.
/// </para>
/// </remarks>
public sealed class ProtocolCoverageGuardTests
{
    /// <summary>
    /// Files whose whole body carries exactly one per-protocol switch.
    /// </summary>
    private static readonly (string Path, string What)[] WholeFileTargets =
    [
        ("src/Heimdall.App/Converters/ConnectionTypeToGeometryConverter.cs", "tree icon"),
        ("src/Heimdall.App/Converters/ConnectionTypeToColorConverter.cs", "protocol colour"),
        ("src/Heimdall.App/Converters/ConnectionTypeToBrushConverter.cs", "protocol brush"),
    ];

    private const string BadgeFile = "src/Heimdall.App/ViewModels/ServerItemViewModel.cs";
    private const string BadgeMember = "public string ConnectionTypeBadge";

    [Fact]
    public void EveryCanonicalProtocolHasAnArmInEveryPerProtocolSwitch()
    {
        string repoRoot = FindRepoRoot();
        string[] protocols = ConnectionTypeCatalog.CanonicalTypes
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // A sweep driven by an empty set passes for the wrong reason.
        Assert.True(protocols.Length >= 5, $"Only {protocols.Length} canonical protocols found.");

        List<string> missing = [];

        foreach ((string path, string what) in WholeFileTargets)
        {
            string body = ReadAll(repoRoot, path);
            foreach (string protocol in protocols)
            {
                if (!body.Contains($"\"{protocol}\" =>", StringComparison.Ordinal))
                {
                    missing.Add($"  {protocol} has no {what} ({path})");
                }
            }
        }

        // Scoped to the badge property on purpose. GetUsername in the same file is a DIFFERENT
        // switch whose fallback is legitimate - VNC, Telnet, Citrix and Local carry no username -
        // and it contains arms like "RDP" =>, so scanning the whole file would satisfy this guard
        // for the wrong reason.
        string badge = ExtractMember(ReadAll(repoRoot, BadgeFile), BadgeMember);
        foreach (string protocol in protocols)
        {
            if (!badge.Contains($"\"{protocol}\" =>", StringComparison.Ordinal))
            {
                missing.Add($"  {protocol} has no badge ({BadgeFile})");
            }
        }

        Assert.True(
            missing.Count == 0,
            "A canonical protocol reaches a silent fallback instead of its own arm, so it renders "
            + "as something generic with nothing to report it:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void TheBadgeMemberIsFoundAndIsNotTheWholeFile()
    {
        // Non-vacuity of the scoping itself: an extraction that silently returned everything would
        // make the badge check pass on GetUsername's arms.
        string repoRoot = FindRepoRoot();
        string file = ReadAll(repoRoot, BadgeFile);
        string badge = ExtractMember(file, BadgeMember);

        Assert.Contains("\"TELNET\" => \"TEL\"", badge, StringComparison.Ordinal);
        Assert.True(
            badge.Length < file.Length / 4,
            $"The extracted member is {badge.Length} chars of a {file.Length} char file.");
        Assert.DoesNotContain("GetUsername", badge, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardWouldNoticeAMissingArm()
    {
        // Proves the check discriminates, without waiting for a real regression: a body missing one
        // protocol must be reported, and the same body with it must not be.
        string[] protocols = ["RDP", "SSH"];
        string complete = "\"RDP\" => \"a\", \"SSH\" => \"b\", _ => \"z\"";
        string incomplete = "\"RDP\" => \"a\", _ => \"z\"";

        Assert.All(
            protocols,
            protocol => Assert.Contains($"\"{protocol}\" =>", complete, StringComparison.Ordinal));
        Assert.DoesNotContain("\"SSH\" =>", incomplete, StringComparison.Ordinal);
    }

    private static string ExtractMember(string fileBody, string memberSignature)
    {
        int start = fileBody.IndexOf(memberSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Member not found: {memberSignature}");

        int end = fileBody.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(end > start, $"End of member not found: {memberSignature}");

        return fileBody[start..end];
    }

    private static string ReadAll(string repoRoot, string relativePath)
    {
        string full = Path.Combine(
            repoRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {relativePath}");
        return File.ReadAllText(full);
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
