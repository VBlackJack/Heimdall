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

using System.IO;
using Heimdall.App.ViewModels;
using Heimdall.Sftp;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

public sealed class IsPermissionDeniedTests
{
    [Fact]
    public void Returns_True_For_SftpPermissionDeniedException()
    {
        var ex = new SftpPermissionDeniedException("Permission denied.");

        Assert.True(EmbeddedSftpViewModel.IsPermissionDenied(ex));
    }

    /// <remarks>
    /// UnauthorizedAccessException is the local file system refusing (a read-only folder, an
    /// ACL). Reading it as a remote denial escalated to a privileged remote transfer that
    /// failed on the same local path, with a message that blamed the server.
    /// </remarks>
    [Fact]
    public void Returns_False_For_UnauthorizedAccessException()
    {
        var ex = new UnauthorizedAccessException("Access denied.");

        Assert.False(EmbeddedSftpViewModel.IsPermissionDenied(ex));
    }

    [Fact]
    public void Returns_True_For_RemoteRecursiveDeletePermissionDenied()
    {
        RemoteRecursiveDeleteException ex = new(
            RemoteRecursiveDeleteFailureReason.PermissionDenied);

        Assert.True(EmbeddedSftpViewModel.IsPermissionDenied(ex));
    }

    [Fact]
    public void Returns_False_For_RemoteRecursiveDeleteCommandFailed()
    {
        RemoteRecursiveDeleteException ex = new(
            RemoteRecursiveDeleteFailureReason.CommandFailed);

        Assert.False(EmbeddedSftpViewModel.IsPermissionDenied(ex));
    }

    [Fact]
    public void Returns_False_For_SshException_With_ChannelFailure_Message()
    {
        var ex = new SshException("Channel failure: administratively prohibited.");

        Assert.False(EmbeddedSftpViewModel.IsPermissionDenied(ex));
    }

    [Fact]
    public void Returns_False_For_SshException_With_Disconnect_Message()
    {
        var ex = new SshException("Disconnect by application.");

        Assert.False(EmbeddedSftpViewModel.IsPermissionDenied(ex));
    }

    [Fact]
    public void Returns_False_For_SshException_With_Generic_Failure_Message()
    {
        var ex = new SshException("Some other failure.");

        Assert.False(EmbeddedSftpViewModel.IsPermissionDenied(ex));
    }

    [Fact]
    public void Returns_False_For_Plain_Exception_With_Permission_Denied_Message()
    {
        var ex = new Exception("permission denied");

        Assert.False(EmbeddedSftpViewModel.IsPermissionDenied(ex));
    }

    [Fact]
    public void Returns_False_For_SftpPathNotFoundException()
    {
        var ex = new SftpPathNotFoundException("No such file.");

        Assert.False(EmbeddedSftpViewModel.IsPermissionDenied(ex));
    }

    [Fact]
    public void Returns_False_For_Generic_IOException()
    {
        var ex = new IOException("The device is not ready.");

        Assert.False(EmbeddedSftpViewModel.IsPermissionDenied(ex));
    }

    [Fact]
    public void Throws_For_Null_Exception()
    {
        Assert.Throws<ArgumentNullException>(() => EmbeddedSftpViewModel.IsPermissionDenied(null!));
    }
}
