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
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Result returned by <see cref="NetworkScannerService"/> after a scan run.
/// </summary>
public sealed record NetworkScanResult(
    int HostCount,
    bool AddedToInventory,
    string? StatusMessage);

/// <summary>
/// Runs the network scanner workflow without depending on WPF types.
/// Prompts for CIDR input, scans the subnet, optionally adds discovered
/// hosts to the inventory, and reports a summary back to the caller.
/// </summary>
public sealed class NetworkScannerService(
    IDialogService dialogService,
    IConfigManager configManager)
{
    private readonly IDialogService _dialogService = dialogService;
    private readonly IConfigManager _configManager = configManager;

    /// <summary>
    /// Scans a subnet for live hosts and optionally adds them to the server inventory.
    /// Progress is reported through the standard <see cref="IProgress{T}"/> pattern.
    /// </summary>
    public async Task<NetworkScanResult> ScanAndPromptAsync(
        Func<string, string> localize,
        IProgress<(int Done, int Total, string Cidr)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localize);

        var cidr = await _dialogService.ShowInputAsync(
            localize("NetworkScannerTitle"),
            localize("NetworkScannerCidrPrompt"),
            "192.168.1.0/24");

        if (string.IsNullOrWhiteSpace(cidr))
        {
            return new NetworkScanResult(0, false, null);
        }

        try
        {
            var trimmedCidr = cidr.Trim();
            var results = await NetworkScanner.ScanSubnetAsync(
                trimmedCidr,
                (done, total) => progress?.Report((done, total, trimmedCidr)),
                ct);

            if (results.Count == 0)
            {
                return new NetworkScanResult(
                    0,
                    false,
                    string.Format(CultureInfo.CurrentCulture, localize("NetworkScannerNoHosts"), trimmedCidr));
            }

            var summary = string.Join("\n", results.Select(result =>
            {
                var ports = result.OpenPorts.Count > 0 ? string.Join(", ", result.OpenPorts) : "-";
                var host = result.Hostname ?? result.IpAddress;
                return $"{result.IpAddress}  {host}  [{ports}]  {result.RoundtripMs}ms";
            }));

            var addServers = await _dialogService.ShowConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, localize("NetworkScannerComplete"), results.Count),
                summary + "\n\n" + localize("NetworkScannerAddServer"),
                "info");

            if (addServers)
            {
                await _configManager.MutateServersAsync(existingServers =>
                {
                    foreach (var result in results)
                    {
                        var connectionType = result.OpenPorts.Contains(DefaultPorts.Rdp) ? "RDP"
                            : result.OpenPorts.Contains(DefaultPorts.Ssh) ? "SSH"
                            : result.OpenPorts.Contains(DefaultPorts.Vnc) ? "VNC"
                            : "SSH";
                        var port = connectionType switch
                        {
                            "RDP" => DefaultPorts.Rdp,
                            "VNC" => DefaultPorts.Vnc,
                            _ => DefaultPorts.Ssh
                        };

                        existingServers.Add(new ServerProfileDto
                        {
                            Id = Guid.NewGuid().ToString(),
                            DisplayName = result.Hostname ?? result.IpAddress,
                            RemoteServer = result.IpAddress,
                            RemotePort = port,
                            ConnectionType = connectionType,
                            Group = "Discovered"
                        });
                    }

                    return results.Count;
                });
            }

            return new NetworkScanResult(
                results.Count,
                addServers,
                string.Format(CultureInfo.CurrentCulture, localize("NetworkScannerComplete"), results.Count));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new NetworkScanResult(
                0,
                false,
                string.Format(CultureInfo.CurrentCulture, localize("NetworkScannerError"), ex.Message));
        }
    }
}
