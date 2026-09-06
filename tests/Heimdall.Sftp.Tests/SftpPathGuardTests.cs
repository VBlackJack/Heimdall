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

public sealed class SftpPathGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("///")]
    [InlineData(" / ")]
    public void IsProtectedRoot_ProtectsEmptyAndRootPaths(string? path)
    {
        Assert.True(SftpPathGuard.IsProtectedRoot(path));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("relative")]
    [InlineData("/var")]
    [InlineData("/var/")]
    [InlineData("//server")]
    public void IsProtectedRoot_AllowsNonRootPaths(string path)
    {
        Assert.False(SftpPathGuard.IsProtectedRoot(path));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("file.txt")]
    [InlineData("my dir")]
    [InlineData("my..file")]
    [InlineData(" release ")]
    public void IsValidChildName_AllowsSinglePathSegments(string name)
    {
        Assert.True(SftpPathGuard.IsValidChildName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("../etc")]
    [InlineData("x\0y")]
    [InlineData(@"a\b")]
    [InlineData("x\ny")]
    [InlineData("x\ry")]
    [InlineData("x\ty")]
    public void IsValidChildName_RejectsEmptyDotAndPathLikeNames(string? name)
    {
        Assert.False(SftpPathGuard.IsValidChildName(name));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("///")]
    public async Task SftpBrowserDeleteAsync_RejectsProtectedRootBeforeConnectionCheck(string path)
    {
        using SftpBrowser browser = new();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => browser.DeleteAsync(path));

        Assert.Contains("protected remote root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
