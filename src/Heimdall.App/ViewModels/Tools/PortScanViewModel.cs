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
/// ViewModel for the port scanner tool.
/// </summary>
public sealed partial class PortScanViewModel : ObservableObject, IDisposable
{
    private readonly IPortScanService _service;
    private readonly List<PortProbeResult> _allResults = [];
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private LocalizationManager? _localizer;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _showError;
    [ObservableProperty] private int _completed;
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private string _progressCountText = string.Empty;
    [ObservableProperty] private int _openCount;
    [ObservableProperty] private int _closedCount;

    public PortScanViewModel(IPortScanService? service = null)
    {
        _service = service ?? new PortScanService();
    }

    public void Initialize(LocalizationManager? localizer)
    {
        _localizer = localizer;
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        CurrentGateway = gateway;
        _service.SetGateway(gateway);
    }

    /// <summary>
    /// The gateway the next scan dials, null for direct. Observable so a test can tell which DTO
    /// the view handed over without reaching into the service.
    /// </summary>
    internal SshGatewayDto? CurrentGateway { get; private set; }

    public (List<int>? Ports, string? ErrorKey) ParseAndValidatePorts(string portsText)
    {
        var ports = BannerGrabEngine.ParsePorts(portsText);
        return ports.Count == 0 ? (null, "ToolValidationPortRangeRequired") : (ports, null);
    }

    public async Task ScanAsync(string host, IReadOnlyList<int> ports)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            ClearResults();
            SetError(Lk("ToolValidationHostRequired"));
            return;
        }

        if (!InputValidator.Validate(host, "Address"))
        {
            ClearResults();
            SetError(Lk("ToolValidationInvalidHost"));
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        lock (_lock)
        {
            _allResults.Clear();
        }

        IsScanning = true;
        ShowError = false;
        ErrorText = string.Empty;
        Completed = 0;
        Total = ports.Count;
        OpenCount = 0;
        ClosedCount = 0;
        ProgressPercent = 0;
        ProgressCountText = string.Format(Lk("ToolPortScanProgressCount"), 0, ports.Count);

        try
        {
            var progress = new Progress<PortScanProgress>(update =>
            {
                Completed = update.Completed;
                Total = update.Total;
                ProgressPercent = update.Total > 0 ? (int)(update.Completed * 100.0 / update.Total) : 0;
                ProgressCountText = string.Format(Lk("ToolPortScanProgressCount"), update.Completed, update.Total);

                if (update.LatestResult is { } probe)
                {
                    lock (_lock)
                    {
                        if (!_allResults.Contains(probe))
                        {
                            _allResults.Add(probe);
                        }
                    }
                }

                OpenCount = GetAllResults().Count(result => result.IsOpen);
                ClosedCount = GetAllResults().Count(result => !result.IsOpen);
            });

            var results = await _service.ScanAsync(host, ports, ((IProgress<PortScanProgress>)progress).Report, _cts.Token);

            lock (_lock)
            {
                _allResults.Clear();
                _allResults.AddRange(results);
            }

            Completed = _allResults.Count;
            Total = ports.Count;
            ProgressPercent = Total > 0 ? (int)(Completed * 100.0 / Total) : 0;
            ProgressCountText = string.Format(Lk("ToolPortScanProgressCount"), Completed, Total);
            OpenCount = _allResults.Count(result => result.IsOpen);
            ClosedCount = _allResults.Count(result => !result.IsOpen);
        }
        catch (OperationCanceledException)
        {
            // User cancelled the scan.
        }
        catch (Exception ex)
        {
            SetError(string.Format(Lk("ToolTunnelFailed"), ex.Message));
        }
        finally
        {
            IsScanning = false;
        }
    }

    public void CancelScan()
    {
        _cts?.Cancel();
    }

    public IReadOnlyList<PortProbeResult> GetAllResults()
    {
        lock (_lock)
        {
            return [.. _allResults.OrderBy(result => result.Port)];
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void ClearResults()
    {
        lock (_lock)
        {
            _allResults.Clear();
        }

        Completed = 0;
        Total = 0;
        OpenCount = 0;
        ClosedCount = 0;
        ProgressPercent = 0;
        ProgressCountText = string.Empty;
    }

    private void SetError(string message)
    {
        ShowError = true;
        ErrorText = message;
    }

    private string Lk(string key) => _localizer?[key] ?? key;
}
