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
/// Unit tests for <see cref="ServerSideCopyCommand"/>: the exact <c>cp</c> command string, the
/// <c>-p</c> (file) vs <c>-a</c> (directory) flag selection, the <c>--</c> end-of-options guard, and
/// the single-quote shell escaping applied to both paths (CWE-78).
/// </summary>
public sealed class ServerSideCopyCommandTests
{
    [Fact]
    public void Build_File_UsesPreserveFlagAndQuotesBothPaths()
    {
        string command = ServerSideCopyCommand.Build("/srv/a.txt", "/srv/b.txt", recursive: false);

        Assert.Equal("cp -p -- '/srv/a.txt' '/srv/b.txt'", command);
    }

    [Fact]
    public void Build_Directory_UsesArchiveFlagAndQuotesBothPaths()
    {
        string command = ServerSideCopyCommand.Build("/srv/data", "/srv/copy", recursive: true);

        Assert.Equal("cp -a -- '/srv/data' '/srv/copy'", command);
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
            "cp -p -- '/srv/my dir/it'\\''s a file.txt' '/dst/o'\\''brien'",
            command);
    }

    [Fact]
    public void Build_DirectoryWithSpaces_UsesArchiveFlagAndEscapes()
    {
        string command = ServerSideCopyCommand.Build("/srv/my data", "/srv/my copy", recursive: true);

        Assert.Equal("cp -a -- '/srv/my data' '/srv/my copy'", command);
    }
}
