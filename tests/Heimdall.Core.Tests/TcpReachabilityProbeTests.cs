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
using Heimdall.Core.Network;

namespace Heimdall.Core.Tests;

public class TcpReachabilityProbeTests
{
    [Fact]
    public async Task ProbeAsync_OpenPort_ReturnsReachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var result = await TcpReachabilityProbe.ProbeAsync(
                "127.0.0.1", port, TcpReachabilityProbe.DefaultTimeoutMs);

            Assert.True(result.Reachable);
            Assert.True(result.LatencyMs >= 0);
            Assert.Null(result.Error);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ProbeAsync_ClosedPort_ReturnsUnreachableWithoutThrowing()
    {
        // The port is RESERVED for the whole probe, not released before it. Binding a listener
        // and stopping it to "obtain a free port" is a time-of-check/time-of-use hole: from the
        // moment it is released the ephemeral port belongs to whoever asks for one next, and the
        // probe then measures whatever took it - which is how this test once observed
        // Reachable=true. Holding the endpoint makes a reachable result impossible by
        // construction rather than merely improbable.
        using Socket reservation = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // Set before Bind: the option is rejected on an already-bound socket, and it is what
        // stops a competing socket from claiming the endpoint with ReuseAddress.
        reservation.ExclusiveAddressUse = true;
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.LocalEndPoint!).Port;

        // Deliberately no Listen call. A bound socket with no listener accepts nothing, so the
        // endpoint is closed to connections while still being owned by this test.
        AssertEndpointCannotBeTakenWhileReserved(port);

        var result = await TcpReachabilityProbe.ProbeAsync("127.0.0.1", port, 1000);

        Assert.False(result.Reachable);
        Assert.Equal(-1.0, result.LatencyMs);
        Assert.False(string.IsNullOrEmpty(result.Error));

        // The reservation has to outlive the measurement, not merely precede it. Disposal is
        // scoped to the end of the method; this pins the socket against an early collection.
        GC.KeepAlive(reservation);
    }

    /// <summary>
    /// Proves the reservation is exclusive for as long as it is held, which is the property the
    /// closed-port probe depends on: no other socket can be listening on that endpoint.
    /// </summary>
    /// <remarks>
    /// Asserting that a bind throws, rather than sampling the probe repeatedly, is what makes
    /// this non-probabilistic. If the reservation ever stopped being exclusive, these binds would
    /// succeed and say so immediately instead of leaving a rare failure for CI to find.
    /// </remarks>
    private static void AssertEndpointCannotBeTakenWhileReserved(int port)
    {
        // A plain competitor.
        using Socket plainChallenger = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        AssertBindIsRefused(plainChallenger, port);

        // The competitor ExclusiveAddressUse exists to defeat. Without that option this bind can
        // take the endpoint out from under the reservation on Windows, and the probe would then
        // be measuring a port this test no longer owns.
        using Socket reuseChallenger = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reuseChallenger.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        AssertBindIsRefused(reuseChallenger, port);
    }

    private static void AssertBindIsRefused(Socket challenger, int port)
    {
        SocketException refusal = Assert.Throws<SocketException>(
            () => challenger.Bind(new IPEndPoint(IPAddress.Loopback, port)));

        // Which of the two arrives is the platform's business: Windows answers AccessDenied to a
        // ReuseAddress bind against an exclusively held endpoint and AddressAlreadyInUse to a
        // plain one. Naming both keeps an unrelated socket failure from passing as proof.
        Assert.Contains(
            refusal.SocketErrorCode,
            new[] { SocketError.AddressAlreadyInUse, SocketError.AccessDenied });
    }

    [Fact]
    public async Task ProbeAsync_UnroutableAddress_TimesOutWithoutThrowing()
    {
        // 10.255.255.1 is not routable on the test host; a tiny timeout keeps the test fast.
        var result = await TcpReachabilityProbe.ProbeAsync("10.255.255.1", 9, 200);

        Assert.False(result.Reachable);
        Assert.Equal(-1.0, result.LatencyMs);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }
}
