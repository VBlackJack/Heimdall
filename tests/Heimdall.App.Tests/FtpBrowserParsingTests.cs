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
    [InlineData("/var/log/../etc", "/var/etc")]
    [InlineData("/var/./log", "/var/log")]
    [InlineData("/../etc", "/etc")]
    [InlineData("/var/log/..", "/var")]
    public void NormalizePath_ProducesCanonicalForm(string? input, string expected)
    {
        Assert.Equal(expected, FtpBrowser.NormalizePath(input!));
    }

    [Theory]
    [InlineData("/etc/passwd", "/var/log", "/etc/passwd")]
    [InlineData("logs", "/var", "/var/logs")]
    [InlineData("logs", "/", "/logs")]
    // Collapsed, not accumulated: the current directory used to grow a "/.." per step and
    // every listed path inherited the spelling.
    [InlineData("../etc", "/var/log", "/var/etc")]
    [InlineData("..", "/", "/")]
    [InlineData("./logs", "/var", "/var/logs")]
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

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/etc")!;

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

    /// <remarks>
    /// The server names the entry, and the name became a path every operation trusted:
    /// delete, download, rename, and the privileged fallbacks. A name that is not one clean
    /// segment is refused at the boundary, where the untrusted value enters.
    /// </remarks>
    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("x\ny")]
    [InlineData("..")]
    public void MapFtpItemToFileInfo_UnsafeName_IsRefused(string name)
    {
        FtpListItem item = new FtpListItem
        {
            Name = name,
            Type = FtpObjectType.File,
            Size = 1,
            Modified = new DateTime(2026, 1, 15, 12, 34, 0, DateTimeKind.Local),
        };

        Assert.Null(FtpBrowser.MapFtpItemToFileInfo(item, "/srv"));
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

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/srv")!;

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

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/srv")!;

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

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/srv")!;

        Assert.Equal(RemoteEntryKind.SymbolicLink, entry.Kind);
    }

    // The explicit regular-file type character. Without this the '-' arm is indistinguishable from the
    // mode-only path that also answers File, so nothing would notice if it stopped classifying.
    [Theory]
    [InlineData("-rw-r--r--", RemoteEntryKind.File)]
    [InlineData("drwxr-xr-x", RemoteEntryKind.Directory)]
    [InlineData("lrwxrwxrwx", RemoteEntryKind.SymbolicLink)]
    public void MapFtpItemToFileInfo_ExplicitTypeCharacter_MapsThatType(
        string rawPermissions,
        RemoteEntryKind expectedKind)
    {
        FtpListItem item = new FtpListItem
        {
            Name = "entry",
            Type = FtpObjectType.File,
            RawPermissions = rawPermissions,
        };

        Assert.Equal(expectedKind, FtpBrowser.MapFtpItemToFileInfo(item, "/srv")!.Kind);
    }

    // A nine-character value is mode-only and carries no type character at all. Reading its first
    // character as a type would classify an ordinary file by whichever permission bit came first, so a
    // mode-only listing must keep the type the library reported.
    [Fact]
    public void MapFtpItemToFileInfo_FileTypeWithModeOnlyPermissions_StaysAFile()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "notes.txt",
            Type = FtpObjectType.File,
            RawPermissions = "rw-r--r--",
        };

        Assert.Equal(RemoteEntryKind.File, FtpBrowser.MapFtpItemToFileInfo(item, "/srv")!.Kind);
    }

    // A type value this build cannot interpret, with nothing in the listing to rescue it. Answering
    // "file" here is what made an unclassifiable entry transferable.
    [Fact]
    public void MapFtpItemToFileInfo_UnrecognisedObjectTypeWithoutPermissions_IsUnknown()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "mystery",
            Type = (FtpObjectType)999,
            RawPermissions = string.Empty,
        };

        Assert.Equal(RemoteEntryKind.Unknown, FtpBrowser.MapFtpItemToFileInfo(item, "/srv")!.Kind);
    }

    // The server did state a type character and this build does not recognise it. That is a positive
    // statement that the entry is not a regular file, not an absence of information.
    [Fact]
    public void MapFtpItemToFileInfo_UnrecognisedTypeCharacter_IsUnknown()
    {
        FtpListItem item = new FtpListItem
        {
            Name = "mystery",
            Type = FtpObjectType.File,
            RawPermissions = "?rw-r--r--",
        };

        Assert.Equal(RemoteEntryKind.Unknown, FtpBrowser.MapFtpItemToFileInfo(item, "/srv")!.Kind);
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

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/dev")!;

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

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/srv")!;

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

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/srv")!;

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

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/tmp")!;

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

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/")!;

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

        SftpFileInfo entry = FtpBrowser.MapFtpItemToFileInfo(item, "/")!;

        Assert.Equal("rwxr-xr-x", entry.Permissions);
        Assert.Equal("-", entry.Owner);
        Assert.Equal("-", entry.Group);
    }
}
