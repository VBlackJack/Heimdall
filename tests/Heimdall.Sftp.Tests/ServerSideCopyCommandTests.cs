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
using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Unit tests for <see cref="ServerSideCopyCommand"/>: exact non-clobbering command chains,
/// sibling-temp cleanup, exclusive directory-root reservation, metadata flags, the <c>--</c>
/// end-of-options guard, and single-quote shell escaping for every remote path (CWE-78).
/// </summary>
public sealed class ServerSideCopyCommandTests
{
    [Fact]
    public void Build_File_UsesPreserveFlagAndQuotesBothPaths()
    {
        string command = ServerSideCopyCommand.Build("/srv/a.txt", "/srv/b.txt", recursive: false);

        string temp = StagingToken(command);
        Assert.Equal(
            $"set -C; : > {temp} || exit $?; set +C; "
            + $"cp -p -- '/srv/a.txt' {temp} && ln -- {temp} '/srv/b.txt'; "
            + "status=$?; if [ $status -eq 0 ] && [ -L '/srv/b.txt' ]; then "
            + "rm -f -- '/srv/b.txt'; status=99; fi; "
            + $"rm -f -- {temp}; exit $status",
            command);
    }

    [Fact]
    public void Build_Directory_UsesArchiveFlagAndQuotesBothPaths()
    {
        string command = ServerSideCopyCommand.Build("/srv/data", "/srv/copy", recursive: true);

        Assert.Equal(
            "mkdir -- '/srv/copy' && cp -a -- '/srv/data'/. '/srv/copy'; "
            + "status=$?; if [ $status -ne 0 ]; then rm -rf -- '/srv/copy'; fi; exit $status",
            command);
    }

    /// <remarks>
    /// A cp -a that fails part way (permission denied on one subtree, quota, disk full) used
    /// to leave the reserved root and a partial tree on the server while the caller reported
    /// that the copy was not performed. The file branch cleaned up; the directory branch did
    /// not.
    /// </remarks>
    [Fact]
    public void Build_Directory_RemovesTheReservedRootWhenTheArchiveCopyFails()
    {
        string command = ServerSideCopyCommand.Build("/srv/data", "/srv/copy", recursive: true);

        int copyIndex = command.IndexOf("cp -a -- ", StringComparison.Ordinal);
        int statusIndex = command.IndexOf("status=$?", StringComparison.Ordinal);
        int cleanupIndex = command.IndexOf("rm -rf -- '/srv/copy'", StringComparison.Ordinal);
        int exitIndex = command.IndexOf("exit $status", StringComparison.Ordinal);

        Assert.True(copyIndex > 0 && statusIndex > copyIndex, "the status is captured after cp");
        Assert.True(cleanupIndex > statusIndex, "the reserved root is removed on failure");
        Assert.Contains("if [ $status -ne 0 ]", command, StringComparison.Ordinal);
        Assert.True(exitIndex > cleanupIndex, "cp's own status is what the command returns");
    }

    [Fact]
    public void Build_PathsWithSpacesAndSingleQuotes_AreShellEscaped()
    {
        string command = ServerSideCopyCommand.Build(
            "/srv/my dir/it's a file.txt",
            "/dst/o'brien",
            recursive: false);

        // EscapeShellArg wraps in single quotes and rewrites each embedded ' as '\'' .
        // The staging token is read back from the command because it now carries a fresh
        // GUID; what this still pins exactly is that the rewriting reaches inside it, which
        // matters more than before since the token embeds the destination's own quote.
        string escapedTemp = StagingToken(command);
        Assert.Equal(
            $"set -C; : > {escapedTemp} || exit $?; set +C; "
            + $"cp -p -- '/srv/my dir/it'\\''s a file.txt' {escapedTemp} "
            + $"&& ln -- {escapedTemp} '/dst/o'\\''brien'; "
            + "status=$?; if [ $status -eq 0 ] && [ -L '/dst/o'\\''brien' ]; then "
            + "rm -f -- '/dst/o'\\''brien'; status=99; fi; "
            + $"rm -f -- {escapedTemp}; exit $status",
            command);
    }

