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
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Contract for scanning visible Wi-Fi networks.
/// </summary>
public interface IWifiScanService
{
    Task<IReadOnlyList<WifiEntry>> ScanAsync();
}

/// <summary>
/// Stateless wrapper over <c>netsh wlan show networks mode=bssid</c>.
/// </summary>
public sealed class WifiScanService : IWifiScanService
{
    public const int ProcessTimeoutMs = 10000;

    /// <summary>Executable that reports the visible wireless networks.</summary>
    internal const string NetshExecutableName = "netsh.exe";

    /// <summary>List the networks with per-BSSID detail, which the parser expects.</summary>
    private const string NetshWifiScanArguments = "wlan show networks mode=bssid";

    private readonly Func<Task<string>> _runNetshAsync;

    public WifiScanService()
        : this(DefaultRunNetshAsync)
    {
    }

    internal WifiScanService(Func<Task<string>> runNetshAsync)
    {
        ArgumentNullException.ThrowIfNull(runNetshAsync);
        _runNetshAsync = runNetshAsync;
    }

    public async Task<IReadOnlyList<WifiEntry>> ScanAsync()
    {
        var output = await _runNetshAsync().ConfigureAwait(false) ?? string.Empty;
        return NetshWifiParser.Parse(output)
            .OrderByDescending(entry => entry.SignalValue)
            .ToList();
    }

    /// <summary>
    /// Builds the start info for the wireless-network scan.
    /// </summary>
    internal static ProcessStartInfo CreateNetshWifiScanStartInfo()
    {
        return new ProcessStartInfo
        {
            FileName = SystemExecutablePath.InSystemDirectory(NetshExecutableName),
            Arguments = NetshWifiScanArguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = SystemExecutablePath.SystemDirectory,
        };
    }

    private static Task<string> DefaultRunNetshAsync()
    {
        return Task.Run(async () =>
        {
            using var proc = Process.Start(CreateNetshWifiScanStartInfo())
                ?? throw new InvalidOperationException("Failed to start netsh process.");

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(ProcessTimeoutMs))
            {
                try
                {
                    proc.Kill();
                }
                catch
                {
                    // Preserve current best-effort kill behavior.
                }
            }

            return await outputTask.ConfigureAwait(false);
        });
    }
}
