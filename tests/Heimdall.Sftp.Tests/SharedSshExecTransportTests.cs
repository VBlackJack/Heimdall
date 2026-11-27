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

public sealed class SharedSshExecTransportTests
{
    private sealed class StubExecRunner(SftpExecResult result) : ISftpExecCommandRunner
    {
        public int Calls { get; private set; }
        public string? LastCommand { get; private set; }

        public Task<SftpExecResult> ExecuteAsync(string command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            LastCommand = command;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task SharedSshClientExecCommandRunner_FallsBackWhenSharedClientIsNull()
    {
        var fallback = new StubExecRunner(new SftpExecResult(0, string.Empty));
        var runner = new SharedSshClientExecCommandRunner(() => null, fallback);

        var result = await runner.ExecuteAsync("echo ok", CancellationToken.None);

        Assert.Equal(0, result.ExitStatus);
        Assert.Equal(1, fallback.Calls);
        Assert.Equal("echo ok", fallback.LastCommand);
    }

    [Fact]
    public async Task SharedSshClientExecCommandRunner_FallsBackWhenAccessorThrowsObjectDisposed()
    {
        var fallback = new StubExecRunner(new SftpExecResult(0, string.Empty));
        var runner = new SharedSshClientExecCommandRunner(
            () => throw new ObjectDisposedException("SshClient"),
            fallback);

        var result = await runner.ExecuteAsync("true", CancellationToken.None);

        Assert.Equal(0, result.ExitStatus);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task SharedSshClientExecCommandRunner_ObservesPreCancelledToken()
    {
        var fallback = new StubExecRunner(new SftpExecResult(0, string.Empty));
        var runner = new SharedSshClientExecCommandRunner(() => null, fallback);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.ExecuteAsync("echo ok", cts.Token));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public void SftpBrowser_AttachSharedSshClient_AcceptsAndClearsAccessor()
    {
        using var browser = new SftpBrowser();
        browser.AttachSharedSshClient(() => null);
        browser.AttachSharedSshClient(null);
    }
}
