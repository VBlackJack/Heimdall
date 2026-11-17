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
    // The constant scripts the commands hand to sh -c, spelled out here on their own rather than read
    // from the production constants: a test that quoted the implementation would agree with a broken one.
    private const string DirectoryScript = "umask 077; mkdir -- \"$2\" || exit $?; "
        + "cp -a -- \"$1\"/. \"$2\" || { status=$?; rm -rf -- \"$2\"; exit $status; }";

    private const string FileScript = "umask 077; set -C; : > \"$3\" || exit $?; set +C; "
        + "cp -p -- \"$1\" \"$3\" && ln -T -- \"$3\" \"$2\"; "
        + "status=$?; if [ $status -eq 0 ] && [ -L \"$2\" ]; then "
        + "rm -f -- \"$2\"; status=99; fi; "
        + "rm -f -- \"$3\"; exit $status";

    [Fact]
    public void Build_File_UsesPreserveFlagAndQuotesBothPaths()
    {
        string command = ServerSideCopyCommand.Build("/srv/a.txt", "/srv/b.txt", recursive: false);

        string temp = StagingToken(command);
        Assert.Equal($"sh -c '{FileScript}' sh '/srv/a.txt' '/srv/b.txt' {temp}", command);
    }

    [Fact]
    public void Build_Directory_UsesArchiveFlagAndQuotesBothPaths()
    {
        string command = ServerSideCopyCommand.Build("/srv/data", "/srv/copy", recursive: true);

        Assert.Equal($"sh -c '{DirectoryScript}' sh '/srv/data' '/srv/copy'", command);
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
        int cleanupIndex = command.IndexOf("rm -rf -- \"$2\"", StringComparison.Ordinal);
        int exitIndex = command.IndexOf("exit $status", StringComparison.Ordinal);

        Assert.True(copyIndex > 0 && statusIndex > copyIndex, "the status is captured after cp");
        Assert.True(cleanupIndex > statusIndex, "the reserved root is removed on failure");
        Assert.True(exitIndex > cleanupIndex, "cp's own status is what the command returns");
    }

    /// <remarks>
    /// The chain used to be `mkdir -- d && cp -a ...; status=$?; if [ $status -ne 0 ]; then
    /// rm -rf -- d; fi`. When mkdir failed because the name had been taken in the meantime,
    /// by another client creating that directory, the status was mkdir's, and the cleanup
    /// removed a tree this command never created. Measured on the docker bench: with the
    /// cleanup reachable only from a failed cp, a pre-existing directory and its content
    /// survive a refused reservation, and a failed copy still removes its own root.
    /// </remarks>
    [Fact]
    public void Build_Directory_CleanupIsReachableOnlyOnceTheReservationSucceeded()
    {
        string command = ServerSideCopyCommand.Build("/srv/data", "/srv/copy", recursive: true);

        int reservation = command.IndexOf("mkdir -- \"$2\" || exit $?;", StringComparison.Ordinal);
        int cleanup = command.IndexOf("rm -rf -- \"$2\"", StringComparison.Ordinal);

        Assert.True(reservation >= 0, "a refused reservation must end the chain with its own status");
        Assert.True(cleanup > reservation, command);

        // The cleanup belongs to the copy's failure branch and to nothing else.
        Assert.Contains(
            "cp -a -- \"$1\"/. \"$2\" || { status=$?; rm -rf -- \"$2\"; exit $status; }",
            command,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(command, Regex.Escape("rm -rf")));
    }

    /// <remarks>
    /// The staging reservation `: > temp` used to create the file with the exec session's
    /// umask, 0644 on a default server, and `cp -p` writes the content first and applies
    /// the source's mode only at the end: a 0600 source was world-readable for the whole
    /// copy. Measured on the docker bench (GNU coreutils and BusyBox 1.37 cp): under
    /// `umask 077` the reservation is 0600, and `cp -p` still ends with the source's mode,
    /// 0600 for a 0600 source and 0644 for a 0644 one; `cp -a` restores every mode of a
    /// tree, the root included, so the directory branch gets the same tightening.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_TightensTheUmaskBeforeCreatingAnything(bool recursive)
    {
        string command = ServerSideCopyCommand.Build("/srv/data", "/srv/copy", recursive);

        Assert.StartsWith("sh -c 'umask 077; ", command, StringComparison.Ordinal);

        int umask = command.IndexOf("umask 077;", StringComparison.Ordinal);
        int firstCreation = recursive
            ? command.IndexOf("mkdir -- ", StringComparison.Ordinal)
            : command.IndexOf(": > ", StringComparison.Ordinal);
        Assert.True(firstCreation > umask, command);
    }

    /// <remarks>
    /// Without -T, `ln -- temp dest` given a dest that became a directory in the meantime
    /// creates the link INSIDE it and exits 0, and the copy is reported as done at a path
    /// that does not hold it. Measured: GNU ln -T and BusyBox 1.37 ln -T both refuse with
    /// exit 1; plain GNU ln linked into the directory and exited 0. An older BusyBox
    /// without -T rejects the option, which the caller reports as a refused copy.
    /// </remarks>
    [Fact]
    public void Build_File_LinksOntoTheNameNeverIntoADirectory()
    {
        string command = ServerSideCopyCommand.Build("/srv/a.txt", "/srv/b.txt", recursive: false);

        Assert.Contains("ln -T -- \"$3\" \"$2\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("ln -- ", command, StringComparison.Ordinal);
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
            $"sh -c '{FileScript}' sh '/srv/my dir/it'\\''s a file.txt' '/dst/o'\\''brien' {escapedTemp}",
            command);
    }

    [Fact]
    public void Build_DirectoryWithSpaces_UsesArchiveFlagAndEscapes()
    {
        string command = ServerSideCopyCommand.Build("/srv/my data", "/srv/my copy", recursive: true);

        Assert.EndsWith("' sh '/srv/my data' '/srv/my copy'", command, StringComparison.Ordinal);
        Assert.Contains("rm -rf -- \"$2\"", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_File_WritesSiblingTempAndNeverCopiesDirectlyToDestination()
    {
        string command = ServerSideCopyCommand.Build(
            "/srv/source.txt",
            "/srv/destination.txt",
            recursive: false);
        string tempPath = StagingToken(command);

        Assert.Contains("cp -p -- \"$1\" \"$3\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "cp -p -- \"$1\" \"$2\"",
            command,
            StringComparison.Ordinal);
        Assert.Contains("ln -T -- \"$3\" \"$2\"", command, StringComparison.Ordinal);
        Assert.EndsWith(
            $" sh '/srv/source.txt' '/srv/destination.txt' {tempPath}",
            command,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Build_File_CleansTempAfterLinkFailureBeforeReturningLinkStatus()
    {
        string command = ServerSideCopyCommand.Build(
            "/srv/source.txt",
            "/srv/destination.txt",
            recursive: false);
        _ = StagingToken(command);
        int linkIndex = command.IndexOf("ln -T -- \"$3\"", StringComparison.Ordinal);
        int statusIndex = command.IndexOf("; status=$?;", StringComparison.Ordinal);
        int cleanupIndex = command.IndexOf("rm -f -- \"$3\"", StringComparison.Ordinal);
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
        // Inside the script the paths are the positional parameters, never the escaped text itself.
        string sourceParameter = "\"$1\"";
        string destinationParameter = "\"$2\"";
        string tempParameter = "\"$3\"";
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
        Assert.Contains(sourceParameter, copyCommand, StringComparison.Ordinal);
        Assert.Contains(tempParameter, copyCommand, StringComparison.Ordinal);
        Assert.Contains(tempParameter, linkCommand, StringComparison.Ordinal);
        Assert.Contains(destinationParameter, linkCommand, StringComparison.Ordinal);
        Assert.Contains(tempParameter, cleanupCommand, StringComparison.Ordinal);
        Assert.EndsWith($" sh {escapedSource} {escapedDestination} {escapedTemp}", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Directory_ReservesRootBeforeArchiveCopy()
    {
        string command = ServerSideCopyCommand.Build("/srv/source", "/srv/destination", recursive: true);
        int reserveIndex = command.IndexOf("mkdir -- \"$2\"", StringComparison.Ordinal);
        int copyIndex = command.IndexOf(
            "cp -a -- \"$1\"/. \"$2\"",
            StringComparison.Ordinal);

        Assert.True(reserveIndex > 0, command);
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

        // One expression pins the constant script, the sibling directory and the 32 hex digits
        // of a GUID in the single staging argument, the three paths in the order the script
        // reads them as $1, $2 and $3, both `--` guards, the ordering and the exit-status
        // capture. The script names its staging path once, as $3, and uses it for the
        // reservation, the copy, the link and the cleanup, so the name cannot be drawn
        // differently for each of them.
        Assert.Matches(
            @"^sh -c 'umask 077; set -C; : > ""\$3"" \|\| exit \$\?; set \+C; "
                + @"cp -p -- ""\$1"" ""\$3"" && ln -T -- ""\$3"" ""\$2""; "
                + @"status=\$\?; if \[ \$status -eq 0 \] && \[ -L ""\$2"" \]; then "
                + @"rm -f -- ""\$2""; status=99; fi; "
                + @"rm -f -- ""\$3""; exit \$status' "
                + @"sh '/srv/a\.txt' '/srv/b\.txt' '/srv/b\.txt\.[0-9a-f]{32}\.part'$",
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

        Assert.True(reservation > 0, command);
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

        Assert.Contains("[ -L \"$2\" ]", command, StringComparison.Ordinal);
        Assert.Contains(
            $"rm -f -- \"$2\"; status={ServerSideCopyCommand.PublishedASymlinkStatus};",
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