    [Fact]
    public void Build_DirectoryWithSpaces_UsesArchiveFlagAndEscapes()
    {
        string command = ServerSideCopyCommand.Build("/srv/my data", "/srv/my copy", recursive: true);

        Assert.StartsWith("mkdir -- '/srv/my copy' && cp -a -- '/srv/my data'/. '/srv/my copy'; ", command, StringComparison.Ordinal);
        Assert.Contains("rm -rf -- '/srv/my copy'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_File_WritesSiblingTempAndNeverCopiesDirectlyToDestination()
    {
        string command = ServerSideCopyCommand.Build(
            "/srv/source.txt",
            "/srv/destination.txt",
            recursive: false);
        string tempPath = StagingToken(command);

        Assert.Contains($"cp -p -- '/srv/source.txt' {tempPath}", command, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "cp -p -- '/srv/source.txt' '/srv/destination.txt' ",
            command,
            StringComparison.Ordinal);
        Assert.Contains($"ln -- {tempPath} '/srv/destination.txt'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_File_CleansTempAfterLinkFailureBeforeReturningLinkStatus()
    {
        string command = ServerSideCopyCommand.Build(
            "/srv/source.txt",
            "/srv/destination.txt",
            recursive: false);
        string tempPath = StagingToken(command);
        int linkIndex = command.IndexOf($"ln -- {tempPath}", StringComparison.Ordinal);
        int statusIndex = command.IndexOf("; status=$?;", StringComparison.Ordinal);
        int cleanupIndex = command.IndexOf($"rm -f -- {tempPath}", StringComparison.Ordinal);
        int exitIndex = command.IndexOf("exit $status", StringComparison.Ordinal);

        Assert.True(linkIndex >= 0);
        Assert.True(statusIndex > linkIndex);
        Assert.True(cleanupIndex > statusIndex);
        Assert.True(exitIndex > cleanupIndex);
    }

    [Fact]
    public void Build_File_GuardsEveryPathCommandAndEscapesEveryPath()
    {
        string command = ServerSideCopyCommand.Build(
            "/srv/my dir/it's.txt",
            "/dst/o'brien.txt",
            recursive: false);
        string escapedSource = "'/srv/my dir/it'\\''s.txt'";
        string escapedDestination = "'/dst/o'\\''brien.txt'";
        string escapedTemp = StagingToken(command);
        int copyStart = command.IndexOf("cp ", StringComparison.Ordinal);
        string copyCommand = command[copyStart..command.IndexOf(" && ", copyStart, StringComparison.Ordinal)];
        int linkStart = command.IndexOf("ln ", StringComparison.Ordinal);
        int linkEnd = command.IndexOf(';', linkStart);
        string linkCommand = command[linkStart..linkEnd];

        // The LAST rm is the staging cleanup. The chain now holds an earlier one, which
        // removes a destination that came out as a symlink, and reading that one instead
        // would assert the guard on the wrong command.
        int cleanupStart = command.LastIndexOf("rm ", StringComparison.Ordinal);
        int cleanupEnd = command.IndexOf(';', cleanupStart);
        string cleanupCommand = command[cleanupStart..cleanupEnd];

        Assert.Contains(" -- ", copyCommand, StringComparison.Ordinal);
        Assert.Contains(" -- ", linkCommand, StringComparison.Ordinal);
        Assert.Contains(" -- ", cleanupCommand, StringComparison.Ordinal);
        Assert.Contains(escapedSource, copyCommand, StringComparison.Ordinal);
        Assert.Contains(escapedTemp, copyCommand, StringComparison.Ordinal);
        Assert.Contains(escapedTemp, linkCommand, StringComparison.Ordinal);
        Assert.Contains(escapedDestination, linkCommand, StringComparison.Ordinal);
        Assert.Contains(escapedTemp, cleanupCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Directory_ReservesRootBeforeArchiveCopy()
    {
        string command = ServerSideCopyCommand.Build("/srv/source", "/srv/destination", recursive: true);
        int reserveIndex = command.IndexOf("mkdir -- '/srv/destination'", StringComparison.Ordinal);
        int copyIndex = command.IndexOf(
            "cp -a -- '/srv/source'/. '/srv/destination'",
            StringComparison.Ordinal);

        Assert.Equal(0, reserveIndex);
        Assert.True(copyIndex > reserveIndex);
    }

    /// <remarks>
    /// The staging name used to be `$$`, the remote shell's own PID. A user with write
    /// access to the destination directory had a few thousand candidate names, so they did
    /// not need to win a race: they could plant a symlink at each one in advance and wait
    /// for `cp -p` to open through it. The name is now drawn client-side, per call.
    /// </remarks>
    [Fact]
    public void Build_File_StagesUnderANameTheRemoteShellCannotPredictOrExpand()
    {
        string first = ServerSideCopyCommand.Build("/srv/a.txt", "/srv/b.txt", recursive: false);
        string second = ServerSideCopyCommand.Build("/srv/a.txt", "/srv/b.txt", recursive: false);

        // Both halves are needed. Two different strings rule out every remote expansion at
        // once - `$$`, `$RANDOM`, `$(date)` - because those produce two identical commands
        // and are resolved by the far end. The literal check names the historical form; the
        // chain legitimately contains `$?` and `$status`, never `$$`.
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("$$", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_File_UsesOneStagingNameForTheCopyTheLinkAndTheCleanup()
    {
        string command = ServerSideCopyCommand.Build("/srv/a.txt", "/srv/b.txt", recursive: false);

        // The backreference is the load-bearing part: one expression pins the sibling
        // directory, the 32 hex digits of a GUID, the SAME token in all three commands,
        // both `--` guards, the ordering and the exit-status capture. Drawing the name
        // three times instead of once passes every other assertion in this file and leaves
        // cp writing one path, ln linking a path that does not exist, and rm deleting
        // nothing.
        Assert.Matches(
            @"^set -C; : > '(/srv/b\.txt\.[0-9a-f]{32}\.part)' \|\| exit \$\?; set \+C; "
                + @"cp -p -- '/srv/a\.txt' '\1' && ln -- '\1' '/srv/b\.txt'; "
                + @"status=\$\?; if \[ \$status -eq 0 \] && \[ -L '/srv/b\.txt' \]; then "
                + @"rm -f -- '/srv/b\.txt'; status=99; fi; "
                + @"rm -f -- '\1'; exit \$status$",
            command);
    }

    /// <remarks>
    /// Measured on 2026-09-07 against BusyBox 1.36.1 and GNU coreutils 9.1 and 8.25: a
    /// staging file pre-created at 644 came out 600 after `cp -p` of a 600 source, so the
    /// reservation costs nothing in mode preservation; `set -C` refuses a name already
    /// taken by a symlink and writes nothing through it; and the whole chain exits
    /// non-zero, leaving the plant in place, because deleting a name it did not create is
    /// not this command's business.
    /// </remarks>
    [Fact]
    public void Build_File_ReservesTheStagingNameBeforeItCopiesIntoIt()
    {
        string command = ServerSideCopyCommand.Build("/srv/a.txt", "/srv/b.txt", recursive: false);

        int reservation = command.IndexOf("set -C; : > ", StringComparison.Ordinal);
        int copy = command.IndexOf("cp -p -- ", StringComparison.Ordinal);

        Assert.Equal(0, reservation);
        Assert.True(copy > reservation, command);
        // Without the guard the reservation is a comment: cp would create the file anyway.
        Assert.Contains("|| exit $?", command, StringComparison.Ordinal);
    }

    /// <remarks>
    /// `ln` given a symlink publishes the symlink itself and exits 0 - measured on both
    /// implementations. A staging file swapped between the copy and the link would
    /// otherwise leave the destination pointing wherever the attacker chose, reported to
    /// the user as a copy that worked.
    /// </remarks>
    [Fact]
    public void Build_File_RefusesADestinationThatCameOutASymlink()
    {
        string command = ServerSideCopyCommand.Build("/srv/a.txt", "/srv/b.txt", recursive: false);

        Assert.Contains("[ -L '/srv/b.txt' ]", command, StringComparison.Ordinal);
        Assert.Contains(
            $"rm -f -- '/srv/b.txt'; status={ServerSideCopyCommand.PublishedASymlinkStatus};",
            command,
            StringComparison.Ordinal);

        // The check must not fire on the ordinary path, where the link succeeded and the
        // destination is a hard link to a regular file.
        Assert.Contains("if [ $status -eq 0 ] &&", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The staging token of a built file command, read back from the command itself.
    /// </summary>
    /// <remarks>
    /// The name carries a fresh GUID per call, so a test cannot spell it in advance. It is
    /// read back rather than regenerated: a helper that built its own would agree with a
    /// broken implementation.
    /// </remarks>
    private static string StagingToken(string command)
    {
        Match match = Regex.Match(command, @"'[^']*(?:'\\''[^']*)*\.[0-9a-f]{32}\.part'");
        Assert.True(match.Success, $"no staging token in: {command}");
        return match.Value;
    }
}
