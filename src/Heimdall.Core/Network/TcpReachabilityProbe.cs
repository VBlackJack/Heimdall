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
using System.Net.Sockets;

namespace Heimdall.Core.Network;

/// <summary>
/// Outcome of a single TCP reachability probe. Locale-free.
/// On success: <see cref="Reachable"/> is <c>true</c>, <see cref="LatencyMs"/> is the
/// measured round trip, and <see cref="Error"/> is <c>null</c>. On any normal connect
/// failure: <see cref="Reachable"/> is <c>false</c>, <see cref="LatencyMs"/> is -1, and
/// <see cref="Error"/> holds a short reason ("timeout", "refused", a message, ...).
/// </summary>
public sealed record TcpReachabilityResult(bool Reachable, double LatencyMs, string? Error);

/// <summary>
/// Minimal TCP connect probe: opens a socket to <c>host:port</c> and reports whether the
/// connection succeeded and how long it took. A normal connect failure (timeout, refused,
/// DNS failure, unreachable) is returned as an unreachable result, never thrown. Only a
/// cancellation requested by the caller's own token propagates as
/// <see cref="OperationCanceledException"/>; the internal per-probe timeout does not.
/// </summary>
public static class TcpReachabilityProbe
{
    /// <summary>Default per-probe connect timeout in milliseconds.</summary>
    public const int DefaultTimeoutMs = 2000;

    /// <summary>
    /// Attempts a TCP connection to <paramref name="host"/> on <paramref name="port"/>,
    /// bounded by <paramref name="timeoutMs"/>. Returns a reachable/unreachable result; a
    /// connect failure is reported, not thrown.
    /// </summary>
    public static async Task<TcpReachabilityResult> ProbeAsync(
        string host,
        int port,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return new TcpReachabilityResult(true, stopwatch.Elapsed.TotalMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled (not our internal per-probe timeout): propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            // The internal timeout elapsed before the connection completed.
            return new TcpReachabilityResult(false, -1.0, "timeout");
        }
        catch (SocketException ex)
        {
            return new TcpReachabilityResult(false, -1.0, ClassifySocketError(ex));
        }
        catch (Exception ex)
        {
            return new TcpReachabilityResult(false, -1.0, ex.Message);
        }
    }

    /// <summary>
    /// Maps the most common socket failures to a short, locale-free reason; falls back to
    /// the framework message for anything else.
    /// </summary>
    private static string ClassifySocketError(SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "refused",
            SocketError.TimedOut => "timeout",
            SocketError.HostNotFound => "dns failure",
            SocketError.HostUnreachable => "unreachable",
            SocketError.NetworkUnreachable => "unreachable",
            _ => ex.Message
        };
    }
}
