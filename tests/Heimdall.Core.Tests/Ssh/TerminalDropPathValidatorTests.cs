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

using Heimdall.Core.Ssh;

namespace Heimdall.Core.Tests.Ssh;

public sealed class TerminalDropPathValidatorTests
{
    [Theory]
    [InlineData(null, ".")]
    [InlineData("", ".")]
    [InlineData("   ", ".")]
    [InlineData("relative/path", ".")]
    [InlineData("/home/user/../.ssh", ".")]
    [InlineData("/tmp/foo\0bar", ".")]
    [InlineData("/tmp/foo\nbar", ".")]
    [InlineData("/tmp\\evil", ".")]
    [InlineData("/", "/")]
    [InlineData("/var/log/", "/var/log")]
    [InlineData("/home/admin/work", "/home/admin/work")]
    public void SanitizeRemoteDirectory_ValidatesAndNormalizes(string? input, string expected)
    {
        Assert.Equal(expected, TerminalDropPathValidator.SanitizeRemoteDirectory(input));
    }

    [Theory]
    [InlineData("/var/log", "app.conf", true, "/var/log/app.conf")]
    [InlineData("/", "motd", true, "/motd")]
    [InlineData(".", "notes.txt", true, "notes.txt")]
    [InlineData("/tmp", "../etc/passwd", false, "")]
    [InlineData("/tmp", "sub/file.txt", false, "")]
    [InlineData("/tmp", "..", false, "")]
    [InlineData("/tmp", "a\0b", false, "")]
    public void TryCombineRemoteFilePath_RejectsTraversalAndCombinesSafely(
        string remoteDir,
        string fileName,
        bool expectedOk,
        string expectedPath)
    {
        bool ok = TerminalDropPathValidator.TryCombineRemoteFilePath(remoteDir, fileName, out string result);

        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedPath, result);
    }
}
