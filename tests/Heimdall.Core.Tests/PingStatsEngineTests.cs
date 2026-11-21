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

using Heimdall.Core.Network;

namespace Heimdall.Core.Tests;

public class PingStatsEngineTests
{
    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal(60, PingStatsEngine.MaxDataPoints);
        Assert.Equal(2000, PingStatsEngine.DefaultPingTimeoutMs);
        Assert.Equal(0, PingStatsEngine.DefaultPingCount);
        Assert.Equal(100, PingStatsEngine.MinTimeoutMs);
        Assert.Equal(30000, PingStatsEngine.MaxTimeoutMs);
    }

    [Theory]
    [InlineData("64 bytes from 1.1.1.1: icmp_seq=1 ttl=58 time=12.3 ms", 12L)]
    [InlineData("reply: time=7ms ttl=64", 7L)]
    [InlineData("time<1ms", 1L)]
    [InlineData("time=0.4 ms", 0L)]
    public void ParsePingLatency_ValidOutput_ReturnsMs(string output, long expected)
    {
        var result = PingStatsEngine.ParsePingLatency(output);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Request timed out")]
    [InlineData("ttl=64 without time")]
    public void ParsePingLatency_InvalidOutput_ReturnsNull(string output)
    {
        Assert.Null(PingStatsEngine.ParsePingLatency(output));
    }

    [Fact]
    public void ValidateInputs_EmptyHost_ReturnsError()
    {
        var (inputs, errorKey) = PingStatsEngine.ValidateInputs("", "1000", "");
        Assert.Null(inputs);
        Assert.Equal("ToolValidationHostRequired", errorKey);
    }

    [Theory]
    [InlineData("-f")]
    [InlineData("--help")]
    [InlineData("-c 1")]
    [InlineData("a b")]
    public void ValidateInputs_OptionLikeOrSpacedHost_IsRefused(string host)
    {
        var (inputs, errorKey) = PingStatsEngine.ValidateInputs(host, "1000", "");
        Assert.Null(inputs);
        Assert.Equal("ErrorInvalidHost", errorKey);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%12")]
    [InlineData("my_host")]
    [InlineData("host.example.com.")]
    [InlineData("1e100.net")]
    [InlineData("3com.com")]
    [InlineData("10.0.0.1")]
    [InlineData("example.com")]
    public void ValidateInputs_ValidHost_IsAccepted(string host)
    {
        var (inputs, errorKey) = PingStatsEngine.ValidateInputs(host, "1000", "");
        Assert.NotNull(inputs);
        Assert.Null(errorKey);
    }

    [Fact]
    public void ValidateInputs_InvalidTimeout_ReturnsError()
    {
        var (inputs, errorKey) = PingStatsEngine.ValidateInputs("example.com", "50", "");
        Assert.Null(inputs);
        Assert.Equal("ToolPingValidationTimeout", errorKey);
    }

    [Fact]
    public void ValidateInputs_InvalidCount_ReturnsError()
    {
        var (inputs, errorKey) = PingStatsEngine.ValidateInputs("example.com", "1000", "-1");
        Assert.Null(inputs);
        Assert.Equal("ToolPingValidationCount", errorKey);
    }

    [Fact]
    public void ValidateInputs_Valid_ReturnsInputs()
    {
        var (inputs, errorKey) = PingStatsEngine.ValidateInputs("  example.com ", "1500", "5");
        Assert.Null(errorKey);
        Assert.NotNull(inputs);
        Assert.Equal("example.com", inputs!.Host);
        Assert.Equal(1500, inputs.TimeoutMs);
        Assert.Equal(5, inputs.Count);
    }

    [Fact]
    public void ComputeStats_Empty_ReturnsZeroSnapshot()
    {
        var stats = PingStatsEngine.ComputeStats([]);
        Assert.Equal(0, stats.Sent);
        Assert.Equal(0, stats.Received);
        Assert.Equal(0, stats.Lost);
        Assert.Equal(0, stats.LossPercent);
    }

    [Fact]
    public void ComputeStats_MixedResults_ComputesAggregates()
    {
        var stats = PingStatsEngine.ComputeStats(
        [
            new PingProbeResult(1, "10:00:00", 10, PingStatus.Success, 64, "OK", "1.1.1.1"),
            new PingProbeResult(2, "10:00:01", 20, PingStatus.Success, 64, "OK", "1.1.1.1"),
            new PingProbeResult(3, "10:00:02", -1, PingStatus.Timeout, 0, "Timeout", "1.1.1.1"),
        ]);

        Assert.Equal(10, stats.Min);
        Assert.Equal(20, stats.Max);
        Assert.Equal(15, stats.Avg);
        Assert.Equal(3, stats.Sent);
        Assert.Equal(2, stats.Received);
        Assert.Equal(1, stats.Lost);
        Assert.Equal(33.333333333333336d, stats.LossPercent);
        Assert.True(stats.Jitter > 0);
    }

    [Fact]
    public void ComputeGraphYScale_Empty_UsesFloor()
    {
        var scale = PingStatsEngine.ComputeGraphYScale([]);
        Assert.Equal(10, scale.MaxY);
        Assert.Equal(5, scale.MidY);
    }

    [Fact]
    public void ComputeGraphYScale_BelowFloor_ClampedToTen()
    {
        var scale = PingStatsEngine.ComputeGraphYScale([new PingDataPoint(3, false), new PingDataPoint(5, false)]);
        Assert.Equal(10, scale.MaxY);
    }

    [Fact]
    public void ComputeGraphYScale_Adds20PercentHeadroom()
    {
        var scale = PingStatsEngine.ComputeGraphYScale([new PingDataPoint(100, false), new PingDataPoint(50, false)]);
        Assert.Equal(120, scale.MaxY);
        Assert.Equal(60, scale.MidY);
    }

    [Fact]
    public void ComputeGraphYScale_IgnoresTimeouts()
    {
        var scale = PingStatsEngine.ComputeGraphYScale(
        [
            new PingDataPoint(10, false),
            new PingDataPoint(999, true),
            new PingDataPoint(20, false),
        ]);

        Assert.Equal(24, scale.MaxY);
    }

    [Fact]
    public void BuildCsvExport_Empty_ReturnsHeaderOnly()
    {
        var csv = PingStatsEngine.BuildCsvExport([]);
        Assert.Single(csv.Trim().Split('\n'));
    }

    [Fact]
    public void BuildCsvExport_WithSuccess_IncludesLatency()
    {
        var csv = PingStatsEngine.BuildCsvExport(
        [
            new PingProbeResult(1, "10:00:00", 12, PingStatus.Success, 64, "OK", "1.1.1.1"),
        ]);

        var lines = csv.Trim().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("12", lines[1]);
        Assert.Contains("OK", lines[1]);
    }

    [Fact]
    public void BuildCsvExport_Timeout_EmptyLatencyField()
    {
        var csv = PingStatsEngine.BuildCsvExport(
        [
            new PingProbeResult(1, "10:00:00", -1, PingStatus.Timeout, 0, "TimedOut", "1.1.1.1"),
        ]);

        Assert.Matches(@"1,10:00:00,,Timeout", csv);
    }

    [Fact]
    public void BuildCsvExport_Error_FormatsWithPrefix()
    {
        var csv = PingStatsEngine.BuildCsvExport(
        [
            new PingProbeResult(1, "10:00:00", -1, PingStatus.Error, 0, "DNS failure", "1.1.1.1"),
        ]);

        Assert.Contains("Error: DNS failure", csv);
    }

    [Fact]
    public void BuildCsvExport_SanitizesCsvInjection()
    {
        var csv = PingStatsEngine.BuildCsvExport(
        [
            new PingProbeResult(1, "=cmd()", -1, PingStatus.Error, 0, "=err()", "h"),
        ]);

        Assert.DoesNotContain(",=cmd()", csv, StringComparison.Ordinal);
        Assert.Contains("'=cmd()", csv);
        Assert.Contains("Error: =err()", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCsvExport_UsesLocalizedHeader()
    {
        string Localize(string key) => key == "ToolPingCsvHeader" ? "Seq,Heure,Latence,Statut" : key;

        var csv = PingStatsEngine.BuildCsvExport([], Localize);
        Assert.StartsWith("Seq,Heure,Latence,Statut", csv.Trim(), StringComparison.Ordinal);
    }
}
