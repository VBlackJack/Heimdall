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

namespace Heimdall.Core.SessionHealth;

/// <summary>
/// Default <see cref="IHealthProbe"/> implementation. Opens a TCP socket and
/// measures connect latency. Maps the common <see cref="SocketException"/>
/// codes (timeout, refused, host unreachable, DNS) to short reason tags that
/// the sidebar tooltip can localize.
/// </summary>
public sealed class TcpHealthProbe : IHealthProbe
{
    public async Task<HealthState> ProbeAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var latency = (int)Math.Max(1, stopwatch.ElapsedMilliseconds);
            return new HealthState(HealthStatus.Up, DateTime.UtcNow, latency, null);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "timeout");
        }
        catch (SocketException ex)
        {
            return new HealthState(HealthStatus.Down, DateTime.UtcNow, null, ReasonFor(ex.SocketErrorCode));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A reason the user can read: the exception type, shown as it was, reached the tooltip.
            return new HealthState(HealthStatus.Down, DateTime.UtcNow, null, OtherReason);
        }
    }

    /// <summary>The reason tag for an unrecognised failure.</summary>
    public const string OtherReason = "other";

    /// <summary>
    /// The reason tag for a socket failure. Every code maps to a tag the tooltip can name; the
    /// code itself, lower-cased, used to reach the user as "(tryagain)".
    /// </summary>
    internal static string ReasonFor(SocketError code) => code switch
    {
        SocketError.ConnectionRefused or SocketError.ConnectionReset => "refused",
        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "dns",
        SocketError.HostUnreachable
            or SocketError.NetworkUnreachable
            or SocketError.NetworkDown
            or SocketError.AddressNotAvailable => "unreachable",
        SocketError.TimedOut => "timeout",
        _ => OtherReason
    };
}
