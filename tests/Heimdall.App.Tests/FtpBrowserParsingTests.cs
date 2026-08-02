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

using FluentFTP;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class FtpBrowserParsingTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("/var/log", "/var/log")]
    [InlineData("var/log", "/var/log")]
    [InlineData("/var/log/", "/var/log")]
    [InlineData("/var/log///", "/var/log")]
    public void NormalizePath_ProducesCanonicalForm(string? input, string expected)
    {
        Assert.Equal(expected, FtpBrowser.NormalizePath(input!));
    }

    [Theory]
    [InlineData("/etc/passwd", "/var/log", "/etc/passwd")]
    [InlineData("logs", "/var", "/var/logs")]
    [InlineData("logs", "/", "/logs")]
    [InlineData("../etc", "/var/log", "/var/log/../etc")]
    public void ResolvePath_HandlesAbsoluteAndRelative(
        string input,
        string currentDirectory,
        string expected)
    {
        Assert.Equal(expected, FtpBrowser.ResolvePath(input, currentDirectory));
    }

    [Fact]
    public void MapFtpItemToFileInfo_File_MapsContractFields()
    {
        DateTime modified = new DateTime(2026, 1, 15, 12, 34, 0, DateTimeKind.Local);
        FtpListItem item = new FtpListItem
        {
            Name = "hosts",
            Type = FtpObjectType.File,
            Size = 4096,
            Modified = modified,
            RawPermissions = "rw-r--r--",
            RawOwner = "root",
            RawGroup = "wheel",
        };

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/etc");

        Assert.Equal("hosts", entry.Name);
        Assert.Equal("/etc/hosts", entry.FullPath);
        Assert.False(entry.IsDirectory);
        Assert.Equal(4096, entry.Size);
        Assert.Equal(DateTimeKind.Utc, entry.LastModified.Kind);
        Assert.Equal(modified.Ticks, entry.LastModified.Ticks);
        Assert.Equal("rw-r--r--", entry.Permissions);
        Assert.Equal("root", entry.Owner);
        Assert.Equal("wheel", entry.Group);
    }

    [Fact]
    public void MapFtpItemToFileInfo_Directory_ForcesSizeToZero()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "archive",
            Type = FtpObjectType.Directory,
            Size = 4096,
            RawPermissions = "rwxr-xr-x",
        };

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/srv");

        Assert.True(entry.IsDirectory);
        Assert.Equal(0, entry.Size);
        Assert.Equal("archive", entry.Name);
        Assert.Equal("/srv/archive", entry.FullPath);
        Assert.Equal("rwxr-xr-x", entry.Permissions);
    }

    [Fact]
    public void MapFtpItemToFileInfo_Link_MapsAsNonDirectoryWithCleanName()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "link",
            Type = FtpObjectType.Link,
            Size = 7,
            LinkTarget = "/target",
            RawPermissions = "rwxrwxrwx",
        };

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/srv");

        Assert.False(entry.IsDirectory);
        Assert.Equal("link", entry.Name);
        Assert.Equal("/srv/link", entry.FullPath);
        Assert.Equal(7, entry.Size);
        Assert.Equal("rwxrwxrwx", entry.Permissions);
    }

    [Fact]
    public void MapFtpItemToFileInfo_Link_MapsSymbolicLinkKind()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "current",
            Type = FtpObjectType.Link,
            RawPermissions = "drwxr-xr-x",
        };

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/srv");

        Assert.Equal(RemoteEntryKind.SymbolicLink, entry.Kind);
    }

    [Theory]
    [InlineData("prw-r--r--", RemoteEntryKind.Fifo)]
    [InlineData("srw-r--r--", RemoteEntryKind.Socket)]
    [InlineData("crw-r--r--", RemoteEntryKind.Device)]
    [InlineData("brw-r--r--", RemoteEntryKind.Device)]
    public void MapFtpItemToFileInfo_FileType_MapsRawPermissionKind(
        string rawPermissions,
        RemoteEntryKind expectedKind)
    {
        FtpListItem item = new FtpListItem
        {
            Name = "special",
            Type = FtpObjectType.File,
            RawPermissions = rawPermissions,
        };

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/dev");

        Assert.Equal(expectedKind, entry.Kind);
    }

    [Fact]
    public void MapFtpItemToFileInfo_FileWithEmptyRawPermissions_MapsFileKind()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "readme.txt",
            Type = FtpObjectType.File,
            RawPermissions = string.Empty,
        };

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/srv");

        Assert.Equal(RemoteEntryKind.File, entry.Kind);
    }

    [Fact]
    public void MapFtpItemToFileInfo_Directory_MapsDirectoryKindAndForcesSizeToZero()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "archive",
            Type = FtpObjectType.Directory,
            Size = 4096,
            RawPermissions = "-rw-r--r--",
        };

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/srv");

        Assert.Equal(RemoteEntryKind.Directory, entry.Kind);
        Assert.Equal(0, entry.Size);
    }

    [Fact]
    public void MapFtpItemToFileInfo_NegativeFileSize_ClampsToZero()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "unknown.bin",
            Type = FtpObjectType.File,
            Size = -1,
        };

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/tmp");

        Assert.Equal(0, entry.Size);
    }

    [Fact]
    public void MapFtpItemToFileInfo_MissingFileMetadata_UsesFallbacks()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "readme.txt",
            Type = FtpObjectType.File,
        };

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/");

        Assert.Equal("rw-r--r--", entry.Permissions);
        Assert.Equal("-", entry.Owner);
        Assert.Equal("-", entry.Group);
        Assert.Equal(DateTimeKind.Utc, entry.LastModified.Kind);
        Assert.Equal(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), entry.LastModified);
    }

    [Fact]
    public void MapFtpItemToFileInfo_MissingDirectoryMetadata_UsesDirectoryFallback()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "subfolder",
            Type = FtpObjectType.Directory,
        };

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/");

        Assert.Equal("rwxr-xr-x", entry.Permissions);
        Assert.Equal("-", entry.Owner);
        Assert.Equal("-", entry.Group);
    }
}
