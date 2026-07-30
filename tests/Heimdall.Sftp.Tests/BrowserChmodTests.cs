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

namespace Heimdall.Sftp.Tests;

public sealed class BrowserChmodTests
{
    [Fact]
    public async Task FtpBrowser_ChmodAsync_ThrowsNotSupportedException()
    {
        using FtpBrowser browser = new();

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => browser.ChmodAsync("/srv/file.txt", 0x1ED));

        Assert.Equal(
            "Changing POSIX permissions is not supported for FTP connections.",
            exception.Message);
    }

    [Fact]
    public void SftpBrowser_ChmodSource_PreservesAllNinePermissionBitsAndPersistsAttributes()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "Heimdall.Sftp",
            "SftpBrowser.cs"));
        int methodStart = source.IndexOf(
            "public async Task ChmodAsync",
            StringComparison.Ordinal);
        int methodEnd = source.IndexOf(
            "public async Task RenameAsync",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0, "SftpBrowser.ChmodAsync was not found.");
        Assert.True(methodEnd > methodStart, "The end of SftpBrowser.ChmodAsync was not found.");

        string method = source[methodStart..methodEnd];
        string[] assignments =
        [
            "attrs.OwnerCanRead = (mode & 0x100) != 0;",
            "attrs.OwnerCanWrite = (mode & 0x080) != 0;",
            "attrs.OwnerCanExecute = (mode & 0x040) != 0;",
            "attrs.GroupCanRead = (mode & 0x020) != 0;",
            "attrs.GroupCanWrite = (mode & 0x010) != 0;",
            "attrs.GroupCanExecute = (mode & 0x008) != 0;",
            "attrs.OthersCanRead = (mode & 0x004) != 0;",
            "attrs.OthersCanWrite = (mode & 0x002) != 0;",
            "attrs.OthersCanExecute = (mode & 0x001) != 0;",
        ];

        Assert.All(assignments, assignment => Assert.Contains(assignment, method, StringComparison.Ordinal));
        Assert.Contains("client.SetAttributes(path, attrs);", method, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
