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

using System.Globalization;
using FluentAssertions;
using Heimdall.App.ViewModels;
using Heimdall.Sftp;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSftpViewModelHelpersTests
{
    [Theory]
    [InlineData("/a/b/c", "/a/b")]
    [InlineData("/a", "/")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/a/b/", "/a")]
    public void GetParentPath_ReturnsExpectedParent(string path, string expected)
    {
        string actual = EmbeddedSftpViewModel.GetParentPath(path);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/a", "b", "/a/b")]
    [InlineData("/a/", "b", "/a/b")]
    [InlineData("/", "b", "/b")]
    public void CombineRemotePath_ReturnsExpectedPath(string directory, string name, string expected)
    {
        string actual = EmbeddedSftpViewModel.CombineRemotePath(directory, name);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveDropTargetDirectory_DirectoryHovered_ReturnsItsFullPath()
    {
        SftpFileInfo folder = new(
            Name: "uploads",
            FullPath: "/srv/uploads",
            IsDirectory: true,
            Size: 0,
            LastModified: default,
            Permissions: "rwxr-xr-x",
            Owner: "0",
            Group: "0");

        string actual = EmbeddedSftpViewModel.ResolveDropTargetDirectory(folder, "/srv");

        Assert.Equal("/srv/uploads", actual);
    }

    [Fact]
    public void ResolveDropTargetDirectory_FileHovered_ReturnsCurrentDirectory()
    {
        SftpFileInfo file = new(
            Name: "notes.txt",
            FullPath: "/srv/notes.txt",
            IsDirectory: false,
            Size: 12,
            LastModified: default,
            Permissions: "rw-r--r--",
            Owner: "0",
            Group: "0");

        string actual = EmbeddedSftpViewModel.ResolveDropTargetDirectory(file, "/srv");

        Assert.Equal("/srv", actual);
    }

    [Fact]
    public void ResolveDropTargetDirectory_NoRowHovered_ReturnsCurrentDirectory()
    {
        string actual = EmbeddedSftpViewModel.ResolveDropTargetDirectory(null, "/srv");

        Assert.Equal("/srv", actual);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void CanPasteFromExternalClipboard_ReturnsTrueOnlyWhenClipboardHasFileDropAndConnected(
        bool clipboardHasFileDrop,
        bool isConnected,
        bool expected)
    {
        bool actual = EmbeddedSftpViewModel.CanPasteFromExternalClipboard(
            clipboardHasFileDrop,
            isConnected);

        actual.Should().Be(expected);
    }

    [Fact]
    public void BuildNonCollidingName_FreeName_ReturnedUnchanged()
    {
        string actual = EmbeddedSftpViewModel.BuildNonCollidingName(["other.txt"], "report.txt");

        Assert.Equal("report.txt", actual);
    }

    [Theory]
    // Single collision inserts " (copy)" before the extension.
    [InlineData("report.txt", new[] { "report.txt" }, "report (copy).txt")]
    // Second collision bumps the counter, extension still preserved.
    [InlineData("report.txt", new[] { "report.txt", "report (copy).txt" }, "report (copy 2).txt")]
    // No extension (a directory name) keeps the suffix at the end.
    [InlineData("data", new[] { "data" }, "data (copy)")]
    // A dotfile has no extension: the suffix goes after the whole name.
    [InlineData(".bashrc", new[] { ".bashrc" }, ".bashrc (copy)")]
    // Only the last extension segment is preserved.
    [InlineData("archive.tar.gz", new[] { "archive.tar.gz" }, "archive.tar (copy).gz")]
    public void BuildNonCollidingName_Collision_InsertsCopySuffixPreservingExtension(
        string desired,
        string[] existing,
        string expected)
    {
        string actual = EmbeddedSftpViewModel.BuildNonCollidingName(existing, desired);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("rwxr-xr-x", "755")]
    [InlineData("rw-r--r--", "644")]
    [InlineData("rwxrwxrwx", "777")]
    [InlineData("---------", "000")]
    [InlineData("", "000")]
    [InlineData("rwx", "000")]
    public void PermissionsToOctal_ReturnsExpectedOctal(string permissions, string expected)
    {
        string actual = EmbeddedSftpViewModel.PermissionsToOctal(permissions);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsPermissionDenied_ReturnsTrueForSftpPermissionDenied()
    {
        bool actual = EmbeddedSftpViewModel.IsPermissionDenied(
            new SftpPermissionDeniedException("permission denied"));

        Assert.True(actual);
    }

    [Fact]
    public void IsPermissionDenied_ReturnsTrueForUnauthorizedAccess()
    {
        bool actual = EmbeddedSftpViewModel.IsPermissionDenied(
            new UnauthorizedAccessException("permission denied"));

        Assert.True(actual);
    }

    [Fact]
    public void IsPermissionDenied_ReturnsFalseForUnrelatedException()
    {
        bool actual = EmbeddedSftpViewModel.IsPermissionDenied(
            new InvalidOperationException("not a permission error"));

        Assert.False(actual);
    }

    [Fact]
    public void SudoUploadCommandsBuild_EscapesPathsAndBuildsWriteAndCleanupCommands()
    {
        (string write, string cleanup) = SudoUploadCommands.Build(
            "/tmp/o'reilly",
            "/var/log/oh's.log");

        Assert.Equal(
            @"cp -- '/tmp/o'\''reilly' '/var/log/oh'\''s.log'",
            write);
        Assert.Equal(@"rm -f '/tmp/o'\''reilly'", cleanup);
    }

    [Fact]
    public void ParseLsOutput_ParsesRegularFile()
    {
        const string output = "-rw-r--r-- 1 root wheel 1234 2026-05-20 14:30 report.txt\n";

        IReadOnlyList<SftpFileInfo> entries = EmbeddedSftpViewModel.ParseLsOutput(output, "/var/reports");

        Assert.Single(entries);
        SftpFileInfo entry = entries[0];
        Assert.Equal("report.txt", entry.Name);
        Assert.Equal("/var/reports/report.txt", entry.FullPath);
        Assert.False(entry.IsDirectory);
        Assert.Equal(1234, entry.Size);
        Assert.Equal("root", entry.Owner);
        Assert.Equal("wheel", entry.Group);
        Assert.NotEqual(default, entry.LastModified);
    }

    [Fact]
    public void ParseLsOutput_ParsesSizeAndTimestampWithInvariantCulture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");
            const string output = "-rw-r--r-- 1 root wheel 1234 2026-05-20 14:30 report.txt\n";

            IReadOnlyList<SftpFileInfo> entries = EmbeddedSftpViewModel.ParseLsOutput(output, "/var/reports");

            Assert.Single(entries);
            Assert.Equal(1234, entries[0].Size);
            Assert.Equal(new DateTime(2026, 5, 20, 14, 30, 0), entries[0].LastModified);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void ParseLsOutput_ParsesDirectory()
    {
        const string output = "drwxr-xr-x 2 root root 4096 2026-05-20 14:30 logs\n";

        IReadOnlyList<SftpFileInfo> entries = EmbeddedSftpViewModel.ParseLsOutput(output, "/var");

        Assert.Single(entries);
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("logs", entries[0].Name);
        Assert.Equal("/var/logs", entries[0].FullPath);
    }

    [Fact]
    public void ParseLsOutput_SkipsTotalCurrentParentAndMalformedLines()
    {
        string output = string.Join('\n',
            "total 16",
            "drwxr-xr-x 2 root root 4096 2026-05-20 14:30 .",
            "drwxr-xr-x 2 root root 4096 2026-05-20 14:30 ..",
            "malformed",
            "-rw-r--r-- 1 root root 10 2026-05-20 14:30 keep.txt");

        IReadOnlyList<SftpFileInfo> entries = EmbeddedSftpViewModel.ParseLsOutput(output, "/tmp");

        Assert.Single(entries);
        Assert.Equal("keep.txt", entries[0].Name);
    }

    [Fact]
    public void ParseLsOutput_StripsSymlinkTargetSuffix()
    {
        const string output = "lrwxrwxrwx 1 root root 9 2026-05-20 14:30 current -> releases\n";

        IReadOnlyList<SftpFileInfo> entries = EmbeddedSftpViewModel.ParseLsOutput(output, "/srv/app");

        Assert.Single(entries);
        Assert.Equal("current", entries[0].Name);
        Assert.Equal("/srv/app/current", entries[0].FullPath);
        Assert.False(entries[0].IsDirectory);
    }

    [Theory]
    [InlineData("/srv/app", "/srv/app/report.txt")]
    [InlineData("/srv/app/", "/srv/app/report.txt")]
    public void ParseLsOutput_ComposesFullPathFromParentPath(string parentPath, string expectedFullPath)
    {
        const string output = "-rw-r--r-- 1 root root 1 2026-05-20 14:30 report.txt\n";

        IReadOnlyList<SftpFileInfo> entries = EmbeddedSftpViewModel.ParseLsOutput(output, parentPath);

        Assert.Single(entries);
        Assert.Equal(expectedFullPath, entries[0].FullPath);
    }
}
