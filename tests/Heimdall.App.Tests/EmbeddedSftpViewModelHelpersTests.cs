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

    // The converter calls this from a per-row binding, so an unhandled value used to throw inside the
    // layout pass rather than simply displaying a type.
    [Theory]
    [InlineData(RemoteEntryKind.Unknown, "SftpPropertiesTypeUnknown")]
    [InlineData(RemoteEntryKind.File, "SftpPropertiesTypeFile")]
    [InlineData(RemoteEntryKind.Directory, "SftpPropertiesTypeDirectory")]
    [InlineData(RemoteEntryKind.SymbolicLink, "SftpPropertiesTypeSymlink")]
    [InlineData(RemoteEntryKind.Fifo, "SftpPropertiesTypeFifo")]
    [InlineData(RemoteEntryKind.Socket, "SftpPropertiesTypeSocket")]
    [InlineData(RemoteEntryKind.Device, "SftpPropertiesTypeDevice")]
    public void GetRemoteEntryKindDisplayKey_ReturnsExpectedLocaleKey(
        RemoteEntryKind kind,
        string expected)
    {
        string actual = EmbeddedSftpViewModel.GetRemoteEntryKindDisplayKey(kind);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveDropTargetDirectory_DirectoryHovered_ReturnsItsFullPath()
    {
        SftpFileInfo folder = new SftpFileInfo(
            Name: "uploads",
            FullPath: "/srv/uploads",
            Kind: RemoteEntryKind.Directory,
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
        SftpFileInfo file = new SftpFileInfo(
            Name: "notes.txt",
            FullPath: "/srv/notes.txt",
            Kind: RemoteEntryKind.File,
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
    [InlineData("rwsr-xr-x", "4755")]
    [InlineData("rwxr-sr-x", "2755")]
    [InlineData("rwxrwxrwt", "1777")]
    [InlineData("rwSr--r--", "4644")]
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
    public void IsPermissionDenied_ReturnsFalseForLocalUnauthorizedAccess()
    {
        bool actual = EmbeddedSftpViewModel.IsPermissionDenied(
            new UnauthorizedAccessException("permission denied"));

        Assert.False(actual);
    }

    [Fact]
    public void IsPermissionDenied_ReturnsFalseForUnrelatedException()
    {
        bool actual = EmbeddedSftpViewModel.IsPermissionDenied(
            new InvalidOperationException("not a permission error"));

        Assert.False(actual);
    }

    [Fact]
    public void SudoUploadCommandsBuild_EscapesTargetAndBuildsAtomicWrite()
    {
        string write = SudoUploadCommands.Build("/var/log/oh's.log");

        Assert.Contains("cat > payload", write, StringComparison.Ordinal);
        Assert.Contains("mv -fT -- payload", write, StringComparison.Ordinal);
        Assert.EndsWith(@"sh '/var/log/oh'\''s.log'", write, StringComparison.Ordinal);
    }

}
