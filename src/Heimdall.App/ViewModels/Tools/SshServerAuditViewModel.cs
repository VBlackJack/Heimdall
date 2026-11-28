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

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Ssh;
using Renci.SshNet;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// View-model backing the SSH Server Cryptographic Audit tool (<c>SSHSERVERAUDIT</c>).
/// Probes a target's RFC 4253 banner and <c>SSH_MSG_KEXINIT</c> algorithms directly or
/// through an SSH gateway chain, grades compliance, and generates exportable reports.
/// </summary>
public sealed partial class SshServerAuditViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, int, SshGatewayDto?, CancellationToken, Task<SshKexInitSnapshot>> _probeRunner;
    private LocalizationManager? _localizer;
    private SshGatewayDto? _gateway;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public SshServerAuditViewModel()
        : this(ProbeWithOptionalGatewayAsync)
    {
    }

    internal SshServerAuditViewModel(
        Func<string, int, SshGatewayDto?, CancellationToken, Task<SshKexInitSnapshot>> probeRunner)
    {
        _probeRunner = probeRunner ?? throw new ArgumentNullException(nameof(probeRunner));
    }

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private string _port = "22";

    [ObservableProperty]
    private bool _isAuditing;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _showError;

    [ObservableProperty]
    private bool _showEmptyState = true;

    [ObservableProperty]
    private bool _showResults;

    private const int MaxFleetAuditConcurrency = 4;

    [ObservableProperty]
    private SshServerAuditReport? _report;

    [ObservableProperty]
    private bool _showFleetResults;

    [ObservableProperty]
    private string _fleetSummaryText = string.Empty;

    [ObservableProperty]
    private SshFleetAuditDriftSummary? _lastFleetDrift;

    [ObservableProperty]
    private string _fleetDriftSummaryText = string.Empty;

    public ObservableCollection<SshFleetServerAuditEntry> FleetResults { get; } = [];

    /// <summary>
    /// Audits all saved SSH profiles concurrently (bounded to 4 simultaneous probes),
    /// automatically routing each server through its configured or inherited SSH gateway.
    /// </summary>
    public async Task RunFleetAuditAsync(
        IReadOnlyList<ServerProfileDto> servers,
        AppSettings settings,
        IReadOnlyList<SshFleetAuditBaselineEntry>? previousBaseline = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(settings);

        if (IsAuditing)
        {
            return;
        }

        var sshTargets = servers
            .Where(s => string.Equals(s.ConnectionType, "SSH", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.RemoteServer)
                && InputValidator.IsValidSshHost(s.RemoteServer.Trim()))
            .ToList();

        if (sshTargets.Count == 0)
        {
            ShowErrorMessage(L("ToolSshServerAuditFleetNoServers"));
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsAuditing = true;
        ShowError = false;
        ErrorMessage = string.Empty;
        FleetResults.Clear();
        LastFleetDrift = null;
        FleetDriftSummaryText = string.Empty;
        ShowFleetResults = true;
        ShowEmptyState = false;

        try
        {
            var gatewayLookup = settings.SshGateways
                .Where(g => !string.IsNullOrWhiteSpace(g.Id))
                .DistinctBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

            var ordered = new SshFleetServerAuditEntry?[sshTargets.Count];
            using var gate = new SemaphoreSlim(MaxFleetAuditConcurrency, MaxFleetAuditConcurrency);

            var tasks = sshTargets.Select(async (server, index) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    string host = server.RemoteServer.Trim();
                    int port = server.SshPort > 0 && InputValidator.ValidatePortRange(server.SshPort)
                        ? server.SshPort
                        : DefaultPorts.Ssh;
                    string displayName = string.IsNullOrWhiteSpace(server.DisplayName) ? host : server.DisplayName;
                    string? gatewayId = SshGatewayRoute.GatewayIdFor(server, settings.GroupDefaults);
                    SshGatewayDto? gw = gatewayId is not null && gatewayLookup.TryGetValue(gatewayId, out var resolved)
                        ? resolved
                        : null;
                    if (gatewayId is not null && gw is null)
                    {
                        string missingMsg = _localizer is not null
                            ? _localizer.Format("StatusTunnelPresetGatewayMissing", gatewayId)
                            : $"Gateway '{gatewayId}' not found.";
                        ordered[index] = new SshFleetServerAuditEntry(displayName, host, port, gatewayId, null, missingMsg);
                        return;
                    }

                    string? gwName = gw is null ? null : (string.IsNullOrWhiteSpace(gw.Name) ? gw.Host : gw.Name);

                    try
                    {
                        SshKexInitSnapshot snapshot = await _probeRunner(host, port, gw, ct).ConfigureAwait(false);
                        SshServerAuditReport report = SshServerAuditEngine.Evaluate(snapshot);
                        ordered[index] = new SshFleetServerAuditEntry(displayName, host, port, gwName, report);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ordered[index] = new SshFleetServerAuditEntry(displayName, host, port, gwName, null, ex.Message);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(true);

            foreach (var entry in ordered)
            {
                if (entry is not null)
                {
                    FleetResults.Add(entry);
                }
            }

            int audited = FleetResults.Count(e => e.Report is not null);
            int anssiPass = FleetResults.Count(e => e.Report?.IsAnssiCompliant == true);
            int pqCount = FleetResults.Count(e => e.Report?.HasPostQuantumKex == true);
            int errors = FleetResults.Count - audited;
            FleetSummaryText = _localizer is not null
                ? _localizer.FormatCount(
                    FleetResults.Count,
                    "ToolSshServerAuditFleetSummaryOne",
                    "ToolSshServerAuditFleetSummary",
                    FleetResults.Count,
                    anssiPass,
                    pqCount,
                    errors)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    L("ToolSshServerAuditFleetSummary"),
                    FleetResults.Count,
                    anssiPass,
                    pqCount,
                    errors);

            var firstReport = FleetResults.FirstOrDefault(e => e.Report is not null)?.Report;
            if (firstReport is not null)
            {
                Report = firstReport;
                ShowResults = true;
            }

            if (previousBaseline is { Count: > 0 })
            {
                var drift = SshServerAuditEngine.CompareAgainstBaseline(FleetResults, previousBaseline);
                LastFleetDrift = drift;
                FleetDriftSummaryText = string.Format(
                    CultureInfo.CurrentCulture,
                    L("ToolSshServerAuditFleetDriftSummary"),
                    drift.Improved,
                    drift.Regressed,
                    drift.Unchanged,
                    drift.NewHosts);
            }
        }
        catch (OperationCanceledException)
        {
            ShowErrorMessage(L("ToolSshServerAuditCancelled"));
        }
        finally
        {
            IsAuditing = false;
        }
    }

    public void Initialize(LocalizationManager? localizer)
    {
        _localizer = localizer;
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    [RelayCommand]
    private async Task RunAuditAsync()
    {
        if (IsAuditing)
        {
            return;
        }

        string trimmedHost = Host?.Trim() ?? string.Empty;
        if (!InputValidator.IsValidSshHost(trimmedHost))
        {
            ShowErrorMessage(L("ToolSshServerAuditErrInvalidHost"));
            return;
        }

        if (!int.TryParse(Port?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int portNum)
            || !InputValidator.ValidatePortRange(portNum))
        {
            ShowErrorMessage(L("ToolSshServerAuditErrInvalidPort"));
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsAuditing = true;
        ShowError = false;
        ErrorMessage = string.Empty;

        try
        {
            SshKexInitSnapshot snapshot = await _probeRunner(trimmedHost, portNum, _gateway, ct)
                .ConfigureAwait(true);

            Report = SshServerAuditEngine.Evaluate(snapshot);
            ShowEmptyState = false;
            ShowResults = true;
        }
        catch (OperationCanceledException)
        {
            ShowErrorMessage(L("ToolSshServerAuditCancelled"));
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshServerAudit] probe failed for {trimmedHost}:{portNum}: {ex.Message}");
            ShowErrorMessage(string.Format(
                CultureInfo.CurrentCulture,
                L("ToolSshServerAuditErrProbeFailed"),
                ex.Message));
        }
        finally
        {
            IsAuditing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    private void ShowErrorMessage(string message)
    {
        ErrorMessage = message;
        ShowError = !string.IsNullOrWhiteSpace(message);
    }

    private string L(string key) => _localizer?[key] ?? key;

    private static async Task<SshKexInitSnapshot> ProbeWithOptionalGatewayAsync(
        string host,
        int port,
        SshGatewayDto? gateway,
        CancellationToken ct)
    {
        if (gateway is null)
        {
            return await SshKexInitProbe.ProbeAsync(host, port, host, port, cancellationToken: ct)
                .ConfigureAwait(false);
        }

        using var gatewayClient = await ToolGatewayConnector.ConnectAsync(gateway, ct)
            .ConfigureAwait(false);
        using var forward = new ForwardedPortLocal(LoopbackBinding.DefaultHost, 0, host, (uint)port);
        gatewayClient.AddForwardedPort(forward);
        try
        {
            forward.Start();
            return await SshKexInitProbe.ProbeAsync(
                LoopbackBinding.DefaultHost,
                (int)forward.BoundPort,
                host,
                port,
                cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (forward.IsStarted)
                {
                    forward.Stop();
                }

                gatewayClient.RemoveForwardedPort(forward);
            }
            catch (Exception)
            {
                // Best-effort cleanup on probe close.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
