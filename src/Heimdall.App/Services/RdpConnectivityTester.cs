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
using System.Net;
using System.Net.Sockets;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Lightweight RDP reachability test: DNS resolution followed by a TCP connect
/// on the target port. It deliberately does not negotiate TLS, NLA, or RDP.
/// </summary>
internal sealed class RdpConnectivityTester
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveAddresses;

    public RdpConnectivityTester()
        : this(static (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken))
    {
    }

    /// <summary>
    /// Test seam over name resolution.
    /// </summary>
    /// <remarks>
    /// How the probe behaves on a name that resolves to several addresses is the whole point of
    /// the loop below, and a loopback fixture resolves to exactly one - so without this seam the
    /// multi-address path cannot be measured at all.
    /// </remarks>
    internal RdpConnectivityTester(Func<string, CancellationToken, Task<IPAddress[]>> resolveAddresses)
    {
        _resolveAddresses = resolveAddresses;
    }

    public async Task<RdpConnectivityTestResult> TestAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var trimmedHost = host?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedHost)
            || !InputValidator.Validate(trimmedHost, "Address"))
        {
            return RdpConnectivityTestResult.InvalidAddress();
        }

        if (!InputValidator.ValidatePortRange(port))
        {
            return RdpConnectivityTestResult.InvalidPort();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return RdpConnectivityTestResult.Cancelled();
        }

        var dnsStopwatch = Stopwatch.StartNew();
        IPAddress[] addresses;
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);

            addresses = await _resolveAddresses(trimmedHost, linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RdpConnectivityTestResult.Cancelled();
        }
        catch (OperationCanceledException)
        {
            return RdpConnectivityTestResult.DnsTimeout(timeout);
        }
        catch (SocketException ex)
        {
            return RdpConnectivityTestResult.DnsFailed(ex.Message);
        }

        dnsStopwatch.Stop();
        if (addresses.Length == 0)
        {
            return RdpConnectivityTestResult.DnsNoResults();
        }

        var tcpStopwatch = Stopwatch.StartNew();
        RdpConnectivityTestResult? lastFailure = null;

        // Every address the name resolved to, not just the first. A dual-stack host whose AAAA
        // sorts ahead of its A under RFC 6724 answers on the second one at a site with no IPv6
        // routing, and the RDP client - which connects by name - gets there. Reporting "the host
        // may be off, unreachable" after dialling one of several addresses says more than was
        // measured.
        for (var index = 0; index < addresses.Length; index++)
        {
            var resolvedAddress = addresses[index];

            // The caller's budget is shared out over the addresses left rather than granted to
            // each in turn, so widening the probe cannot multiply the wait the user sits through.
            var remaining = timeout - tcpStopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var attemptBudget = TimeSpan.FromTicks(remaining.Ticks / (addresses.Length - index));

            try
            {
                using var socket = new TcpClient(resolvedAddress.AddressFamily);
                using var timeoutCts = new CancellationTokenSource(attemptBudget);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token);

                await socket.ConnectAsync(resolvedAddress, port, linkedCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return RdpConnectivityTestResult.Cancelled();
            }
            catch (OperationCanceledException)
            {
                lastFailure = RdpConnectivityTestResult.TcpTimeout(
                    resolvedAddress.ToString(),
                    attemptBudget);
                continue;
            }
            catch (SocketException ex)
            {
                lastFailure = RdpConnectivityTestResult.TcpFailed(
                    resolvedAddress.ToString(),
                    ex.SocketErrorCode,
                    ex.Message);
                continue;
            }

            tcpStopwatch.Stop();
            return RdpConnectivityTestResult.Success(
                resolvedAddress.ToString(),
                dnsStopwatch.Elapsed,
                tcpStopwatch.Elapsed);
        }

        // The loop only ends without a verdict when the budget ran out before an address could be
        // tried, which is the same thing the first address timing out already reported.
        return lastFailure
            ?? RdpConnectivityTestResult.TcpTimeout(addresses[0].ToString(), timeout);
    }
}

internal sealed record RdpConnectivityTestResult(
    RdpConnectivityTestOutcome Outcome,
    string? ResolvedAddress,
    TimeSpan? DnsElapsed,
    TimeSpan? TcpElapsed,
    string? Detail,
    SocketError? SocketError)
{
    public static RdpConnectivityTestResult Success(string address, TimeSpan dnsElapsed, TimeSpan tcpElapsed)
        => new(RdpConnectivityTestOutcome.Success, address, dnsElapsed, tcpElapsed, null, null);

    public static RdpConnectivityTestResult InvalidAddress()
        => new(RdpConnectivityTestOutcome.InvalidAddress, null, null, null, null, null);

    public static RdpConnectivityTestResult InvalidPort()
        => new(RdpConnectivityTestOutcome.InvalidPort, null, null, null, null, null);

    public static RdpConnectivityTestResult DnsTimeout(TimeSpan timeout)
        => new(RdpConnectivityTestOutcome.DnsTimeout, null, timeout, null, null, null);

    public static RdpConnectivityTestResult DnsFailed(string detail)
        => new(RdpConnectivityTestOutcome.DnsFailed, null, null, null, detail, null);

    public static RdpConnectivityTestResult DnsNoResults()
        => new(RdpConnectivityTestOutcome.DnsNoResults, null, null, null, null, null);

    public static RdpConnectivityTestResult TcpTimeout(string address, TimeSpan timeout)
        => new(RdpConnectivityTestOutcome.TcpTimeout, address, null, timeout, null, null);

    public static RdpConnectivityTestResult TcpFailed(string address, SocketError socketError, string detail)
        => new(RdpConnectivityTestOutcome.TcpFailed, address, null, null, detail, socketError);

    public static RdpConnectivityTestResult Cancelled()
        => new(RdpConnectivityTestOutcome.Cancelled, null, null, null, null, null);
}

internal enum RdpConnectivityTestOutcome
{
    Success,
    InvalidAddress,
    InvalidPort,
    DnsTimeout,
    DnsFailed,
    DnsNoResults,
    TcpTimeout,
    TcpFailed,
    Cancelled
}
