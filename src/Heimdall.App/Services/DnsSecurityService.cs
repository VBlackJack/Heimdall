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

using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

public interface IDnsSecurityService
{
    /// <summary>
    /// Runs all six DNS security checks for the supplied domain.
    /// </summary>
    Task<IReadOnlyList<DnsCheckResult>> RunAllChecksAsync(string domain, CancellationToken ct);

    /// <summary>
    /// Sets or clears the SSH gateway used for remote DNS queries.
    /// </summary>
    void SetGateway(SshGatewayDto? gateway);
}

/// <summary>
/// Stateless service that performs local or tunnel-based DNS security checks.
/// </summary>
public sealed class DnsSecurityService : IDnsSecurityService
{
    /// <summary>Executable that resolves DNS records locally.</summary>
    internal const string NslookupExecutableName = "nslookup.exe";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);
    private static readonly FrozenSet<string> AllowedRecordTypes =
        FrozenSet.ToFrozenSet(
        [
            "TXT",
            "CAA",
            "DNSKEY",
            "RRSIG",
            "MX"
        ],
        StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string, CancellationToken, Task<string>> _localQuery;
    private readonly Func<SshGatewayDto, string, string, CancellationToken, Task<string>> _tunnelQuery;
    private SshGatewayDto? _gateway;

    public DnsSecurityService(SshGatewayDto? gateway = null)
        : this(gateway, QueryDnsLocalAsync, QueryDnsViaTunnelAsync)
    {
    }

    internal DnsSecurityService(
        SshGatewayDto? gateway,
        Func<string, string, CancellationToken, Task<string>> localQuery,
        Func<SshGatewayDto, string, string, CancellationToken, Task<string>> tunnelQuery)
    {
        _gateway = gateway;
        _localQuery = localQuery ?? throw new ArgumentNullException(nameof(localQuery));
        _tunnelQuery = tunnelQuery ?? throw new ArgumentNullException(nameof(tunnelQuery));
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    public async Task<IReadOnlyList<DnsCheckResult>> RunAllChecksAsync(string domain, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ct.ThrowIfCancellationRequested();

        var spf = CheckSpfAsync(domain, ct);
        var dkim = CheckDkimAsync(domain, ct);
        var dmarc = CheckDmarcAsync(domain, ct);
        var caa = CheckCaaAsync(domain, ct);
        var dnssec = CheckDnssecAsync(domain, ct);
        var mx = CheckMxAsync(domain, ct);

        await Task.WhenAll(spf, dkim, dmarc, caa, dnssec, mx).ConfigureAwait(false);

        return
        [
            await spf.ConfigureAwait(false),
            await dkim.ConfigureAwait(false),
            await dmarc.ConfigureAwait(false),
            await caa.ConfigureAwait(false),
            await dnssec.ConfigureAwait(false),
            await mx.ConfigureAwait(false),
        ];
    }

    private Task<string> QueryDnsAsync(string type, string domain, CancellationToken ct)
    {
        var validatedRecordType = ValidateRecordType(type);
        var validatedDomain = ValidateLookupDomain(domain);

        return _gateway is null
            ? _localQuery(validatedRecordType, validatedDomain, ct)
            : _tunnelQuery(_gateway, validatedRecordType, validatedDomain, ct);
    }

    private async Task<DnsCheckResult> CheckSpfAsync(string domain, CancellationToken ct)
    {
        try
        {
            var raw = await QueryDnsAsync("TXT", domain, ct).ConfigureAwait(false);
            return DnsSecurityEvaluationEngine.EvaluateSpf(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DnsSecurityEvaluationEngine.BuildErrorResult(DnsCheckKind.Spf, ex.Message);
        }
    }

    private async Task<DnsCheckResult> CheckDkimAsync(string domain, CancellationToken ct)
    {
        try
        {
            DnsCheckResult? firstWarn = null;

            foreach (string selector in DnsSecurityEvaluationEngine.DefaultDkimSelectors)
            {
                string dkimDomain = $"{selector}._domainkey.{domain}";
                string raw = await QueryDnsAsync("TXT", dkimDomain, ct).ConfigureAwait(false);
                DnsCheckResult result = DnsSecurityEvaluationEngine.EvaluateDkim(selector, raw);
                if (result.Status == DnsCheckStatus.Pass)
                {
                    return result;
                }

                if (result.Status == DnsCheckStatus.Warn && firstWarn is null)
                {
                    firstWarn = result;
                }
            }

            return firstWarn ?? DnsSecurityEvaluationEngine.EvaluateDkim(null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DnsSecurityEvaluationEngine.BuildErrorResult(DnsCheckKind.Dkim, ex.Message);
        }
    }

    private async Task<DnsCheckResult> CheckDmarcAsync(string domain, CancellationToken ct)
    {
        try
        {
            var raw = await QueryDnsAsync("TXT", $"_dmarc.{domain}", ct).ConfigureAwait(false);
            return DnsSecurityEvaluationEngine.EvaluateDmarc(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DnsSecurityEvaluationEngine.BuildErrorResult(DnsCheckKind.Dmarc, ex.Message);
        }
    }

    private async Task<DnsCheckResult> CheckCaaAsync(string domain, CancellationToken ct)
    {
        try
        {
            var raw = await QueryDnsAsync("CAA", domain, ct).ConfigureAwait(false);
            return DnsSecurityEvaluationEngine.EvaluateCaa(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DnsSecurityEvaluationEngine.BuildErrorResult(DnsCheckKind.Caa, ex.Message);
        }
    }

    private async Task<DnsCheckResult> CheckDnssecAsync(string domain, CancellationToken ct)
    {
        try
        {
            var dnskeyRaw = await QueryDnsAsync("DNSKEY", domain, ct).ConfigureAwait(false);
            string? rrsigRaw = null;
            if (!DnsSecurityEvaluationEngine.ContainsDnsRecords(dnskeyRaw, "DNSKEY"))
            {
                rrsigRaw = await QueryDnsAsync("RRSIG", domain, ct).ConfigureAwait(false);
            }

            return DnsSecurityEvaluationEngine.EvaluateDnssec(dnskeyRaw, rrsigRaw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DnsSecurityEvaluationEngine.BuildErrorResult(DnsCheckKind.Dnssec, ex.Message);
        }
    }

    private async Task<DnsCheckResult> CheckMxAsync(string domain, CancellationToken ct)
    {
        try
        {
            var raw = await QueryDnsAsync("MX", domain, ct).ConfigureAwait(false);
            return DnsSecurityEvaluationEngine.EvaluateMx(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DnsSecurityEvaluationEngine.BuildErrorResult(DnsCheckKind.Mx, ex.Message);
        }
    }

    private static async Task<string> QueryDnsLocalAsync(string type, string domain, CancellationToken ct)
    {
        var psi = CreateNslookupStartInfo(domain, type);

        using var process = new Process { StartInfo = psi };
        process.Start();

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(output) ? error : output;
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
                    // Best effort cleanup.
                }
            }
        }
    }

    internal static ProcessStartInfo CreateNslookupStartInfo(string domain, string recordType)
    {
        var validatedDomain = ValidateLookupDomain(domain);
        var validatedRecordType = ValidateRecordType(recordType);

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

        psi.ArgumentList.Add($"-type={validatedRecordType}");
        psi.ArgumentList.Add(validatedDomain);
        return psi;
    }

    private static async Task<string> QueryDnsViaTunnelAsync(
        SshGatewayDto gateway,
        string type,
        string domain,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var client = ToolGatewayConnector.Connect(gateway);

            try
            {
                using var digCommand = client.CreateCommand(CreateDigTunnelCommand(type, domain));
                digCommand.CommandTimeout = CommandTimeout;
                var digResult = digCommand.Execute()?.Trim();
                if (!string.IsNullOrWhiteSpace(digResult))
                {
                    return digResult;
                }

                using var nslookupCommand = client.CreateCommand(CreateNslookupTunnelCommand(type, domain));
                nslookupCommand.CommandTimeout = CommandTimeout;
                return nslookupCommand.Execute()?.Trim() ?? string.Empty;
            }
            finally
            {
                try
                {
                    client.Disconnect();
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }, ct).ConfigureAwait(false);
    }

    internal static string CreateDigTunnelCommand(string recordType, string domain)
    {
        var escapedRecordType = InputValidator.EscapeShellArg(ValidateRecordType(recordType));
        var escapedDomain = InputValidator.EscapeShellArg(ValidateLookupDomain(domain));
        return $"dig {escapedRecordType} {escapedDomain} +short 2>/dev/null";
    }

    internal static string CreateNslookupTunnelCommand(string recordType, string domain)
    {
        var escapedRecordType = InputValidator.EscapeShellArg(ValidateRecordType(recordType));
        var escapedDomain = InputValidator.EscapeShellArg(ValidateLookupDomain(domain));
        return $"nslookup -type={escapedRecordType} {escapedDomain} 2>&1";
    }

    internal static string ValidateRecordType(string recordType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);

        var normalized = recordType.Trim().ToUpperInvariant();
        if (!AllowedRecordTypes.Contains(normalized))
        {
            throw new ArgumentException(
                $"Unsupported DNS record type '{recordType}'. Allowed values: {string.Join(", ", AllowedRecordTypes.Order(StringComparer.Ordinal))}.",
                nameof(recordType));
        }

        return normalized;
    }

    internal static string ValidateLookupDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var normalized = domain.Trim();
        if (normalized.IndexOf('\0') >= 0 || !IsValidLookupDomain(normalized))
        {
            throw new ArgumentException($"Invalid DNS lookup domain: {domain}", nameof(domain));
        }

        return normalized;
    }

    private static bool IsValidLookupDomain(string domain)
    {
        if (InputValidator.ValidateDomain(domain))
        {
            return true;
        }

        var trimmed = domain.Trim().TrimStart('*', '.');
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 255)
        {
            return false;
        }

        if (trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Contains(".-", StringComparison.Ordinal)
            || trimmed.Contains("-.", StringComparison.Ordinal))
        {
            return false;
        }

        var labels = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
            {
                return false;
            }

            if (label.StartsWith("-", StringComparison.Ordinal)
                || label.EndsWith("-", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!char.IsLetterOrDigit(c) && c is not '-' and not '_')
                {
                    return false;
                }
            }
        }

        return true;
    }
}
