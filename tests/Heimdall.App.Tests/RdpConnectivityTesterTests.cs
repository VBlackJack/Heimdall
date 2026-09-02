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

using System.Net;
using System.Net.Sockets;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class RdpConnectivityTesterTests
{
    [Fact]
    public async Task TestAsync_InvalidPortReturnsInvalidPortOutcome()
    {
        var sut = new RdpConnectivityTester();

        var result = await sut.TestAsync(
            "localhost",
            70000,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(RdpConnectivityTestOutcome.InvalidPort, result.Outcome);
    }

    [Fact]
    public async Task TestAsync_BlankHostReturnsInvalidAddressOutcome()
    {
        RdpConnectivityTester sut = new RdpConnectivityTester();

        RdpConnectivityTestResult result = await sut.TestAsync(
            "   ",
            3389,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(RdpConnectivityTestOutcome.InvalidAddress, result.Outcome);
    }

    [Fact]
    public async Task TestAsync_InvalidHostnameReturnsInvalidAddressOutcome()
    {
        var sut = new RdpConnectivityTester();

        var result = await sut.TestAsync(
            "not a valid hostname!",
            3389,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(RdpConnectivityTestOutcome.InvalidAddress, result.Outcome);
    }

    [Fact]
    public async Task TestAsync_UnreachableLocalPortReturnsTcpFailureOrTimeout()
    {
        var sut = new RdpConnectivityTester();

        var result = await sut.TestAsync(
            "127.0.0.1",
            1,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(
            result.Outcome is RdpConnectivityTestOutcome.TcpFailed
                or RdpConnectivityTestOutcome.TcpTimeout,
            $"Unexpected outcome: {result.Outcome}");
    }

    [Fact]
    public async Task TestAsync_CancelledTokenReturnsCancelledOutcome()
    {
        var sut = new RdpConnectivityTester();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await sut.TestAsync(
            "127.0.0.1",
            3389,
            TimeSpan.FromSeconds(5),
            cts.Token);

        Assert.Equal(RdpConnectivityTestOutcome.Cancelled, result.Outcome);
    }

    // The Success branch had no reader at all: every case above stops at a refusal or at a
    // validation failure, so returning TcpFailed from the end of TestAsync was green. What the
    // chip prints on a healthy host - the address that answered, and the two measured times -
    // was therefore unpinned.
    [Fact]
    public async Task TestAsync_ReachablePort_ReturnsSuccessWithBothElapsedTimes()
    {
        using Listener listener = Listener.OnLoopback();
        RdpConnectivityTester sut = new RdpConnectivityTester();

        RdpConnectivityTestResult result = await sut.TestAsync(
            "127.0.0.1",
            listener.Port,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(RdpConnectivityTestOutcome.Success, result.Outcome);
        Assert.Equal(IPAddress.Loopback.ToString(), result.ResolvedAddress);
        Assert.NotNull(result.DnsElapsed);
        Assert.NotNull(result.TcpElapsed);
    }

    // The two durations are reported separately and read separately, so which stopwatch feeds
    // which field is a claim of its own. Holding name resolution open makes the DNS leg the long
    // one by construction, and a loopback connect to an already-bound port is the short one; if
    // the two arguments are ever swapped, the ordering inverts. The gate is the thing being
    // measured here, not a stand-in for a state transition.
    [Fact]
    public async Task TestAsync_AttributesTheSlowLegToDnsAndNotToTcp()
    {
        using Listener listener = Listener.OnLoopback();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RdpConnectivityTester sut = new RdpConnectivityTester(async (_, _) =>
        {
            await gate.Task.ConfigureAwait(false);
            return new[] { IPAddress.Loopback };
        });

        Task<RdpConnectivityTestResult> pending = sut.TestAsync(
            "srv.example.test",
            listener.Port,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        await Task.Delay(ResolverGateHold);
        gate.SetResult();
        RdpConnectivityTestResult result = await pending;

        Assert.Equal(RdpConnectivityTestOutcome.Success, result.Outcome);
        Assert.True(
            result.DnsElapsed > result.TcpElapsed,
            $"DNS was held open for {ResolverGateHold.TotalMilliseconds} ms while the TCP leg was "
            + $"a loopback connect to a bound port, yet DNS is reported as "
            + $"{result.DnsElapsed?.TotalMilliseconds} ms against TCP "
            + $"{result.TcpElapsed?.TotalMilliseconds} ms. The elapsed times are attributed to "
            + "the wrong legs.");
    }

    // A name that resolves to several addresses is the ordinary case for a dual-stack host, and
    // the RDP client itself connects by name and walks all of them. Probing only the first and
    // then reporting "the host may be off, unreachable" states more than was measured.
    [Fact]
    public async Task TestAsync_FirstAddressUnreachable_StillFindsTheHostOnALaterOne()
    {
        using Listener listener = Listener.OnLoopback();
        RdpConnectivityTester sut = new RdpConnectivityTester((_, _) =>
            Task.FromResult(new[] { IPAddress.Parse(UnroutableAddress), IPAddress.Loopback }));

        RdpConnectivityTestResult result = await sut.TestAsync(
            "srv.example.test",
            listener.Port,
            TimeSpan.FromSeconds(6),
            CancellationToken.None);

        Assert.Equal(RdpConnectivityTestOutcome.Success, result.Outcome);
        Assert.Equal(IPAddress.Loopback.ToString(), result.ResolvedAddress);
    }

    // The control for the case above. Without it, "walk every address" would also be satisfied
    // by a probe that reports Success on anything.
    [Fact]
    public async Task TestAsync_NoAddressAnswers_StillReportsAFailure()
    {
        RdpConnectivityTester sut = new RdpConnectivityTester((_, _) =>
            Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Loopback }));

        RdpConnectivityTestResult result = await sut.TestAsync(
            "srv.example.test",
            ClosedLoopbackPort,
            TimeSpan.FromSeconds(4),
            CancellationToken.None);

        Assert.True(
            result.Outcome is RdpConnectivityTestOutcome.TcpFailed
                or RdpConnectivityTestOutcome.TcpTimeout,
            $"Unexpected outcome: {result.Outcome}");
    }

    // DnsNoResults had no reader either, and it is the one outcome a real resolver will not
    // produce on demand.
    [Fact]
    public async Task TestAsync_ResolverReturnsNothing_ReportsDnsNoResults()
    {
        RdpConnectivityTester sut = new RdpConnectivityTester((_, _) =>
            Task.FromResult(Array.Empty<IPAddress>()));

        RdpConnectivityTestResult result = await sut.TestAsync(
            "srv.example.test",
            DefaultRdpPort,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(RdpConnectivityTestOutcome.DnsNoResults, result.Outcome);
    }

    [Fact]
    public async Task TestAsync_ResolverThrows_ReportsDnsFailedAndKeepsTheDetail()
    {
        const string detail = "host not found in this fixture";
        RdpConnectivityTester sut = new RdpConnectivityTester((_, _) =>
            Task.FromException<IPAddress[]>(
                new SocketException((int)SocketError.HostNotFound, detail)));

        RdpConnectivityTestResult result = await sut.TestAsync(
            "srv.example.test",
            DefaultRdpPort,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(RdpConnectivityTestOutcome.DnsFailed, result.Outcome);
        Assert.Equal(detail, result.Detail);
    }

    // Resolution that outlives the budget is a DNS timeout, not a TCP one: the two send the
    // reader to different places.
    [Fact]
    public async Task TestAsync_ResolverOutlivesTheBudget_ReportsDnsTimeout()
    {
        RdpConnectivityTester sut = new RdpConnectivityTester(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new[] { IPAddress.Loopback };
        });

        RdpConnectivityTestResult result = await sut.TestAsync(
            "srv.example.test",
            DefaultRdpPort,
            ShortDnsBudget,
            CancellationToken.None);

        Assert.Equal(RdpConnectivityTestOutcome.DnsTimeout, result.Outcome);
    }

    // TEST-NET-3 (RFC 5737): reserved for documentation, so nothing routes there and a connect
    // either hangs until its slice of the budget runs out or is refused by the local stack.
    private const string UnroutableAddress = "203.0.113.1";

    // Reserved by IANA and never a listening RDP port.
    private const int ClosedLoopbackPort = 1;

    private const int DefaultRdpPort = 3389;

    private static readonly TimeSpan ResolverGateHold = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan ShortDnsBudget = TimeSpan.FromMilliseconds(200);

    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _listener;

        private Listener(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public static Listener OnLoopback()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new Listener(listener);
        }

        public void Dispose() => _listener.Stop();
    }
}
