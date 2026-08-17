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

using Heimdall.Core.Security;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Pins the publication commands: what makes them safe is the primitive chosen, so the assertions are
/// about which primitive appears and which must not.
/// </summary>
/// <remarks>
/// These are contract oracles over generated text, not evidence that a server behaves this way. Three
/// separate things are worth keeping apart. The exclusivity of <c>ln</c> and of <c>mkdir</c> without
/// <c>-p</c> is a semantic guarantee of those utilities. Whether the remote utility accepts <c>--</c>
/// as an end-of-options marker is a compatibility question about the server's implementation, not a
/// universal guarantee. And neither is evidence about any real server: no SFTP server is exercised
/// here or anywhere in this suite. An incompatible utility makes the publish fail, and the caller
/// refuses rather than falling back to a primitive that could replace the destination.
/// </remarks>
public sealed class ServerSideNoClobberPublishCommandTests
{
    [Fact]
    public void BuildFilePublish_LinksTheStagingPath_AndNeverRenamesOverTheDestination()
    {
        string command = ServerSideNoClobberPublishCommand.BuildFilePublish(
            "/srv/data/file.txt.abc.part",
            "/srv/data/file.txt");

        Assert.Contains("ln -- '/srv/data/file.txt.abc.part' '/srv/data/file.txt'", command, StringComparison.Ordinal);

        // A rename replaces the destination silently on many servers. It is the one primitive that
        // must never appear here, in any form.
        Assert.DoesNotContain("mv ", command, StringComparison.Ordinal);
        Assert.DoesNotContain("cp ", command, StringComparison.Ordinal);
        Assert.DoesNotContain("ln -f", command, StringComparison.Ordinal);
        Assert.DoesNotContain("ln -s", command, StringComparison.Ordinal);
    }

    // The exit status must be the link's, sampled before cleanup: a failed cleanup must not report a
    // failed publish, and a successful publish must not be masked by it.
    [Fact]
    public void BuildFilePublish_ReportsTheLinkStatus_AndCleansUpTheStagingPath()
    {
        string command = ServerSideNoClobberPublishCommand.BuildFilePublish("/s/t.part", "/s/t");

        int linkIndex = command.IndexOf("ln -- ", StringComparison.Ordinal);
        int statusIndex = command.IndexOf("status=$?", StringComparison.Ordinal);
        int cleanupIndex = command.IndexOf("rm -f -- ", StringComparison.Ordinal);
        int exitIndex = command.IndexOf("exit $status", StringComparison.Ordinal);

        Assert.True(linkIndex >= 0 && statusIndex > linkIndex, "the status must be sampled after the link");
        Assert.True(cleanupIndex > statusIndex, "the cleanup must run after the status is captured");
        Assert.True(exitIndex > cleanupIndex, "the captured status must be what the command exits with");

        // Cleanup targets the staging path only. The destination is never a cleanup argument.
        Assert.Contains("rm -f -- '/s/t.part'", command, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -f -- '/s/t'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDirectoryReservation_ReservesExclusively_WithoutParentsFlag()
    {
        string command = ServerSideNoClobberPublishCommand.BuildDirectoryReservation("/srv/data/proj");

        // Exact equality, which already excludes -p. A separate DoesNotContain("-p") would also fire
        // on any destination path containing those two characters, so it would be a trap rather than
        // a guarantee. -p is what makes mkdir succeed on an existing directory, i.e. the merge this
        // design forbids.
        Assert.Equal("mkdir -- '/srv/data/proj'", command);
    }

    // Both paths are shell-escaped and guarded by --, so a path that begins with a dash or embeds a
    // quote cannot become a flag or break out of its argument. Asserted as exact equality: checking
    // only that the escaped destination appears would pass while the staging path went through raw,
    // and the staging path is the one this command names twice.
    [Theory]
    [InlineData("/srv/-rf")]
    [InlineData("/srv/it's here")]
    [InlineData("/srv/a b")]
    [InlineData("/srv/$(whoami)")]
    [InlineData("/srv/`id`")]
    public void BuildFilePublish_EscapesBothPaths_AndKeepsTheEndOfOptionsGuard(string destination)
    {
        string staging = destination + ".part";
        string escapedStaging = InputValidator.EscapeShellArg(staging);
        string escapedDestination = InputValidator.EscapeShellArg(destination);

        string command = ServerSideNoClobberPublishCommand.BuildFilePublish(staging, destination);

        Assert.Equal(
            $"ln -- {escapedStaging} {escapedDestination}; status=$?; rm -f -- {escapedStaging}; exit $status",
            command);
    }

    [Theory]
    [InlineData("/srv/-rf")]
    [InlineData("/srv/it's here")]
    [InlineData("/srv/$(whoami)")]
    public void BuildDirectoryReservation_EscapesThePath_AndKeepsTheEndOfOptionsGuard(string path)
    {
        string command = ServerSideNoClobberPublishCommand.BuildDirectoryReservation(path);

        Assert.StartsWith("mkdir -- '", command, StringComparison.Ordinal);
        Assert.Contains(InputValidator.EscapeShellArg(path), command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "/dst")]
    [InlineData("   ", "/dst")]
    [InlineData("/staging", "")]
    [InlineData("/staging", "   ")]
    public void BuildFilePublish_RejectsBlankPaths(string staging, string destination)
    {
        Assert.Throws<ArgumentException>(
            () => ServerSideNoClobberPublishCommand.BuildFilePublish(staging, destination));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildDirectoryReservation_RejectsBlankPaths(string path)
    {
        Assert.Throws<ArgumentException>(
            () => ServerSideNoClobberPublishCommand.BuildDirectoryReservation(path));
    }
}
