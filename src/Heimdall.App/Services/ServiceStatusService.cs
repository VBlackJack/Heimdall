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

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Heimdall.Core.Security;
using Heimdall.Core.SystemInfo;

namespace Heimdall.App.Services;

/// <summary>
/// Loads local Windows services and launches elevated PowerShell actions.
/// </summary>
public interface IServiceStatusService
{
    Task<IReadOnlyList<ServiceEntry>> LoadAsync(CancellationToken ct);
    void StartService(string serviceName);
    void StopService(string serviceName);
    void RestartService(string serviceName);
}

public sealed class ServiceStatusService : IServiceStatusService
{
    /// <summary>Script that lists the services as CSV, which the parser expects.</summary>
    private const string ServiceListScript =
        "Get-Service | Select-Object Name,DisplayName,Status,StartType | ConvertTo-Csv -NoTypeInformation";

    /// <summary>Elevation verb the service actions need.</summary>
    private const string ElevationVerb = "runas";

    private readonly Func<CancellationToken, Task<string>> _loadCsvAsync;
    private readonly Func<ProcessStartInfo, Process?> _launchProcess;

    public ServiceStatusService()
        : this(DefaultLoadCsvAsync, Process.Start)
    {
    }

    internal ServiceStatusService(
        Func<CancellationToken, Task<string>> loadCsvAsync,
        Func<ProcessStartInfo, Process?> launchProcess)
    {
        ArgumentNullException.ThrowIfNull(loadCsvAsync);
        ArgumentNullException.ThrowIfNull(launchProcess);
        _loadCsvAsync = loadCsvAsync;
        _launchProcess = launchProcess;
    }

    public async Task<IReadOnlyList<ServiceEntry>> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var csv = await _loadCsvAsync(ct).ConfigureAwait(false) ?? string.Empty;
        return PowershellServiceCsvParser.Parse(csv);
    }

    public void StartService(string serviceName) => ExecuteServiceAction("Start-Service", serviceName);

    public void StopService(string serviceName) => ExecuteServiceAction("Stop-Service", serviceName);

    public void RestartService(string serviceName) => ExecuteServiceAction("Restart-Service", serviceName);

    private void ExecuteServiceAction(string command, string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var safeName = serviceName.Trim().Replace("'", "''", StringComparison.Ordinal);
        var script = $"{command} '{safeName}'";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        try
        {
            var process = _launchProcess(CreateElevatedServiceActionStartInfo(encoded));

            process?.Dispose();
        }
        catch (Win32Exception)
        {
            // Preserve current behavior: UAC declined or unavailable.
        }
    }

    /// <summary>
    /// Builds the start info for the elevated service action. The command itself is already
    /// base64-encoded by the caller, so no quoting survives into the child.
    /// </summary>
    internal static ProcessStartInfo CreateElevatedServiceActionStartInfo(string encodedCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedCommand);

        return new ProcessStartInfo
        {
            FileName = SystemExecutablePath.WindowsPowerShell,
            Arguments = $"-NoProfile -EncodedCommand {encodedCommand}",
            Verb = ElevationVerb,
            UseShellExecute = true,
            WorkingDirectory = SystemExecutablePath.SystemDirectory,
        };
    }

    /// <summary>
    /// Builds the start info for the service listing.
    /// </summary>
    internal static ProcessStartInfo CreateServiceListStartInfo()
    {
        return new ProcessStartInfo
        {
            FileName = SystemExecutablePath.WindowsPowerShell,
            Arguments = $"-NoProfile -Command \"{ServiceListScript}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = SystemExecutablePath.SystemDirectory,
        };
    }

    private static async Task<string> DefaultLoadCsvAsync(CancellationToken ct)
    {
        using var proc = Process.Start(CreateServiceListStartInfo())
            ?? throw new InvalidOperationException("Failed to start PowerShell.");

        var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return output;
    }
}
