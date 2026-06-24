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
        // Bind then immediately release to obtain a loopback port nothing is listening on.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await TcpReachabilityProbe.ProbeAsync("127.0.0.1", port, 1000);

        Assert.False(result.Reachable);
        Assert.Equal(-1.0, result.LatencyMs);
        Assert.False(string.IsNullOrEmpty(result.Error));
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
