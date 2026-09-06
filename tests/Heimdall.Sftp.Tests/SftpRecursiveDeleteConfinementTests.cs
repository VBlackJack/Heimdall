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

using System.Net.Sockets;
using Heimdall.Sftp;
using Heimdall.Ssh;
using Renci.SshNet.Common;

namespace Heimdall.Sftp.Tests;

public sealed class SftpRecursiveDeleteConfinementTests
{
    public enum TransportFailureKind
    {
        Ssh,
        Socket,
        Io,
        Timeout,
    }

    [Fact]
    public async Task DeleteDirectoryViaExecAsync_ExecutesOneConfinedCommandWithoutSftpTraversal()
    {
        FakeExecCommandRunner runner = new(
            static (_, _) => Task.FromResult(new SftpExecResult(0, string.Empty)));
        using SftpBrowser browser = new(runner);

        await browser.DeleteDirectoryViaExecAsync("/srv/tree", CancellationToken.None);

        Assert.Equal(1, runner.CallCount);
        Assert.Equal("LC_ALL=C rm -r -- '/srv/tree'", runner.LastCommand);
    }

    /// <remarks>
    /// Every other exec in the browser carries its own ceiling; the recursive delete ran with
    /// the caller's token alone, and a stalled exec channel ran for as long as the connection
    /// lived. A caller passing CancellationToken.None must still hand the runner a token
    /// that can fire.
    /// </remarks>
    [Fact]
    public async Task DeleteDirectoryViaExecAsync_BoundsTheExecWithItsOwnToken()
    {
        CancellationToken observed = default;
        FakeExecCommandRunner runner = new((_, ct) =>
        {
            observed = ct;
            return Task.FromResult(new SftpExecResult(0, string.Empty));
        });
        using SftpBrowser browser = new(runner);

        await browser.DeleteDirectoryViaExecAsync("/srv/tree", CancellationToken.None);

        Assert.True(observed.CanBeCanceled, "the exec must run under a token that a timeout can cancel");
    }

    [Fact]
    public async Task DeleteDirectoryViaExecAsync_MapsExit127ToShellOrRmUnavailable()
    {
        FakeExecCommandRunner runner = CreateResultRunner(127, "rm: not found");
        using SftpBrowser browser = new(runner);

        RemoteRecursiveDeleteException exception =
            await Assert.ThrowsAsync<RemoteRecursiveDeleteException>(
                () => browser.DeleteDirectoryViaExecAsync("/srv/tree", CancellationToken.None));

        Assert.Equal(RemoteRecursiveDeleteFailureReason.ShellOrRmUnavailable, exception.Reason);
    }

    [Fact]
    public async Task DeleteDirectoryViaExecAsync_MapsPermissionDiagnosticCaseInsensitively()
    {
        FakeExecCommandRunner runner = CreateResultRunner(1, "rm: Permission Denied");
        using SftpBrowser browser = new(runner);

        RemoteRecursiveDeleteException exception =
            await Assert.ThrowsAsync<RemoteRecursiveDeleteException>(
                () => browser.DeleteDirectoryViaExecAsync("/srv/tree", CancellationToken.None));

        Assert.Equal(RemoteRecursiveDeleteFailureReason.PermissionDenied, exception.Reason);
    }

    [Fact]
    public async Task DeleteDirectoryViaExecAsync_MapsOtherNonzeroExitToCommandFailed()
    {
        FakeExecCommandRunner runner = CreateResultRunner(2, "rm: device busy");
        using SftpBrowser browser = new(runner);

        RemoteRecursiveDeleteException exception =
            await Assert.ThrowsAsync<RemoteRecursiveDeleteException>(
                () => browser.DeleteDirectoryViaExecAsync("/srv/tree", CancellationToken.None));

        Assert.Equal(RemoteRecursiveDeleteFailureReason.CommandFailed, exception.Reason);
    }

