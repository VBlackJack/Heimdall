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

namespace Heimdall.Ssh.Tests;

public sealed class ServerHealthMonitorNetTests
{
    [Fact]
    public void TryParseNetworkBytes_SumsNonLoopbackInterfacesAndIgnoresLo()
    {
        const string procNetDev = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo: 9999999   1000    0    0    0     0          0         0  9999999   1000    0    0    0     0       0          0
              eth0: 1000000    500    0    0    0     0          0         0   250000    200    0    0    0     0       0          0
              ens3:  500000    100    0    0    0     0          0         0    50000     50    0    0    0     0       0          0
            """;

        var ok = ServerHealthMonitor.TryParseNetworkBytes(procNetDev, out var rx, out var tx);

        Assert.True(ok);
        Assert.Equal(1_500_000, rx);
        Assert.Equal(300_000, tx);
    }

    [Fact]
    public void ComputeNetworkRates_ComputesPositiveDeltaPerSecond_AndGuardsCounterReset()
    {
        using var monitor = new ServerHealthMonitor();
        var t0 = new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);

        Assert.Null(monitor.ComputeNetworkRates(10_000, 5_000, t0));

        var r1 = monitor.ComputeNetworkRates(25_000, 10_000, t0.AddSeconds(10));
        Assert.NotNull(r1);
        Assert.Equal(1500, r1.Value.RxBytesPerSec);
        Assert.Equal(500, r1.Value.TxBytesPerSec);

        // Counter reset / wrap must return null instead of negative throughput.
        Assert.Null(monitor.ComputeNetworkRates(1_000, 500, t0.AddSeconds(20)));
    }

    [Theory]
    [InlineData(512, "512 B/s")]
    [InlineData(2048, "2.0 KB/s")]
    [InlineData(2_097_152, "2.0 MB/s")]
    public void FormatNetworkRate_FormatsHumanReadableUnits(long bytesPerSec, string expected)
    {
        Assert.Equal(expected, ServerHealthMonitor.FormatNetworkRate(bytesPerSec));
    }

    [Fact]
    public void CombinedHealthCommand_IncludesNetworkMarker()
    {
        Assert.Contains(ServerHealthMonitor.MarkerNet, ServerHealthMonitor.CombinedHealthCommandText, StringComparison.Ordinal);
        Assert.Contains("/proc/net/dev", ServerHealthMonitor.CombinedHealthCommandText, StringComparison.Ordinal);
    }
}
