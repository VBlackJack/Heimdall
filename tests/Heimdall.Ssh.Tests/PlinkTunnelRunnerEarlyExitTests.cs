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
            Assert.Equal(SshFailureCode.AuthRejected, result.FailureCode);
            Assert.Equal(TunnelMessageKeys.MessageKeyPlinkExitedEarly, result.MessageKey);
            Assert.Equal(1, result.MessageArguments![0]);
            Assert.Equal("FATAL ERROR: No supported authentication methods available", result.MessageArguments[1]);
        }
        finally
        {
            File.Delete(plinkStandIn);
        }
    }

    /// <summary>
    /// A plink that exited is read to the end of its stderr before the start reports why. The
    /// runner joined the stderr drain for 500 ms and then cancelled it: on a loaded machine the
    /// drain had not read "Access denied" yet, and the refused sign-in was reported as a port
    /// nobody listened on (seen under full CPU load on 2026-10-26).
    /// </summary>
    [Fact]
    public async Task StartAsync_StderrReadLateAfterTheExit_StillNamesTheRefusal()
    {
        string plinkStandIn = Path.GetTempFileName();
        using PlinkTunnelRunner runner = new(
            new PlinkTunnelRunnerOptions(PortCheckIntervalMs: 1000, KillGracePeriodMs: 100),
            new NothingListeningProbe(),
            _ => new ExitedPlinkProcess(new SlowStream("Using username \"ops\".\r\nAccess denied\r\n", LateStderrDelay)));

        try
        {
            PlinkTunnelResult result = await runner.StartAsync(
                plinkStandIn,
                "gw.test", 22, "ops", null, "s3cret",
                "remote", 22, GetAvailableLoopbackPort(), "SHA256:test");

            Assert.False(result.Success);
            Assert.Equal(SshFailureCode.AuthRejected, result.FailureCode);
            Assert.Equal(TunnelMessageKeys.MessageKeyPlinkExitedEarly, result.MessageKey);
        }
        finally
        {
            File.Delete(plinkStandIn);
        }
    }

    /// <summary>
    /// The other side of the wait above: a stderr that never closes, a pipe some other process
    /// still holds, does not hold the start forever. It returns once the bounded wait is over,
    /// with the port account since plink said nothing.
    /// </summary>
    [Fact]
    public async Task StartAsync_StderrThatNeverCloses_StillReturns()
    {
        string plinkStandIn = Path.GetTempFileName();
        using PlinkTunnelRunner runner = new(
            new PlinkTunnelRunnerOptions(PortCheckIntervalMs: 1000, KillGracePeriodMs: 100),
            new NothingListeningProbe(),
            _ => new ExitedPlinkProcess(new SlowStream(string.Empty, Timeout.InfiniteTimeSpan)));

        try
        {
            // WaitAsync, not a stopwatch read after the call: an unbounded wait must fail here,
            // not hang the run.
            PlinkTunnelResult result = await runner.StartAsync(
                plinkStandIn,
                "gw.test", 22, "ops", null, "s3cret",
                "remote", 22, GetAvailableLoopbackPort(), "SHA256:test").WaitAsync(NeverClosingStderrBound);

            Assert.False(result.Success);
            Assert.Equal(SshFailureCode.TunnelPortNotListening, result.FailureCode);
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
    [InlineData("Host key did not appear in manually configured list", SshFailureCode.HostKeyMismatch)]
    [InlineData("FATAL ERROR: No supported authentication methods available", SshFailureCode.NoSupportedAuth)]
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

    // Plink's host key warning runs to seven lines and names the mismatch in its first two.
    // Proved against the mutant that keeps only the last five lines: this comes back Unknown.
    [Fact]
    public async Task StartAsync_HostKeyWarning_IsNamedFromItsFirstLines()
    {
        string plinkStandIn = Path.GetTempFileName();
        const string warning =
            "WARNING - POTENTIAL SECURITY BREACH!\r\n"
            + "The host key does not match the one PuTTY has cached for this server.\r\n"
            + "This means that either the server administrator has changed the host key,\r\n"
            + "or you have actually connected to another computer pretending to be the server.\r\n"
            + "The new ssh-ed25519 key fingerprint is:\r\n"
            + "ssh-ed25519 255 SHA256:abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG\r\n"
            + "Connection abandoned.\r\n";
        using PlinkTunnelRunner runner = new(
            new PlinkTunnelRunnerOptions(PortCheckIntervalMs: 1000, KillGracePeriodMs: 100),
            new FixedProbe(TcpListenerOwnership.NothingListening),
            _ => new ExitedPlinkProcess(warning));

        try
        {
            PlinkTunnelResult result = await runner.StartAsync(
                plinkStandIn,
                "gw.test", 22, "ops", null, "s3cret",
                "remote", 22, GetAvailableLoopbackPort(), "SHA256:test");

            Assert.Equal(SshFailureCode.HostKeyMismatch, result.FailureCode);
        }
        finally
        {
            File.Delete(plinkStandIn);
        }
    }

    // A plink that exits without naming a cause gets the port check's own verdict, as before.
    [Fact]
    public async Task StartAsync_PlinkExitsWithoutNamingACause_ReportsThePortCheck()
    {
        string plinkStandIn = Path.GetTempFileName();
        using PlinkTunnelRunner runner = new(
            new PlinkTunnelRunnerOptions(PortCheckIntervalMs: 1000, KillGracePeriodMs: 100),
            new FixedProbe(TcpListenerOwnership.NothingListening),
            _ => new ExitedPlinkProcess("'gw.test' is not recognized as an internal or external command.\r\n"));

        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            PlinkTunnelResult result = await runner.StartAsync(
                plinkStandIn,
                "gw.test", 22, "ops", null, "s3cret",
                "remote", 22, GetAvailableLoopbackPort(), "SHA256:test");

            Assert.Equal(SshFailureCode.TunnelPortNotListening, result.FailureCode);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Took {elapsed.Elapsed}.");
        }
        finally
        {
            File.Delete(plinkStandIn);
        }
    }

    // Another process on the port is the clearer account of why plink gave up.
    [Fact]
    public async Task StartAsync_PlinkExitsWhileAnotherProcessHoldsThePort_ReportsThePortOwnership()
    {
        string plinkStandIn = Path.GetTempFileName();
        using PlinkTunnelRunner runner = new(
            new PlinkTunnelRunnerOptions(PortCheckIntervalMs: 1000, KillGracePeriodMs: 100),
            new FixedProbe(TcpListenerOwnership.OwnedByDifferentProcess),
            _ => new ExitedPlinkProcess("FATAL ERROR: Local port 9090 forwarding failed: Address already in use\r\n"));

        try
        {
            PlinkTunnelResult result = await runner.StartAsync(
                plinkStandIn,
                "gw.test", 22, "ops", null, "s3cret",
                "remote", 22, GetAvailableLoopbackPort(), "SHA256:test");

            Assert.Equal(SshFailureCode.TunnelPortOwnedByDifferentProcess, result.FailureCode);
        }
        finally
        {
            File.Delete(plinkStandIn);
        }
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

    private sealed class FixedProbe(TcpListenerOwnership ownership) : ITcpListenerOwnershipProbe
    {
        public TcpListenerOwnership Probe(string bindHost, int port, int expectedProcessId) => ownership;
    }

    /// <summary>Three times the 500 ms the runner used to wait for the drain of an exited plink.</summary>
    private static readonly TimeSpan LateStderrDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>The runner's bounded wait for an exited plink's drain, plus room for a loaded machine.</summary>
    private static readonly TimeSpan NeverClosingStderrBound = TimeSpan.FromSeconds(15);

    /// <summary>A stderr pipe whose first read only completes after a delay, as on a loaded machine.</summary>
    private sealed class SlowStream(string content, TimeSpan delay) : Stream
    {
        private readonly MemoryStream _inner = new(Encoding.UTF8.GetBytes(content));
        private bool _delayed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delayed)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                _delayed = true;
            }

            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ExitedPlinkProcess(Stream stderr) : IPlinkProcess
    {
        private static int _nextProcessId = 20000;
        private readonly StreamReader _standardError = new(stderr);

        public ExitedPlinkProcess(string stderr)
            : this(new MemoryStream(Encoding.UTF8.GetBytes(stderr)))
        {
        }

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