    [Theory]
    [InlineData(TransportFailureKind.Ssh)]
    [InlineData(TransportFailureKind.Socket)]
    [InlineData(TransportFailureKind.Io)]
    [InlineData(TransportFailureKind.Timeout)]
    public async Task DeleteDirectoryViaExecAsync_MapsTransportFailureToExecUnavailable(
        TransportFailureKind failureKind)
    {
        Exception failure = CreateTransportFailure(failureKind);
        FakeExecCommandRunner runner = new(
            (_, _) => Task.FromException<SftpExecResult>(failure));
        using SftpBrowser browser = new(runner);

        RemoteRecursiveDeleteException exception =
            await Assert.ThrowsAsync<RemoteRecursiveDeleteException>(
                () => browser.DeleteDirectoryViaExecAsync("/srv/tree", CancellationToken.None));

        Assert.Equal(RemoteRecursiveDeleteFailureReason.ExecUnavailable, exception.Reason);
        Assert.Same(failure, exception.InnerException);
    }

    [Fact]
    public async Task DeleteDirectoryViaExecAsync_MapsHostKeyRejectionToExecUnavailable()
    {
        HostKeyRejectedException failure = new(
            "example.test",
            22,
            "ssh-ed25519",
            "presented",
            "stored");
        FakeExecCommandRunner runner = new(
            (_, _) => Task.FromException<SftpExecResult>(failure));
        using SftpBrowser browser = new(runner);

        RemoteRecursiveDeleteException exception =
            await Assert.ThrowsAsync<RemoteRecursiveDeleteException>(
                () => browser.DeleteDirectoryViaExecAsync("/srv/tree", CancellationToken.None));

        Assert.Equal(RemoteRecursiveDeleteFailureReason.ExecUnavailable, exception.Reason);
        Assert.Same(failure, exception.InnerException);
    }

    [Fact]
    public async Task DeleteDirectoryViaExecAsync_PreservesOperationCanceledException()
    {
        OperationCanceledException failure = new("exec cancelled");
        FakeExecCommandRunner runner = new(
            (_, _) => Task.FromException<SftpExecResult>(failure));
        using SftpBrowser browser = new(runner);

        OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => browser.DeleteDirectoryViaExecAsync("/srv/tree", CancellationToken.None));

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task DeleteDirectoryViaExecAsync_DoesNotExposeStandardErrorInExceptionMessage()
    {
        const string DetailedStandardError = "private remote diagnostic: /secret/tree";
        FakeExecCommandRunner runner = CreateResultRunner(1, DetailedStandardError);
        using SftpBrowser browser = new(runner);

        RemoteRecursiveDeleteException exception =
            await Assert.ThrowsAsync<RemoteRecursiveDeleteException>(
                () => browser.DeleteDirectoryViaExecAsync("/srv/tree", CancellationToken.None));

        Assert.DoesNotContain(DetailedStandardError, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteDirectoryViaExecAsync_WithoutRetainedConnectionContextFailsClosed()
    {
        using SftpBrowser browser = new();

        RemoteRecursiveDeleteException exception =
            await Assert.ThrowsAsync<RemoteRecursiveDeleteException>(
                () => browser.DeleteDirectoryViaExecAsync("/srv/tree", CancellationToken.None));

        Assert.Equal(RemoteRecursiveDeleteFailureReason.ExecUnavailable, exception.Reason);
    }

    private static FakeExecCommandRunner CreateResultRunner(int exitStatus, string standardError)
    {
        return new FakeExecCommandRunner(
            (_, _) => Task.FromResult(new SftpExecResult(exitStatus, standardError)));
    }

    private static Exception CreateTransportFailure(TransportFailureKind failureKind)
    {
        return failureKind switch
        {
            TransportFailureKind.Ssh => new SshException("SSH transport failed."),
            TransportFailureKind.Socket => new SocketException((int)SocketError.ConnectionRefused),
            TransportFailureKind.Io => new IOException("Transport stream failed."),
            TransportFailureKind.Timeout => new TimeoutException("Transport timed out."),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, null),
        };
    }

    private sealed class FakeExecCommandRunner : ISftpExecCommandRunner
    {
        private readonly Func<string, CancellationToken, Task<SftpExecResult>> _executeAsync;

        public FakeExecCommandRunner(
            Func<string, CancellationToken, Task<SftpExecResult>> executeAsync)
        {
            _executeAsync = executeAsync;
        }

        public int CallCount { get; private set; }

        public string? LastCommand { get; private set; }

        public Task<SftpExecResult> ExecuteAsync(string command, CancellationToken ct)
        {
            CallCount++;
            LastCommand = command;
            return _executeAsync(command, ct);
        }
    }
}
