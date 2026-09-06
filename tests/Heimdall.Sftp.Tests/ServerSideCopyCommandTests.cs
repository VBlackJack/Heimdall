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

        Assert.Equal(
            "cp -p -- '/srv/a.txt' '/srv/b.txt'.$$.part && ln -- '/srv/b.txt'.$$.part '/srv/b.txt'; "
            + "status=$?; rm -f -- '/srv/b.txt'.$$.part; exit $status",
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
        Assert.Equal(
            "cp -p -- '/srv/my dir/it'\\''s a file.txt' '/dst/o'\\''brien'.$$.part "
            + "&& ln -- '/dst/o'\\''brien'.$$.part '/dst/o'\\''brien'; status=$?; "
            + "rm -f -- '/dst/o'\\''brien'.$$.part; exit $status",
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
        string tempPath = "'/srv/destination.txt'.$$.part";

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
        string tempPath = "'/srv/destination.txt'.$$.part";
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
        string escapedTemp = $"{escapedDestination}.$$.part";
        string copyCommand = command[..command.IndexOf(" && ", StringComparison.Ordinal)];
        int linkStart = command.IndexOf("ln ", StringComparison.Ordinal);
        int linkEnd = command.IndexOf(';', linkStart);
        string linkCommand = command[linkStart..linkEnd];
        int cleanupStart = command.IndexOf("rm ", StringComparison.Ordinal);
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
}
