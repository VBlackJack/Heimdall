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

using System.Reflection;
using Heimdall.Sftp;
using Renci.SshNet.Sftp;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Unit tests for remote entry kind predicates.
/// </summary>
public sealed class SftpFileInfoKindTests
{
    // Exercises the real classifier, not a copy of its rules. Every positive flag is false, which is the
    // shape the fallback exists for: the entry is neither a link, a directory, a pipe, a socket, a device,
    // nor a regular file. A source scan could not tell whether that branch is still reachable.
    [Fact]
    public void GetRemoteEntryKind_EntryWithNoRecognisedType_IsUnknown()
    {
        MethodInfo classifier = typeof(SftpBrowser).GetMethod(
            "GetRemoteEntryKind",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "the SFTP classifier was not found; this oracle would be vacuous");

        object? kind = classifier.Invoke(null, [new UnclassifiableSftpFile()]);

        Assert.Equal(RemoteEntryKind.Unknown, kind);
    }

    // The zero value is the non-transferable one on purpose. When File was zero, a struct left
    // uninitialised, a mapper that forgot a branch, or a cast from an out-of-range integer all produced
    // something the file-only paths accepted as an ordinary file.
    [Fact]
    public void DefaultKind_IsUnknown_NotARegularFile()
    {
        Assert.Equal(RemoteEntryKind.Unknown, default(RemoteEntryKind));
        Assert.Equal(0, (int)RemoteEntryKind.Unknown);
        Assert.NotEqual(0, (int)RemoteEntryKind.File);

        SftpFileInfo defaulted = new(
            "entry",
            "/entry",
            default,
            0,
            DateTime.UnixEpoch,
            string.Empty,
            string.Empty,
            string.Empty);

        Assert.False(defaulted.IsRegularFile);
        Assert.False(defaulted.IsDirectory);
    }

    [Theory]
    [InlineData(RemoteEntryKind.Unknown, false)]
    [InlineData(RemoteEntryKind.File, true)]
    [InlineData(RemoteEntryKind.Directory, false)]
    [InlineData(RemoteEntryKind.SymbolicLink, false)]
    [InlineData(RemoteEntryKind.Fifo, false)]
    [InlineData(RemoteEntryKind.Socket, false)]
    [InlineData(RemoteEntryKind.Device, false)]
    public void IsRegularFile_ReturnsExpectedValue(RemoteEntryKind kind, bool expected)
    {
        SftpFileInfo file = new(
            "entry",
            "/entry",
            kind,
            0,
            DateTime.UnixEpoch,
            "---------",
            "0",
            "0");

        Assert.Equal(expected, file.IsRegularFile);
    }

    /// <summary>
    /// An <see cref="ISftpFile"/> whose every type flag is false.
    /// </summary>
    /// <remarks>
    /// Members the classifier does not read throw, so a future classifier that started depending on one
    /// of them would fail loudly here instead of being silently mis-measured by a stub returning zeros.
    /// </remarks>
    private sealed class UnclassifiableSftpFile : ISftpFile
    {
        public bool IsSymbolicLink => false;

        public bool IsDirectory => false;

        public bool IsNamedPipe => false;

        public bool IsSocket => false;

        public bool IsBlockDevice => false;

        public bool IsCharacterDevice => false;

        public bool IsRegularFile => false;

        public string FullName => "/srv/data/unclassifiable";

        public string Name => "unclassifiable";

        public SftpFileAttributes Attributes => throw new NotSupportedException();

        public long Length => throw new NotSupportedException();

        public int GroupId { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public int UserId { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public DateTime LastAccessTime { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public DateTime LastWriteTime { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public DateTime LastAccessTimeUtc { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public DateTime LastWriteTimeUtc { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool GroupCanExecute { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool GroupCanRead { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool GroupCanWrite { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool OthersCanExecute { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool OthersCanRead { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool OthersCanWrite { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool OwnerCanExecute { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool OwnerCanRead { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool OwnerCanWrite { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Delete() => throw new NotSupportedException();

        public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public void MoveTo(string destFileName) => throw new NotSupportedException();

        public void SetPermissions(short mode) => throw new NotSupportedException();

        public void UpdateStatus() => throw new NotSupportedException();
    }
}
