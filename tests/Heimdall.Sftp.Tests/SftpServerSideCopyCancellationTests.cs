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
/// The server-side <c>cp</c> fast path used to test its token once and then block in a synchronous
/// <c>Execute()</c>, so cancelling the copy of a large tree had no effect: the request never reached
/// the running command. These tests pin the two properties that fix requires - the token travels to
/// the exec channel, and a cancelled copy raises a cancellation rather than a refusal.
/// </summary>
public sealed class SftpServerSideCopyCancellationTests
{
    private const string SourcePath = "/srv/tree";
    private const string DestinationPath = "/srv/tree-copy";

    [Fact]
    public async Task TryServerSideCopyAsync_CancelledDuringExec_ThrowsInsteadOfFallingBack()
    {
        TaskCompletionSource execStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeExecCommandRunner runner = new(async (_, execToken) =>
        {
            execStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, execToken);
            return new SftpExecResult(0, string.Empty);
        });
        using SftpBrowser browser = new(runner);
        using CancellationTokenSource cts = new();

        Task<ServerSideCopyOutcome> copy = browser.TryServerSideCopyAsync(
            SourcePath,
            DestinationPath,
            recursive: true,
            cts.Token);
        await execStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();

        // Falling back to the roundtrip here would restart the very transfer the user cancelled.
        // The bound matters: a copy that does not receive the request never returns at all, and an
        // unbounded wait would turn that regression into a hung job instead of a failing test.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => copy.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TryServerSideCopyAsync_CallerCancels_CancelsTheTokenHandedToTheExecChannel()
    {
        TaskCompletionSource execStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken execToken = default;
        FakeExecCommandRunner runner = new(async (_, token) =>
        {
            execToken = token;
            execStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new SftpExecResult(0, string.Empty);
        });
        using SftpBrowser browser = new(runner);
        using CancellationTokenSource cts = new();

        Task<ServerSideCopyOutcome> copy = browser.TryServerSideCopyAsync(
            SourcePath,
            DestinationPath,
            recursive: true,
            cts.Token);
        await execStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(execToken.IsCancellationRequested);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => copy.WaitAsync(TimeSpan.FromSeconds(5)));

        // The exec channel is driven by a token linked to the caller's, not by an unrelated one.
        Assert.True(execToken.IsCancellationRequested);
    }

    [Fact]
    public async Task TryServerSideCopyAsync_ExecTimesOutWhileCallerStillWants_ReportsCommandTimedOut()
    {
        // A command that outruns its own cap surfaces as a cancellation the CALLER never asked for.
        FakeExecCommandRunner runner = new(
            static (_, _) => throw new OperationCanceledException(new CancellationToken(canceled: true)));
        using SftpBrowser browser = new(runner);
        using CancellationTokenSource cts = new();

        ServerSideCopyOutcome outcome = await browser.TryServerSideCopyAsync(
            SourcePath,
            DestinationPath,
            recursive: true,
            cts.Token);

        Assert.Equal(ServerSideCopyOutcome.CommandTimedOut, outcome);
        // The discriminant between an internal timeout and a user cancellation: the caller never asked.
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task TryServerSideCopyAsync_ExitStatusZero_ReportsSuccessOnTheConfinedCommand()
    {
        FakeExecCommandRunner runner = new(
            static (_, _) => Task.FromResult(new SftpExecResult(0, string.Empty)));
        using SftpBrowser browser = new(runner);

        ServerSideCopyOutcome outcome = await browser.TryServerSideCopyAsync(
            SourcePath,
            DestinationPath,
            recursive: true,
            CancellationToken.None);

        Assert.Equal(ServerSideCopyOutcome.Succeeded, outcome);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(
            ServerSideCopyCommand.Build(SourcePath, DestinationPath, recursive: true),
            runner.LastCommand);
    }

    [Fact]
    public async Task TryServerSideCopyAsync_NonZeroExitStatus_ReportsNonZeroExit()
    {
        FakeExecCommandRunner runner = new(
            static (_, _) => Task.FromResult(new SftpExecResult(1, "cp: cannot stat")));
        using SftpBrowser browser = new(runner);

        ServerSideCopyOutcome outcome = await browser.TryServerSideCopyAsync(
            SourcePath,
            DestinationPath,
            recursive: true,
            CancellationToken.None);

        Assert.Equal(ServerSideCopyOutcome.NonZeroExit, outcome);
    }

    [Fact]
    public async Task TryServerSideCopyAsync_NoPinnedContextAndNoInjectedRunner_ReportsNoPinnedContext()
    {
        // Fail closed: without a retained pinned context there is no channel to trust, and no second
        // route reserves the destination exclusively, so the caller refuses the copy outright.
        using SftpBrowser browser = new();

        ServerSideCopyOutcome outcome = await browser.TryServerSideCopyAsync(
            SourcePath,
            DestinationPath,
            recursive: true,
            CancellationToken.None);

        Assert.Equal(ServerSideCopyOutcome.NoPinnedContext, outcome);
    }

    // A user cancellation must never be reported as "the server cannot copy safely". The runner tears
    // the SSH client down to cancel, which surfaces as an SshException rather than an
    // OperationCanceledException, so the transport handler has to reconcile against the caller's token.
    [Fact]
    public async Task TryServerSideCopyAsync_TransportFailsBecauseTheCallerCancelled_PropagatesCancellation()
    {
        using CancellationTokenSource cts = new();
        FakeExecCommandRunner runner = new((_, _) =>
        {
            cts.Cancel();
            throw new Renci.SshNet.Common.SshConnectionException("client torn down to cancel");
        });
        using SftpBrowser browser = new(runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => browser.TryServerSideCopyAsync(
                SourcePath,
                DestinationPath,
                recursive: true,
                cts.Token));
    }

    [Fact]
    public async Task TryServerSideCopyAsync_TransportFailsWithoutCancellation_ReportsTransportFailed()
    {
        FakeExecCommandRunner runner = new(
            static (_, _) => throw new Renci.SshNet.Common.SshConnectionException("channel lost"));
        using SftpBrowser browser = new(runner);

        ServerSideCopyOutcome outcome = await browser.TryServerSideCopyAsync(
            SourcePath,
            DestinationPath,
            recursive: true,
            CancellationToken.None);

        Assert.Equal(ServerSideCopyOutcome.TransportFailed, outcome);
    }

    private sealed class FakeExecCommandRunner : ISftpExecCommandRunner
    {
        private readonly Func<string, CancellationToken, Task<SftpExecResult>> _executeAsync;

        public FakeExecCommandRunner(Func<string, CancellationToken, Task<SftpExecResult>> executeAsync)
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
