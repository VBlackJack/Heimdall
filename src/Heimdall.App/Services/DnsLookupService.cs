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
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Contract for a single DNS record lookup. Engines are locale-free at
/// construction time; callers pass a <c>localize</c> delegate into
/// <see cref="LookupAsync"/> so the few human-facing strings emitted by the
/// platform resolver path ("No IPv4 addresses found.", "Aliases:") can be
/// formatted under the current culture. Failures are reported with
/// <see cref="DnsLookupResult.ErrorKey"/> + optional
/// <see cref="DnsLookupResult.ErrorArg"/> so the view layer can re-project
/// them on locale change.
/// </summary>
public interface IDnsLookupService
{
    /// <summary>
    /// Sets or clears the SSH gateway used for tunnel-based DNS queries.
    /// When null, the platform default resolver (or local <c>nslookup</c>) is used.
    /// </summary>
    void SetGateway(SshGatewayDto? gateway);

    /// <summary>
    /// Performs a DNS lookup according to the supplied <paramref name="request"/>.
    /// The result is always non-null; inspect <see cref="DnsLookupResult.Success"/>
    /// to decide between the <see cref="DnsLookupResult.Output"/> path and the
    /// <see cref="DnsLookupResult.ErrorKey"/> path.
    /// </summary>
    /// <param name="request">Hostname, record type, and optional DNS server.</param>
    /// <param name="localize">
    /// Key-to-string resolver. Used only on the platform-resolver path to
    /// format "No IPv4/IPv6 addresses found." and the "Aliases:" header.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<DnsLookupResult> LookupAsync(
        DnsLookupRequest request,
        Func<string, string> localize,
        CancellationToken ct);
}

/// <summary>
/// Stateful service that performs local or tunnel-based DNS record lookups.
/// </summary>
public sealed class DnsLookupService : IDnsLookupService
{
    /// <summary>Executable that resolves DNS records locally.</summary>
    internal const string NslookupExecutableName = "nslookup.exe";

    /// <summary>
    /// Remote command timeout for the SSH gateway path (<c>dig</c>/<c>nslookup</c>/<c>host</c>).
    /// </summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);

    private readonly Func<string, string, Func<string, string>, CancellationToken, Task<string>> _hostEntryQuery;
    private readonly Func<string, string, string?, CancellationToken, Task<string>> _localNslookupQuery;
    private readonly Func<SshGatewayDto, string, string, string?, CancellationToken, Task<string>> _tunnelQuery;
    private SshGatewayDto? _gateway;

    public DnsLookupService(SshGatewayDto? gateway = null)
        : this(gateway, HostEntryQueryAsync, LocalNslookupQueryAsync, TunnelQueryAsync)
    {
    }

