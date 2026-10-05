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

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Heimdall.Core.Network;
using Heimdall.Ssh.Plink;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// A plink that exited before it opened the tunnel was waited on for the whole port check and then
/// reported as a port another process owned.
/// </summary>
public sealed class PlinkTunnelRunnerEarlyExitTests
{
    // Proved against the mutant that keeps probing after the exit: the start takes the full
    // fifteen attempts, fourteen seconds at this interval, and the elapsed bound goes red.
    [Fact]
    public async Task StartAsync_PlinkExitsAtOnce_FailsWithoutWaitingOutThePortCheck()
    {
        string plinkStandIn = Path.GetTempFileName();
        using PlinkTunnelRunner runner = new(
            new PlinkTunnelRunnerOptions(PortCheckIntervalMs: 1000, KillGracePeriodMs: 100),
            new NothingListeningProbe(),
            _ => new ExitedPlinkProcess("Using username \"ops\".\r\nAccess denied\r\nFATAL ERROR: No supported authentication methods available\r\n"));

        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            PlinkTunnelResult result = await runner.StartAsync(
                plinkStandIn,
                "gw.test", 22, "ops", null, "s3cret",
                "remote", 22, GetAvailableLoopbackPort(), "SHA256:test");
            elapsed.Stop();

            Assert.False(result.Success);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Took {elapsed.Elapsed}.");
            Assert.Equal(SshFailureCode.NoSupportedAuth, result.FailureCode);
            Assert.Equal(TunnelMessageKeys.MessageKeyPlinkExitedEarly, result.MessageKey);
            Assert.Equal(1, result.MessageArguments![0]);
            Assert.Equal("FATAL ERROR: No supported authentication methods available", result.MessageArguments[1]);
        }
        finally
        {
            File.Delete(plinkStandIn);
        }
    }

    [Theory]
    [InlineData("Access denied", SshFailureCode.AuthRejected)]
    [InlineData("FATAL ERROR: Network error: Connection refused", SshFailureCode.NetworkRefused)]
    [InlineData("FATAL ERROR: Network error: Connection timed out", SshFailureCode.NetworkTimedOut)]
    [InlineData("FATAL ERROR: Host does not exist", SshFailureCode.NetworkUnreachable)]
    [InlineData("WARNING - POTENTIAL SECURITY BREACH!", SshFailureCode.HostKeyMismatch)]
    [InlineData("Wrong passphrase", SshFailureCode.PassphraseRejected)]
    [InlineData("Unable to use key file \"C:\\keys\\id.ppk\" (unable to open file)", SshFailureCode.KeyFileInvalid)]
    [InlineData("Server refused our key", SshFailureCode.KeyRejected)]
    [InlineData("Something nobody planned for", SshFailureCode.Unknown)]
    public void Classify_NamesTheFailurePlinkReported(string line, SshFailureCode expected)
    {
        Assert.Equal(expected, PlinkStderrClassifier.Classify(["Using username \"ops\".", line]));
    }

    // A refused key is followed by plink's generic last word; the specific cause still wins.
    [Fact]
    public void Classify_PrefersTheSpecificCauseOverPlinksClosingLine()
    {
        Assert.Equal(
            SshFailureCode.KeyRejected,
            PlinkStderrClassifier.Classify(["Server refused our key", "Access denied"]));
    }

    private static int GetAvailableLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class NothingListeningProbe : ITcpListenerOwnershipProbe
    {
        public TcpListenerOwnership Probe(string bindHost, int port, int expectedProcessId) =>
            TcpListenerOwnership.NothingListening;
    }

    private sealed class ExitedPlinkProcess(string stderr) : IPlinkProcess
    {
        private static int _nextProcessId = 20000;
        private readonly StreamReader _standardError = new(new MemoryStream(Encoding.UTF8.GetBytes(stderr)));

        public event EventHandler? Exited;

        public int Id { get; } = Interlocked.Increment(ref _nextProcessId);

        public bool HasExited { get; private set; }

        public int ExitCode => 1;

        public StreamReader StandardError => _standardError;

        public bool Start()
        {
            HasExited = true;
            Exited?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void Kill()
        {
        }

        public bool WaitForExit(int milliseconds) => true;

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() => _standardError.Dispose();
    }
}
