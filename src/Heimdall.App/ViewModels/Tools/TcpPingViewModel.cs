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
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// ViewModel for the TCP ping tool. Owns validation, probe loop orchestration,
/// localized summary/status reprojection, and help state.
/// </summary>
public sealed partial class TcpPingViewModel : ObservableObject, IDisposable
{
    public const int ConnectTimeoutMs = 5000;
    public const int DefaultPort = 443;
    public const int DefaultCount = 10;
    public const int DelayBetweenPingsMs = 1000;
    private const int MinCount = 1;
    private const int MaxCount = 10000;

    private readonly ITcpPingService _service;
    private LocalizationManager? _localizer;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private string? _lastErrorKey;
    private object?[]? _lastErrorArgs;
    private readonly List<TcpPingProbeResult> _lastRunProbes = [];
    private bool _lastRunHadOutcome;
    private DateTime _lastCompletedAt;

    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _port = DefaultPort.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _count = DefaultCount.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showError;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _results = string.Empty;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _isHelpVisible;
    [ObservableProperty] private string _helpText = string.Empty;
    [ObservableProperty] private string _hostWatermark = string.Empty;

    public TcpPingViewModel(ITcpPingService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public void UpdateLocalizer(LocalizationManager? localizer)
    {
        if (!ReferenceEquals(_localizer, localizer))
        {
            if (_localizer is not null)
            {
                _localizer.LocaleChanged -= OnLocaleChanged;
            }

            _localizer = localizer;
            if (_localizer is not null)
            {
                _localizer.LocaleChanged += OnLocaleChanged;
            }
        }

        RefreshLocalizedMessages();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        ResetTransientState();

        var host = Host?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            SetError("ToolValidationHostRequired");
            return;
        }

        if (!InputValidator.Validate(host, "Address"))
        {
            SetError("ToolValidationInvalidHost");
            return;
        }

        if (!int.TryParse(Port, CultureInfo.InvariantCulture, out var port) ||
            !InputValidator.ValidatePortRange(port))
        {
            SetError("ToolValidationPortRangeRequired");
            return;
        }

        if (!int.TryParse(Count, CultureInfo.InvariantCulture, out var count) ||
            count < MinCount ||
            count > MaxCount)
        {
            SetError("ToolTcpPingErrorCount");
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        var builder = new StringBuilder();
        var probes = new List<TcpPingProbeResult>(count);

        try
        {
            for (var seq = 1; seq <= count; seq++)
            {
                TcpPingProbeResult probe;
                try
                {
                    var request = new TcpPingProbeRequest(host, port, seq, ConnectTimeoutMs);
                    probe = await _service.ProbeAsync(request, ct).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }

                probes.Add(probe);
                builder.AppendLine(FormatProbeLine(probe, count));
                Results = builder.ToString();
                HasResults = true;

                if (seq < count)
                {
                    try
                    {
                        await Task.Delay(DelayBetweenPingsMs, ct).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            _lastRunProbes.Clear();
            _lastRunProbes.AddRange(probes);
            _lastRunHadOutcome = true;
            _lastCompletedAt = DateTime.Now;
            SummaryText = BuildSummaryText(_lastRunProbes);
            StatusText = BuildStatusText(_lastRunProbes);
            HasResults = !string.IsNullOrWhiteSpace(Results);
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _cts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void CopyResults()
    {
        // Clipboard access stays in the view code-behind. This command exists
        // only to expose CanExecute for the button.
    }

    [RelayCommand]
    private void ToggleHelp()
    {
        IsHelpVisible = !IsHelpVisible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localizer = null;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        GC.SuppressFinalize(this);
    }

    private bool CanStart() => !IsBusy;

    private bool CanStop() => IsBusy;

    private bool CanCopy() => HasResults && !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CopyResultsCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasResultsChanged(bool value)
    {
        CopyResultsCommand.NotifyCanExecuteChanged();
    }

    private void OnLocaleChanged(string _)
    {
        RefreshLocalizedMessages();
    }

    private void RefreshLocalizedMessages()
    {
        HostWatermark = L("ToolWatermarkHostnameOrIp");
        HelpText = L("ToolHelpTCPPING").Replace("\\n", "\n", StringComparison.Ordinal);

        if (ShowError && !string.IsNullOrEmpty(_lastErrorKey))
        {
            ErrorText = FormatError(_lastErrorKey, _lastErrorArgs);
        }

        if (_lastRunHadOutcome)
        {
            SummaryText = BuildSummaryText(_lastRunProbes);
            StatusText = BuildStatusText(_lastRunProbes);
        }
    }

    private void ResetTransientState()
    {
        ShowError = false;
        ErrorText = string.Empty;
        StatusText = string.Empty;
        SummaryText = string.Empty;
        Results = string.Empty;
        HasResults = false;
        _lastErrorKey = null;
        _lastErrorArgs = null;
        _lastRunProbes.Clear();
        _lastRunHadOutcome = false;
        _lastCompletedAt = default;
    }

    private void SetError(string errorKey, params object?[] args)
    {
        _lastErrorKey = errorKey;
        _lastErrorArgs = args;
        ShowError = true;
        ErrorText = FormatError(errorKey, args);
    }

    private string BuildSummaryText(IReadOnlyList<TcpPingProbeResult> probes)
    {
        var summary = TcpPingSummary.FromProbes(probes);
        if (summary is null)
        {
            return string.Empty;
        }

        if (summary.Lost == summary.Total)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                L("ToolTcpPingSummary"),
                "-",
                "-",
                "-",
                summary.Lost,
                summary.Total);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            L("ToolTcpPingSummary"),
            summary.MinMs.ToString("F0", CultureInfo.InvariantCulture),
            summary.AvgMs.ToString("F0", CultureInfo.InvariantCulture),
            summary.MaxMs.ToString("F0", CultureInfo.InvariantCulture),
            summary.Lost,
            summary.Total);
    }

    private string BuildStatusText(IReadOnlyList<TcpPingProbeResult> probes)
    {
        if (!_lastRunHadOutcome)
        {
            return string.Empty;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            L("ToolTcpPingStatus"),
            probes.Count,
            _lastCompletedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static string FormatProbeLine(TcpPingProbeResult probe, int totalCount)
    {
        if (probe.Status == TcpPingProbeStatus.Success)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "[{0}/{1}] {2}:{3} - {4:F1} ms",
                probe.Seq,
                totalCount,
                probe.Host,
                probe.Port,
                probe.LatencyMs);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "[{0}/{1}] {2}:{3} - FAILED: {4}",
            probe.Seq,
            totalCount,
            probe.Host,
            probe.Port,
            probe.ErrorMessage ?? string.Empty);
    }

    private string FormatError(string key, object?[]? args)
    {
        var template = L(key);
        return args is null || args.Length == 0 ? template : string.Format(template, args);
    }

    private string L(string key) => _localizer?[key] ?? key;
}