    internal DnsLookupService(
        SshGatewayDto? gateway,
        Func<string, string, Func<string, string>, CancellationToken, Task<string>> hostEntryQuery,
        Func<string, string, string?, CancellationToken, Task<string>> localNslookupQuery,
        Func<SshGatewayDto, string, string, string?, CancellationToken, Task<string>> tunnelQuery)
    {
        _gateway = gateway;
        _hostEntryQuery = hostEntryQuery ?? throw new ArgumentNullException(nameof(hostEntryQuery));
        _localNslookupQuery = localNslookupQuery ?? throw new ArgumentNullException(nameof(localNslookupQuery));
        _tunnelQuery = tunnelQuery ?? throw new ArgumentNullException(nameof(tunnelQuery));
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    public async Task<DnsLookupResult> LookupAsync(
        DnsLookupRequest request,
        Func<string, string> localize,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(localize);
        ct.ThrowIfCancellationRequested();

        var hostname = request.Hostname?.Trim() ?? string.Empty;
        var dnsServer = string.IsNullOrWhiteSpace(request.DnsServer) ? null : request.DnsServer.Trim();
        var recordToken = request.RecordType.ToWireFormat();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!TryValidateLookupInputs(hostname, dnsServer, out var validationError))
            {
                stopwatch.Stop();
                return DnsLookupResult.Error("ToolDnsErrorLookupFailed", stopwatch.ElapsedMilliseconds, validationError);
            }

            string output;

            if (_gateway is not null)
            {
                output = await _tunnelQuery(_gateway, hostname, recordToken, dnsServer, ct).ConfigureAwait(false);
            }
            else if (dnsServer is null && request.RecordType is DnsRecordType.A or DnsRecordType.AAAA)
            {
                // Platform resolver path: preserves GetHostEntry behavior (aliases, IPv4/v6 split).
                output = await _hostEntryQuery(hostname, recordToken, localize, ct).ConfigureAwait(false);
            }
            else
            {
                output = await _localNslookupQuery(hostname, recordToken, dnsServer, ct).ConfigureAwait(false);
            }

            stopwatch.Stop();
            return DnsLookupResult.Ok(output ?? string.Empty, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return DnsLookupResult.Error("ToolDnsErrorTimeout", stopwatch.ElapsedMilliseconds);
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();
            return DnsLookupResult.Error("ToolDnsErrorLookupFailed", stopwatch.ElapsedMilliseconds, ex.Message);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return DnsLookupResult.Error("ToolDnsErrorLookupFailed", stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>
    /// Local A/AAAA resolution via <see cref="Dns.GetHostEntryAsync(string, CancellationToken)"/>,
    /// formatted as a multi-line block (addresses + optional localized "Aliases:" section).
    /// </summary>
    private static async Task<string> HostEntryQueryAsync(
        string hostname,
        string recordType,
        Func<string, string> localize,
        CancellationToken ct)
    {
        var entry = await Dns.GetHostEntryAsync(hostname, ct).ConfigureAwait(false);
        var sb = new StringBuilder();

        foreach (var address in entry.AddressList)
        {
            var isIpv4 = address.AddressFamily == AddressFamily.InterNetwork;
            var isIpv6 = address.AddressFamily == AddressFamily.InterNetworkV6;

            if (recordType == "A" && isIpv4)
            {
                sb.AppendLine(address.ToString());
            }
            else if (recordType == "AAAA" && isIpv6)
            {
                sb.AppendLine(address.ToString());
            }
        }

        if (sb.Length == 0)
        {
            sb.AppendLine(recordType == "A"
                ? localize("ToolDnsNoIpv4")
                : localize("ToolDnsNoIpv6"));
        }

        if (entry.Aliases.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(localize("ToolDnsAliases"));
            foreach (var alias in entry.Aliases)
            {
                sb.AppendLine($"  {alias}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Local <c>nslookup</c> invocation. Output is cleaned via
    /// <see cref="NslookupOutputParser.Parse(string?)"/> to drop the server header block.
    /// </summary>
    private static async Task<string> LocalNslookupQueryAsync(
        string hostname,
        string recordType,
        string? dnsServer,
        CancellationToken ct)
    {
        var psi = CreateLocalNslookupStartInfo(hostname, recordType, dnsServer);

        using var process = new Process { StartInfo = psi };
        process.Start();

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            var parsed = NslookupOutputParser.Parse(output);

            if (string.IsNullOrWhiteSpace(parsed) && !string.IsNullOrWhiteSpace(error))
            {
                return error.Trim();
            }

            return string.IsNullOrWhiteSpace(parsed) ? (output ?? string.Empty).Trim() : parsed;
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    internal static ProcessStartInfo CreateLocalNslookupStartInfo(
        string hostname,
        string recordType,
        string? dnsServer)
    {
        if (!TryValidateLookupInputs(hostname, dnsServer, out var validationError))
        {
            throw new ArgumentException(validationError);
        }

        var psi = new ProcessStartInfo
        {
            FileName = SystemExecutablePath.InSystemDirectory(NslookupExecutableName),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = SystemExecutablePath.SystemDirectory,
        };

        psi.ArgumentList.Add($"-type={recordType}");
        psi.ArgumentList.Add(hostname);
        if (!string.IsNullOrWhiteSpace(dnsServer))
        {
            psi.ArgumentList.Add(dnsServer);
        }

        return psi;
    }

    internal static bool TryValidateLookupInputs(
        string hostname,
        string? dnsServer,
        out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(hostname) || hostname.Contains('\0') || !InputValidator.ValidateDomain(hostname))
        {
            errorMessage = $"Invalid DNS hostname: {hostname}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(dnsServer))
        {
            if (dnsServer.Contains('\0')
                || (!InputValidator.ValidateDomain(dnsServer) && !IPAddress.TryParse(dnsServer, out _)))
            {
                errorMessage = $"Invalid DNS server: {dnsServer}";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }

    /// <summary>
    /// Remote DNS lookup via SSH gateway, using a <c>dig → nslookup → host</c>
    /// fallback chain. All user-controlled arguments are shell-escaped via
    /// <see cref="InputValidator.EscapeShellArg(string)"/>.
    /// </summary>
    private static async Task<string> TunnelQueryAsync(
        SshGatewayDto gateway,
        string hostname,
        string recordType,
        string? dnsServer,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var client = ToolGatewayConnector.Connect(gateway);
            try
            {
                var safeHostname = InputValidator.EscapeShellArg(hostname);
                var safeRecordType = InputValidator.EscapeShellArg(recordType);
                var safeDnsServer = dnsServer is not null ? InputValidator.EscapeShellArg(dnsServer) : null;

                // 1. Prefer dig (concise +noall +answer output).
                var serverArg = safeDnsServer is not null ? $"@{safeDnsServer} " : string.Empty;
                using var digCmd = client.CreateCommand(
                    $"dig {serverArg}{safeHostname} {safeRecordType} +noall +answer 2>/dev/null");
                digCmd.CommandTimeout = CommandTimeout;
                var digResult = digCmd.Execute()?.Trim();
                if (!string.IsNullOrWhiteSpace(digResult))
                {
                    return digResult;
                }

                // 2. Fall back to nslookup (common on minimal Linux images).
                var nslookupArgs = safeDnsServer is not null
                    ? $"-type={safeRecordType} {safeHostname} {safeDnsServer}"
                    : $"-type={safeRecordType} {safeHostname}";
                using var nsCmd = client.CreateCommand($"nslookup {nslookupArgs} 2>&1");
                nsCmd.CommandTimeout = CommandTimeout;
                var nsResult = nsCmd.Execute()?.Trim();
                if (!string.IsNullOrWhiteSpace(nsResult))
                {
                    return NslookupOutputParser.Parse(nsResult);
                }

                // 3. Last-resort fallback: host(1).
                var hostDnsArg = safeDnsServer ?? string.Empty;
                using var hostCmd = client.CreateCommand($"host -t {safeRecordType} {safeHostname} {hostDnsArg} 2>&1");
                hostCmd.CommandTimeout = CommandTimeout;
                return hostCmd.Execute()?.Trim() ?? string.Empty;
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
