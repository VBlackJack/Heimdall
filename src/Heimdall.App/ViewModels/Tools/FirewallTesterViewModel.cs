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

using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// ViewModel for the firewall tester tool.
/// </summary>
public sealed partial class FirewallTesterViewModel : ObservableObject, IDisposable
{
    private readonly IFirewallTesterService _service;
    private readonly List<FwProbeResult> _allResults = [];
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private LocalizationManager? _localizer;
    private List<string> _lastHosts = [];
    private List<int> _lastPorts = [];

    /// <summary>
    /// Number of the test run in progress, and of the last run that reached its final state.
    /// Progress reports are posted asynchronously by <see cref="Progress{T}"/>, so one can arrive
    /// after the run has written its final counters; a report whose run is finished, or is not the
    /// current one, is dropped instead of overwriting them.
    /// </summary>
    private int _run;
    private int _finishedRun;

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _showError;
    [ObservableProperty] private int _completed;
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private string _progressCountText = string.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private int _openCount;
    [ObservableProperty] private int _closedCount;
    [ObservableProperty] private int _timeoutCount;

    public FirewallTesterViewModel(IFirewallTesterService? service = null)
    {
        _service = service ?? new FirewallTesterService();
    }

    public void Initialize(LocalizationManager? localizer)
    {
        _localizer = localizer;
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _service.SetGateway(gateway);
    }

    public (List<string>? Hosts, List<int>? Ports, string? ErrorKey) ParseAndValidateInputs(string hostsText, string portsText)
    {
        var hosts = FirewallProbeEngine.ParseHosts(hostsText);
        if (hosts.Count == 0)
        {
            return (null, null, "ToolFwErrorNoHosts");
        }

        hosts = [.. hosts.Where(host => InputValidator.Validate(host, "Address"))];
        if (hosts.Count == 0)
        {
            return (null, null, "ErrorInvalidHost");
        }

        var ports = BannerGrabEngine.ParsePorts(portsText);
        if (ports.Count == 0)
        {
            return (null, null, "ToolFwErrorNoPorts");
        }

        hosts = [.. hosts.Take(FirewallProbeEngine.MaxHosts)];
        ports = [.. ports.Take(FirewallProbeEngine.MaxPorts)];
        return (hosts, ports, null);
    }

    public async Task TestAsync(IReadOnlyList<string> hosts, IReadOnlyList<int> ports)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        int run;
        lock (_lock)
        {
            _allResults.Clear();
            run = ++_run;
        }

        _lastHosts = [.. hosts];
        _lastPorts = [.. ports];

        IsTesting = true;
        ShowError = false;
        ErrorText = string.Empty;
        Completed = 0;
        Total = hosts.Count * ports.Count;
        OpenCount = 0;
        ClosedCount = 0;
        TimeoutCount = 0;
        ProgressPercent = 0;
        ProgressCountText = Format("ToolFwProgress", 0, Total);
        SummaryText = string.Empty;

        try
        {
            var progress = new Progress<FwProbeProgress>(update =>
            {
                lock (_lock)
                {
                    if (IsStaleReport(run))
                    {
                        return;
                    }

                    Completed = update.Completed;
                    Total = update.Total;
                    ProgressPercent = update.Total > 0 ? (int)(update.Completed * 100.0 / update.Total) : 0;
                    ProgressCountText = Format("ToolFwProgress", update.Completed, update.Total);

                    if (update.LatestResult is { } result && !_allResults.Contains(result))
                    {
                        _allResults.Add(result);
                    }

                    UpdateCountsAndSummary();
                }
            });

            var results = await _service.TestMatrixAsync(
                hosts,
                ports,
                ((IProgress<FwProbeProgress>)progress).Report,
                _cts.Token);

            lock (_lock)
            {
                MarkFinished(run);
                _allResults.Clear();
                _allResults.AddRange(results);

                Completed = _allResults.Count;
                Total = hosts.Count * ports.Count;
                ProgressPercent = Total > 0 ? (int)(Completed * 100.0 / Total) : 0;
                ProgressCountText = Format("ToolFwProgress", Completed, Total);
                UpdateCountsAndSummary();
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                MarkFinished(run);
                UpdateCountsAndSummary();
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                MarkFinished(run);
            }

            SetError(FormatError("ToolTunnelFailed", ex.Message));
        }
        finally
        {
            IsTesting = false;
        }
    }

    public void CancelTest()
    {
        _cts?.Cancel();
    }

    public IReadOnlyList<FwProbeResult> GetAllResults()
    {
        lock (_lock)
        {
            return [.. _allResults.OrderBy(result => result.Host).ThenBy(result => result.Port)];
        }
    }

    public IReadOnlyList<string> GetLastHosts() => [.. _lastHosts];

    public IReadOnlyList<int> GetLastPorts() => [.. _lastPorts];

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Call under <see cref="_lock"/>.</summary>
    private bool IsStaleReport(int run) => run != _run || _finishedRun >= run;

    /// <summary>Call under <see cref="_lock"/>.</summary>
    private void MarkFinished(int run)
    {
        if (_finishedRun < run)
        {
            _finishedRun = run;
        }
    }

    private void UpdateCountsAndSummary()
    {
        var summary = FirewallProbeEngine.ComputeSummary(GetAllResults());
        OpenCount = summary.Open;
        ClosedCount = summary.Closed;
        TimeoutCount = summary.Timeout;
        SummaryText = summary.Total > 0
            ? Format("ToolFwSummary", summary.Open, summary.Closed, summary.Timeout, summary.Total)
            : string.Empty;
    }

    private void SetError(string message)
    {
        ShowError = true;
        ErrorText = message;
    }

    private string L(string key) => _localizer?[key] ?? key;

    private string Format(string key, params object[] args)
    {
        var template = L(key);
        return template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(template, args)
            : $"{template} {string.Join(" ", args)}";
    }

    private string FormatError(string key, string message)
    {
        var template = L(key);
        return template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(template, message)
            : $"{template}: {message}";
    }
}
