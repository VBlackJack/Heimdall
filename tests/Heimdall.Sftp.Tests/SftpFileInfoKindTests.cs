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
/// Unit tests for remote entry kind predicates.
/// </summary>
public sealed class SftpFileInfoKindTests
{
    [Theory]
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
}
