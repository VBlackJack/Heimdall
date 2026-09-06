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
/// The kind guards the browser applies without following a link. Chmod followed one to
/// its target; the server-side copy read a link to a directory as a directory and cp then
/// duplicated the whole target tree; the listing let any server-supplied name into the
/// model. The wiring needs a live client; the decisions do not.
/// </summary>
public sealed class SftpBrowserEntryKindGuardTests
{
    [Fact]
    public void EnsureChmodTargetSupported_RefusesASymbolicLink()
    {
        IOException refusal = Assert.Throws<IOException>(
            () => SftpBrowser.EnsureChmodTargetSupported("/srv/link", RemoteEntryKind.SymbolicLink));

        Assert.Contains("symbolic link", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RemoteEntryKind.File)]
    [InlineData(RemoteEntryKind.Directory)]
    [InlineData(RemoteEntryKind.Fifo)]
    public void EnsureChmodTargetSupported_AcceptsEverythingElse(RemoteEntryKind kind)
    {
        SftpBrowser.EnsureChmodTargetSupported("/srv/entry", kind);
    }

    [Theory]
    [InlineData(RemoteEntryKind.SymbolicLink)]
    [InlineData(RemoteEntryKind.Fifo)]
    [InlineData(RemoteEntryKind.Socket)]
    [InlineData(RemoteEntryKind.Device)]
    [InlineData(RemoteEntryKind.Unknown)]
    public void EnsureCopySourceSupported_RefusesAnythingCpWouldDereference(RemoteEntryKind kind)
    {
        IOException refusal = Assert.Throws<IOException>(
            () => SftpBrowser.EnsureCopySourceSupported("/srv/source", kind));

        Assert.Contains("Refused to copy", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RemoteEntryKind.File)]
    [InlineData(RemoteEntryKind.Directory)]
    public void EnsureCopySourceSupported_AcceptsFilesAndDirectories(RemoteEntryKind kind)
    {
        SftpBrowser.EnsureCopySourceSupported("/srv/source", kind);
    }

    [Theory]
    [InlineData("hosts", true)]
    [InlineData("my dir", true)]
    [InlineData("../x", false)]
    [InlineData("a/b", false)]
    [InlineData("x\ny", false)]
    [InlineData("", false)]
    public void IsListableEntryName_AdmitsOnlyOneCleanSegment(string name, bool expected)
    {
        Assert.Equal(expected, SftpBrowser.IsListableEntryName(name));
    }
}
