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
/// Unit tests for validation of an existing SFTP upload destination.
/// </summary>
public sealed class SftpAtomicUploadTargetTests
{
    private const string FinalRemotePath = "/srv/app/config.txt";

    [Fact]
    public void EnsureUploadTargetSupported_AllowsAbsentDestination()
    {
        Exception? exception = Record.Exception(() =>
            SftpAtomicUpload.EnsureUploadTargetSupported(FinalRemotePath, null));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureUploadTargetSupported_AllowsAnExistingRegularFile()
    {
        Exception? exception = Record.Exception(() =>
            SftpAtomicUpload.EnsureUploadTargetSupported(
                FinalRemotePath,
                RemoteEntryKind.File));

        Assert.Null(exception);
    }

    /// <remarks>
    /// A directory used to be accepted here and refused only by the server, at the rename,
    /// after the whole file had been staged. It is decidable up front.
    /// </remarks>
    [Theory]
    [InlineData(RemoteEntryKind.Directory)]
    [InlineData(RemoteEntryKind.Unknown)]
    [InlineData(RemoteEntryKind.SymbolicLink)]
    [InlineData(RemoteEntryKind.Fifo)]
    [InlineData(RemoteEntryKind.Socket)]
    [InlineData(RemoteEntryKind.Device)]
    public void EnsureUploadTargetSupported_ThrowsForUnsupportedExistingDestination(
        RemoteEntryKind existingDestinationKind)
    {
        RemoteUploadTargetUnsupportedException exception =
            Assert.Throws<RemoteUploadTargetUnsupportedException>(() =>
                SftpAtomicUpload.EnsureUploadTargetSupported(
                    FinalRemotePath,
                    existingDestinationKind));

        Assert.Equal(FinalRemotePath, exception.RemotePath);
        Assert.Equal(existingDestinationKind, exception.Kind);
    }
}
