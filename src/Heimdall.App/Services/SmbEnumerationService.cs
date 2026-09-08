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

using Heimdall.Core.Configuration;
using Heimdall.Core.Discovery;
using Heimdall.Core.Security;
using Renci.SshNet;

namespace Heimdall.App.Services;

/// <summary>
/// Contract for SMB enumeration, direct or through an SSH gateway.
/// </summary>
public interface ISmbEnumerationService
{
    /// <summary>
    /// Sets the SSH gateway used for the next enumeration. Null means direct mode.
    /// </summary>
    void SetGateway(SshGatewayDto? gateway);

    /// <summary>
    /// Executes the enumeration and returns a locale-neutral outcome.
    /// </summary>
    Task<SmbEnumOutcome> EnumerateAsync(SmbEnumInputs inputs, CancellationToken ct);
}

/// <summary>
/// Stateless SMB enumeration service. Direct mode delegates to NTLM/NetBIOS probes;
/// tunnel mode executes smbclient/rpcclient/nmblookup over SSH.
/// </summary>
public sealed class SmbEnumerationService : ISmbEnumerationService
{
    private SshGatewayDto? _gateway;

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    public async Task<SmbEnumOutcome> EnumerateAsync(SmbEnumInputs inputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        return _gateway is null
            ? await EnumerateDirectAsync(inputs, ct).ConfigureAwait(false)
            : await EnumerateViaTunnelAsync(inputs, _gateway, ct).ConfigureAwait(false);
    }

    private static async Task<SmbEnumOutcome> EnumerateDirectAsync(SmbEnumInputs inputs, CancellationToken ct)
    {
        var ntlmTask = NtlmProbe.ProbeWithSmbInfoAsync(inputs.Host, inputs.NtlmTimeoutMs, ct);
        var netBiosTask = UdpProbeEngine.QueryNetBiosAsync(inputs.Host, inputs.NetBiosTimeoutMs, ct);

        NtlmInfo? ntlm = null;
        SmbNegotiateInfo? smb = null;
        string? ntlmError = null;
        string? nbName = null;
        string? nbDomain = null;
        string? nbMac = null;
        var netBiosFailed = false;

        try
        {
            (ntlm, smb) = await ntlmTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ntlmError = ex.Message;
        }

        try
        {
            (nbName, nbDomain, nbMac) = await netBiosTask.ConfigureAwait(false);
            netBiosFailed = nbName is null && nbDomain is null && nbMac is null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            netBiosFailed = true;
        }

        if (ntlm is null && smb is null && ntlmError is not null)
        {
            return new SmbEnumOutcome(null, "ToolSmbErrorNtlm", ntlmError);
        }

        if (ntlm is null && smb is null && nbName is null && nbDomain is null && nbMac is null)
        {
            return new SmbEnumOutcome(null, "ToolSmbErrorConnection", "port 445 closed or filtered");
        }

        var result = SmbEnumerationEngine.BuildResult(ntlm, smb, nbName, nbDomain, nbMac, netBiosFailed);
        return new SmbEnumOutcome(result, null, null);
    }

    private static async Task<SmbEnumOutcome> EnumerateViaTunnelAsync(
        SmbEnumInputs inputs,
        SshGatewayDto gateway,
        CancellationToken ct)
    {
        SshClient? client = null;

        try
        {
            client = await ToolGatewayConnector.ConnectAsync(gateway, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new SmbEnumOutcome(null, "ToolTunnelFailed", ex.Message);
        }

        try
        {
            var host = InputValidator.EscapeShellArg(inputs.Host);

            var smbclientOutput = await ExecuteTunnelCommandAsync(
                client,
                $"smbclient -N -L //{host} 2>&1 | head -30",
                inputs.TunnelCommandTimeoutSeconds,
                ct).ConfigureAwait(false);

            var rpcclientOutput = await ExecuteTunnelCommandAsync(
                client,
                $"rpcclient -U \"\" -N {host} -c \"srvinfo\" 2>&1 | head -10",
                inputs.TunnelCommandTimeoutSeconds,
                ct).ConfigureAwait(false);

            var nmblookupOutput = await ExecuteTunnelCommandAsync(
                client,
                $"nmblookup -A {host} 2>&1 | head -20",
                inputs.TunnelCommandTimeoutSeconds,
                ct).ConfigureAwait(false);

            var observations = SmbEnumerationEngine.ParseTunnelOutputs(
                smbclientOutput,
                rpcclientOutput,
                nmblookupOutput);

            if (!observations.HasAnyData)
            {
                return new SmbEnumOutcome(null, "ToolSmbErrorConnection", "port 445 closed or filtered");
            }

            var result = SmbEnumerationEngine.BuildTunnelResult(observations);
            return new SmbEnumOutcome(result, null, null);
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    client.Disconnect();
                }
                catch
                {
                    // Best effort.
                }

                client.Dispose();
            }
        }
    }

    private static Task<string?> ExecuteTunnelCommandAsync(
        SshClient client,
        string commandText,
        int timeoutSeconds,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var command = client.CreateCommand(commandText);
            command.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            command.Execute();

            var stdout = command.Result?.Trim();
            var stderr = command.Error?.Trim();
            return string.IsNullOrWhiteSpace(stderr)
                ? stdout
                : string.IsNullOrWhiteSpace(stdout)
                    ? stderr
                    : $"{stdout}{Environment.NewLine}{stderr}";
        }, ct);
    }
}

/// <summary>
/// Immutable inputs for a single SMB enumeration run.
/// </summary>
public sealed record SmbEnumInputs(
    string Host,
    int NtlmTimeoutMs = 5000,
    int NetBiosTimeoutMs = 3000,
    int TunnelCommandTimeoutSeconds = 10);

/// <summary>
/// Locale-neutral result of an enumeration attempt.
/// </summary>
public sealed record SmbEnumOutcome(
    SmbEnumerationResult? Result,
    string? ErrorKey,
    string? ErrorArg);
