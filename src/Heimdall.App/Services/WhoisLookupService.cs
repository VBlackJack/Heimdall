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
using System.Net.Sockets;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Contract for a single WHOIS lookup. The service is locale-free: failures are
/// reported with <see cref="WhoisLookupResult.ErrorKey"/> and an optional
/// <see cref="WhoisLookupResult.ErrorArg"/>.
/// </summary>
public interface IWhoisLookupService
{
    /// <summary>
    /// Sets or clears the SSH gateway used for tunnel-based WHOIS queries.
    /// </summary>
    void SetGateway(SshGatewayDto? gateway);

    /// <summary>
    /// Performs a WHOIS lookup according to the supplied request. The service
    /// trusts its input; callers are responsible for validating the domain or IP.
    /// </summary>
    Task<WhoisLookupResult> LookupAsync(WhoisLookupRequest request, CancellationToken ct);
}

/// <summary>
/// Stateful service that performs direct or tunnel-based WHOIS lookups.
/// </summary>
public sealed class WhoisLookupService : IWhoisLookupService
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private const int WhoisPort = 43;

    private readonly Func<string, CancellationToken, Task<string>> _directQuery;
    private readonly Func<SshGatewayDto, string, CancellationToken, Task<string>> _tunnelQuery;
    private SshGatewayDto? _gateway;

    public WhoisLookupService(SshGatewayDto? gateway = null)
        : this(gateway, DirectQueryAsync, TunnelQueryAsync)
    {
    }

    internal WhoisLookupService(
        SshGatewayDto? gateway,
        Func<string, CancellationToken, Task<string>> directQuery,
        Func<SshGatewayDto, string, CancellationToken, Task<string>> tunnelQuery)
    {
        _gateway = gateway;
        _directQuery = directQuery ?? throw new ArgumentNullException(nameof(directQuery));
        _tunnelQuery = tunnelQuery ?? throw new ArgumentNullException(nameof(tunnelQuery));
    }

    public void SetGateway(SshGatewayDto? gateway) => _gateway = gateway;

    public async Task<WhoisLookupResult> LookupAsync(WhoisLookupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Domain))
        {
            throw new ArgumentException("Domain must be non-empty.", nameof(request));
        }

        var domain = request.Domain.Trim();
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = new CancellationTokenSource(QueryTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var output = _gateway is not null
                ? await _tunnelQuery(_gateway, domain, linkedCts.Token).ConfigureAwait(false)
                : await _directQuery(domain, linkedCts.Token).ConfigureAwait(false);

            stopwatch.Stop();
            return WhoisLookupResult.Ok(output, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return WhoisLookupResult.Error("ToolWhoisErrorTimeout", stopwatch.ElapsedMilliseconds);
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();
            return WhoisLookupResult.Error("ToolWhoisErrorFailed", stopwatch.ElapsedMilliseconds, ex.Message);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return WhoisLookupResult.Error("ToolWhoisErrorFailed", stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    private static async Task<string> DirectQueryAsync(string domain, CancellationToken ct)
    {
        var server = WhoisServerResolver.GetWhoisServer(domain);

        using var client = new TcpClient();
        await client.ConnectAsync(server, WhoisPort, ct).ConfigureAwait(false);

        await using var stream = client.GetStream();
        var query = Encoding.ASCII.GetBytes(domain + "\r\n");
        await stream.WriteAsync(query, ct).ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> TunnelQueryAsync(
        SshGatewayDto gateway,
        string domain,
        CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            using var client = await ToolGatewayConnector.ConnectAsync(gateway, ct).ConfigureAwait(false);
            try
            {
                var safeDomain = InputValidator.EscapeShellArg(domain);
                using var cmd = client.CreateCommand($"whois {safeDomain} 2>&1");
                cmd.CommandTimeout = QueryTimeout;
                return cmd.Execute()?.Trim() ?? string.Empty;
            }
            finally
            {
                try
                {
                    client.Disconnect();
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }, ct).ConfigureAwait(false);
    }
}
