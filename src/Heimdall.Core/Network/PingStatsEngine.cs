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

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.Core.Security;

namespace Heimdall.Core.Network;

/// <summary>
/// Pure ping engine: constants, data records, output parsing, input validation,
/// stats computation, graph Y-axis scaling, and CSV export formatting.
/// </summary>
public static class PingStatsEngine
{
    public const int MaxDataPoints = 60;
    public const int DefaultPingTimeoutMs = 2000;
    public const int DefaultPingCount = 0;
    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 30000;
    public const int MinCount = 0;
    public const int MaxCount = 100000;

    private static readonly Regex PingTimeRegex = new(
        @"time[=<]\s*(\d+(?:\.\d+)?)\s*ms",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts the round-trip time in milliseconds from a ping command's
    /// textual output.
    /// </summary>
    public static long? ParsePingLatency(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        var match = PingTimeRegex.Match(output);
        if (!match.Success)
        {
            return null;
        }

        if (!double.TryParse(
                match.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var latencyDouble))
        {
            return null;
        }

        return (long)Math.Round(latencyDouble);
    }

    /// <summary>
    /// Validates raw textual inputs.
    /// </summary>
    public static (PingInputs? Inputs, string? ErrorKey) ValidateInputs(
        string? hostText,
        string? timeoutText,
        string? countText)
    {
        var host = (hostText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return (null, "ToolValidationHostRequired");
        }

        // A leading "-" would be read as an option by the remote ping command. IPv6 is accepted.
        if (!InputValidator.IsValidDiagnosticHost(host))
        {
            return (null, "ErrorInvalidHost");
        }

        var timeoutRaw = (timeoutText ?? string.Empty).Trim();
        if (!int.TryParse(timeoutRaw, out var timeoutMs) ||
            timeoutMs < MinTimeoutMs ||
            timeoutMs > MaxTimeoutMs)
        {
            return (null, "ToolPingValidationTimeout");
        }

        var countRaw = (countText ?? string.Empty).Trim();
        var count = DefaultPingCount;
        if (!string.IsNullOrEmpty(countRaw))
        {
            if (!int.TryParse(countRaw, out count) || count < MinCount || count > MaxCount)
            {
                return (null, "ToolPingValidationCount");
            }
        }

        return (new PingInputs(host, timeoutMs, count), null);
    }

    /// <summary>
    /// Aggregates probe results into min/max/avg/jitter/loss counts.
    /// Jitter is the standard deviation of successful latencies.
    /// </summary>
    public static PingStatsSnapshot ComputeStats(IReadOnlyList<PingProbeResult> results)
    {
        if (results.Count == 0)
        {
            return new PingStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0);
        }

        long min = long.MaxValue;
        long max = 0;
        long total = 0;
        double sumSquared = 0;
        var received = 0;
        var lost = 0;

        foreach (var result in results)
        {
            if (result.Status == PingStatus.Success && result.Latency >= 0)
            {
                received++;
                total += result.Latency;
                sumSquared += result.Latency * (double)result.Latency;
                if (result.Latency < min)
                {
                    min = result.Latency;
                }

                if (result.Latency > max)
                {
                    max = result.Latency;
                }
            }
            else
            {
                lost++;
            }
        }

        if (received == 0)
        {
            min = 0;
        }

        var sent = results.Count;
        var avg = received > 0 ? total / received : 0;
        var mean = received > 0 ? (double)total / received : 0.0;
        var variance = received > 0 ? (sumSquared / received) - (mean * mean) : 0.0;
        var jitter = Math.Sqrt(Math.Max(0, variance));
        var lossPercent = sent > 0 ? (lost * 100.0 / sent) : 0.0;

        return new PingStatsSnapshot(min, max, avg, jitter, lossPercent, sent, received, lost);
    }

    /// <summary>
    /// Computes the Y-axis max and midpoint for the live graph.
    /// </summary>
    public static PingGraphScale ComputeGraphYScale(IReadOnlyList<PingDataPoint> dataPoints)
    {
        long maxY = 1;
        foreach (var point in dataPoints)
        {
            if (!point.IsTimeout && point.Latency > maxY)
            {
                maxY = point.Latency;
            }
        }

        maxY = (long)(maxY * 1.2);
        if (maxY < 10)
        {
            maxY = 10;
        }

        return new PingGraphScale(maxY, maxY / 2);
    }

    /// <summary>
    /// Builds CSV content from the ping probe history.
    /// </summary>
    public static string BuildCsvExport(
        IReadOnlyList<PingProbeResult> results,
        Func<string, string>? localize = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine(L(localize, "ToolPingCsvHeader"));

        foreach (var result in results)
        {
            var latencyString = result.Latency >= 0
                ? result.Latency.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

            var statusCell = result.Status switch
            {
                PingStatus.Success => "OK",
                PingStatus.Timeout => "Timeout",
                PingStatus.Error => $"Error: {result.StatusDetail}",
                _ => result.StatusDetail,
            };

            builder.Append(result.Seq);
            builder.Append(',');
            builder.Append(InputValidator.SanitizeCsvCell(result.Timestamp));
            builder.Append(',');
            builder.Append(latencyString);
            builder.Append(',');
            builder.AppendLine(InputValidator.SanitizeCsvCell(statusCell));
        }

        return builder.ToString();
    }

    private static string L(Func<string, string>? localize, string key)
        => localize?.Invoke(key) ?? key;
}

/// <summary>
/// Single-ping outcome returned by the service.
/// </summary>
public enum PingStatus
{
    Success,
    Timeout,
    Error,
}

/// <summary>
/// Raw result of one ping probe (before UI projection).
/// </summary>
public sealed record PingProbeResult(
    int Seq,
    string Timestamp,
    long Latency,
    PingStatus Status,
    int Ttl,
    string StatusDetail,
    string Address);

/// <summary>
/// Single data point used for the live latency graph.
/// </summary>
public readonly record struct PingDataPoint(long Latency, bool IsTimeout);

/// <summary>
/// Aggregated statistics over a ping session.
/// </summary>
public sealed record PingStatsSnapshot(
    long Min,
    long Max,
    long Avg,
    double Jitter,
    double LossPercent,
    int Sent,
    int Received,
    int Lost);

/// <summary>
/// Y-axis scale parameters for the graph.
/// </summary>
public readonly record struct PingGraphScale(long MaxY, long MidY);

/// <summary>
/// Validated input bundle.
/// </summary>
public sealed record PingInputs(string Host, int TimeoutMs, int Count);
