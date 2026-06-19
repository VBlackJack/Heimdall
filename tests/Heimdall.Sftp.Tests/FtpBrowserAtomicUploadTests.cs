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

public sealed class FtpBrowserAtomicUploadTests
{
    [Fact]
    public void CreateUploadTempRemotePath_KeepsSameRemoteDirectoryAndUsesSlashSeparators()
    {
        string tempPath = FtpBrowser.CreateUploadTempRemotePath("/srv/app/config.txt");

        Assert.StartsWith("/srv/app/config.txt.", tempPath, StringComparison.Ordinal);
        Assert.EndsWith(".part", tempPath, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', tempPath);
        Assert.Equal("/srv/app", GetRemoteDirectory(tempPath));
    }

    [Fact]
    public void CreateUploadTempRemotePath_ReturnsDistinctPath()
    {
        string finalPath = "/srv/app/config.txt";
        string tempPath = FtpBrowser.CreateUploadTempRemotePath(finalPath);

        Assert.NotEqual(finalPath, tempPath);
    }

    [Fact]
    public void CreateUploadTempRemotePath_GeneratesUniquePaths()
    {
        string first = FtpBrowser.CreateUploadTempRemotePath("/srv/app/config.txt");
        string second = FtpBrowser.CreateUploadTempRemotePath("/srv/app/config.txt");

        Assert.NotEqual(first, second);
    }

    private static string GetRemoteDirectory(string remotePath)
    {
        int separator = remotePath.LastIndexOf('/');
        return separator <= 0 ? "/" : remotePath[..separator];
    }
}
