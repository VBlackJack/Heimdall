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
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;
using Heimdall.Terminal;

namespace Heimdall.App.Tests;

/// <summary>
/// How a failed Telnet connect is told apart (user cancel, timeout, other failure) and that the
/// handler reports each one through the mapper.
/// </summary>
public sealed class TelnetConnectFailureTests
{
    [Fact]
    public void Classify_CancellationWhileTheUserTokenIsLive_IsATimeout()
    {
        TelnetConnectOutcome outcome = TelnetConnectFailureMapper.Classify(
            new OperationCanceledException(), CancellationToken.None);

        Assert.Equal(TelnetConnectOutcome.TimedOut, outcome);
    }

    [Fact]
    public void Classify_CancellationAfterTheUserCancelled_IsACancel()
    {
        using CancellationTokenSource cancel = new();
        cancel.Cancel();

        TelnetConnectOutcome outcome = TelnetConnectFailureMapper.Classify(
            new TaskCanceledException(), cancel.Token);

        Assert.Equal(TelnetConnectOutcome.Cancelled, outcome);
    }

    [Fact]
    public void Classify_NetworkTimeouts_AreTimeouts()
    {
        Assert.Equal(
            TelnetConnectOutcome.TimedOut,
            TelnetConnectFailureMapper.Classify(new TimeoutException(), CancellationToken.None));
        Assert.Equal(
            TelnetConnectOutcome.TimedOut,
            TelnetConnectFailureMapper.Classify(new SocketException((int)SocketError.TimedOut), CancellationToken.None));
    }

    [Fact]
    public void Classify_OtherFailures_AreFailures()
    {
        Assert.Equal(
            TelnetConnectOutcome.Failed,
            TelnetConnectFailureMapper.Classify(new SocketException((int)SocketError.ConnectionRefused), CancellationToken.None));
    }

    [Fact]
    public void MessageKey_GivesTimeoutAndFailureDifferentKeysAndACancelNone()
    {
        Assert.Null(TelnetConnectFailureMapper.MessageKey(TelnetConnectOutcome.Cancelled));
        Assert.Equal("ErrorTelnetConnectionTimedOut", TelnetConnectFailureMapper.MessageKey(TelnetConnectOutcome.TimedOut));
        Assert.Equal("ErrorTelnetConnectionFailed", TelnetConnectFailureMapper.MessageKey(TelnetConnectOutcome.Failed));
    }

    [Fact]
    public async Task ConnectAsync_WhenTheConnectTimesOut_ReportsTheTimeoutMessage()
    {
        TelnetHandler handler = CreateHandler(new ThrowingSession(new OperationCanceledException()));

        ConnectionResult result = await handler.ConnectAsync(CreateServer(), new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorTelnetConnectionTimedOut", result.ErrorMessage);
    }

    [Fact]
    public async Task ConnectAsync_WhenTheConnectIsRefused_ReportsTheFailureMessage()
    {
        TelnetHandler handler = CreateHandler(new ThrowingSession(new SocketException((int)SocketError.ConnectionRefused)));

        ConnectionResult result = await handler.ConnectAsync(CreateServer(), new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorTelnetConnectionFailed", result.ErrorMessage);
    }

    [Fact]
    public async Task ConnectAsync_WhenTheUserCancels_IsRethrownAndNotShownAsAnError()
    {
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        ThrowingSession session = new(new OperationCanceledException(cancel.Token));
        TelnetHandler handler = CreateHandler(session);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.ConnectAsync(CreateServer(), new AppSettings(), cancel.Token));

        Assert.True(session.Disposed);
    }

    private static TelnetHandler CreateHandler(ITerminalSession session)
        => new(new ConnectionStateMachine(), new LocalizationManager(), (_, _, _) => session);

    private static ServerProfileDto CreateServer()
        => new()
        {
            Id = "telnet-failure-test",
            DisplayName = "Telnet Failure Test",
            RemoteServer = "host.example.com",
            ConnectionType = "Telnet",
            TelnetPort = 23
        };

    /// <summary>A session whose start fails with the exception it was given.</summary>
    private sealed class ThrowingSession : ITerminalSession
    {
        private readonly Exception _failure;

        public ThrowingSession(Exception failure) => _failure = failure;

        public bool Disposed { get; private set; }

        public event Action<ReadOnlyMemory<byte>>? DataReceived { add { } remove { } }

        public event Action<int>? ProcessExited { add { } remove { } }

        public bool IsRunning => false;

        public int? ProcessId => null;

        public Dictionary<string, string>? EnvironmentVariables { get; set; }

        public Task StartAsync(
            string executable,
            string arguments,
            int columns = 80,
            int rows = 24,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
            => Task.FromException(_failure);

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Write(string text)
        {
        }

        public void Resize(int columns, int rows)
        {
        }

        public void Kill()
        {
        }

        public void Dispose() => Disposed = true;
    }
}
