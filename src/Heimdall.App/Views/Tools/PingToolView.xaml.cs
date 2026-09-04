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
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Network;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Continuous ping monitor with live latency graph and running statistics.
/// </summary>
public partial class PingToolView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private bool _disposed;
    private Action<bool>? _setBusy;
    private GatewayRouteSelector? _routeSelector;
    private readonly PingToolViewModel _vm;

    public PingToolView()
    {
        _vm = new PingToolViewModel();
        InitializeComponent();
        _vm.PropertyChanged += OnVmPropertyChanged;

        TxtTimeout.Text = PingStatsEngine.DefaultPingTimeoutMs.ToString();
        TxtCount.Text = PingStatsEngine.DefaultPingCount.ToString();
        TxtHost.KeyDown += OnHostKeyDown;
        ResetStats();
    }

    /// <summary>
    /// Initializes the tool with optional context and localizer.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _setBusy = context?.SetBusyAction;
        _vm.Initialize(localizer);
        ApplyLocalization();

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            TxtHost.Text = context.TargetHost.Trim();
        }
        else
        {
            TxtHost.Text = string.Empty;
        }

        _routeSelector?.Dispose();
        _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportRouteStatus);
        _vm.SetGateway(null);

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtHost.Focus();
            if (!string.IsNullOrEmpty(TxtHost.Text))
            {
                TxtHost.SelectAll();
            }
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolPingTitle");
        BtnToggle.Content = L("ToolPingBtnStart");
        LblMin.Text = L("ToolPingMin");
        LblAvg.Text = L("ToolPingAvg");
        LblMax.Text = L("ToolPingMax");
        LblLoss.Text = L("ToolPingLoss");
        TxtPingCount.Text = string.Empty;

        LblJitter.Text = L("ToolPingJitter");
        BtnClear.Content = L("ToolPingBtnClear");
        BtnCopyLog.Content = L("ToolPingBtnCopyLog");
        BtnExportCsv.Content = L("ToolPingBtnExportCsv");
        LblInterval.Text = L("ToolPingIntervalLabel");
        Ping500ms.Content = L("PingInterval500ms");
        Ping1s.Content = L("PingInterval1s");
        Ping2s.Content = L("PingInterval2s");
        Ping5s.Content = L("PingInterval5s");
        Ping10s.Content = L("PingInterval10s");
        LblTimeout.Text = L("ToolPingTimeoutLabel");
        LblCount.Text = L("ToolPingCountLabel");

        System.Windows.Automation.AutomationProperties.SetName(BtnToggle, L("ToolPingBtnStart"));
        System.Windows.Automation.AutomationProperties.SetName(BtnClear, L("ToolPingBtnClear"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopyLog, L("ToolPingBtnCopyLog"));
        System.Windows.Automation.AutomationProperties.SetName(BtnExportCsv, L("ToolPingBtnExportCsv"));
        BtnCopyLog.ToolTip = L("ToolBtnCopyToClipboard");
        System.Windows.Automation.AutomationProperties.SetName(TxtHost, L("ToolPingHostLabel"));
        System.Windows.Automation.AutomationProperties.SetName(CmbInterval, L("ToolPingIntervalLabel"));
        System.Windows.Automation.AutomationProperties.SetName(TxtTimeout, L("ToolPingTimeoutLabel"));
        System.Windows.Automation.AutomationProperties.SetName(TxtCount, L("ToolPingCountLabel"));

        TxtYMin.Text = string.Format(L("ToolPingUnitMs"), 0);

        LblHost.Text = L("ToolPingHostLabel");
        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        LblRouteVia.Text = L("ToolTunnelRouteVia");
        System.Windows.Automation.AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));

        TxtHost.Tag = L("ToolWatermarkHostnameOrIp");
        TxtEmptyState.Text = L("ToolPingEmptyState");
    }

    private void OnGatewaySelected(SshGatewayDto? gateway)
    {
        _vm.SetGateway(gateway);
    }

    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TogglePing();
            e.Handled = true;
        }
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        TogglePing();
    }

    private void TogglePing()
    {
        if (_vm.IsRunning)
        {
            StopPing();
        }
        else
        {
            _ = StartPingAsync();
        }
    }

    private async Task StartPingAsync()
    {
        var (inputs, errorKey) = _vm.ValidateInputs(TxtHost.Text, TxtTimeout.Text, TxtCount.Text);
        if (errorKey is not null)
        {
            TxtLog.Inlines.Clear();
            AppendLogLine(L(errorKey), true);
            return;
        }

        TxtLog.Text = string.Empty;
        TxtLog.Inlines.Clear();
        ResetStats();
        TxtPingCount.Text = string.Empty;
        RedrawGraph();

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        MainAreaPanel.Visibility = Visibility.Visible;

        _setBusy?.Invoke(true);
        BtnToggle.Content = L("ToolPingBtnStop");
        BtnToggle.Foreground = (Brush)FindResource("ErrorBrush");
        BtnToggle.Style = (Style)FindResource("SecondaryButtonStyle");
        System.Windows.Automation.AutomationProperties.SetName(BtnToggle, L("ToolPingBtnStop"));
        TxtHost.IsReadOnly = true;
        CmbInterval.IsEnabled = false;
        CmbRouteVia.IsEnabled = false;
        TxtTimeout.IsReadOnly = true;
        TxtCount.IsReadOnly = true;

        try
        {
            await _vm.StartAsync(inputs!, GetSelectedIntervalMs());
        }
        finally
        {
            ReleaseStartingUiState();
        }
    }

    private void ReleaseStartingUiState()
    {
        _setBusy?.Invoke(false);
        BtnToggle.Content = L("ToolPingBtnStart");
        BtnToggle.Foreground = (Brush)FindResource("TextPrimaryBrush");
        BtnToggle.Style = (Style)FindResource("PrimaryButtonStyle");
        System.Windows.Automation.AutomationProperties.SetName(BtnToggle, L("ToolPingBtnStart"));
        TxtHost.IsReadOnly = false;
        CmbInterval.IsEnabled = true;
        CmbRouteVia.IsEnabled = true;
        TxtTimeout.IsReadOnly = false;
        TxtCount.IsReadOnly = false;

        if (_vm.GetHistory().Count == 0 && TxtLog.Inlines.Count == 0)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            MainAreaPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void StopPing()
    {
        _vm.Stop();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _vm.Reset();
        TxtLog.Text = string.Empty;
        TxtLog.Inlines.Clear();
        TxtPingCount.Text = string.Empty;
        ResetStats();
        RedrawGraph();
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        var logText = new StringBuilder();
        foreach (var inline in TxtLog.Inlines)
        {
            if (inline is Run run)
            {
                logText.Append(run.Text);
            }
        }

        var text = logText.ToString().Trim();
        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                Clipboard.SetText(text);
                CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"PingTool clipboard copy failed: {ex.Message}");
                AppendLogLine(string.Format(L("StatusClipboardCopyFailed"), ex.Message), true);
            }
        }
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        var history = _vm.GetHistory();
        if (history.Count == 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("FileDialogCsvFilter"),
            FileName = $"ping_{TxtHost.Text.Trim()}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var csv = PingStatsEngine.BuildCsvExport(history, CreateLocalize());
            File.WriteAllText(dialog.FileName, csv, Encoding.UTF8);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"PingTool CSV export failed: {ex.Message}");
            AppendLogLine(string.Format(L("StatusExportFailed"), ex.Message), true);
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpPING").Replace("\\n", "\n", StringComparison.Ordinal);
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnGraphCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawGraph();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PingToolViewModel.Stats):
                RenderStats(_vm.Stats);
                break;

            case nameof(PingToolViewModel.SentCount):
                TxtPingCount.Text = string.Format(L("ToolPingCountFormat"), _vm.SentCount);
                break;

            case nameof(PingToolViewModel.LatestResult):
                if (_vm.LatestResult is { } result)
                {
                    AppendLogForResult(result);
                    RedrawGraph();
                }
                break;

            case nameof(PingToolViewModel.ShowError) when _vm.ShowError:
                AppendLogLine(_vm.ErrorText, true);
                break;

            case nameof(PingToolViewModel.SessionCompleted) when _vm.SessionCompleted:
                var stats = _vm.Stats;
                AppendLogLine(
                    string.Format(L("ToolPingCompleteFormat"), stats.Sent, stats.Received, $"{stats.LossPercent:F1}"),
                    false);
                break;
        }
    }

    private void RenderStats(PingStatsSnapshot stats)
    {
        if (stats.Received > 0)
        {
            TxtMin.Text = string.Format(L("ToolPingUnitMs"), stats.Min);
            TxtAvg.Text = string.Format(L("ToolPingUnitMs"), stats.Avg);
            TxtMax.Text = string.Format(L("ToolPingUnitMs"), stats.Max);
            TxtJitter.Text = string.Format(L("ToolPingUnitMs"), $"{stats.Jitter:F1}");
        }
        else
        {
            TxtMin.Text = "\u2014";
            TxtAvg.Text = "\u2014";
            TxtMax.Text = "\u2014";
            TxtJitter.Text = "\u2014";
        }

        TxtLoss.Text = string.Format(L("ToolPingStatsPercent"), $"{stats.LossPercent:F1}");
    }

    private void AppendLogForResult(PingProbeResult result)
    {
        switch (result.Status)
        {
            case PingStatus.Success:
                AppendLogLine(
                    string.Format(L("ToolPingReplyFormat"), result.Timestamp, result.Seq, result.Address, result.Latency, result.Ttl),
                    false);
                break;

            case PingStatus.Timeout:
                AppendLogLine(
                    string.Format(L("ToolPingTimeoutFormat"), result.Timestamp, result.Seq, result.StatusDetail),
                    true);
                break;

            case PingStatus.Error:
                AppendLogLine(
                    string.Format(L("ToolPingErrorFormat"), result.Timestamp, result.Seq, result.StatusDetail),
                    true);
                break;
        }
    }

    private void AppendLogLine(string text, bool isError)
    {
        var run = new Run(text + Environment.NewLine)
        {
            Foreground = isError
                ? (Brush)FindResource("ErrorBrush")
                : (Brush)FindResource("TextPrimaryBrush"),
        };

        TxtLog.Inlines.Add(run);
        LogScrollViewer.ScrollToEnd();
    }

    private void RedrawGraph()
    {
        GraphCanvas.Children.Clear();

        var dataPoints = _vm.GetDataPoints();
        if (dataPoints.Count < 2)
        {
            return;
        }

        var width = GraphCanvas.ActualWidth;
        var height = GraphCanvas.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var scale = PingStatsEngine.ComputeGraphYScale(dataPoints);
        var maxY = scale.MaxY;

        var msFormat = L("ToolPingUnitMs");
        TxtYMax.Text = string.Format(msFormat, maxY);
        TxtYMid.Text = string.Format(msFormat, scale.MidY);

        const double marginLeft = 50;
        const double marginRight = 10;
        const double marginTop = 10;
        const double marginBottom = 10;

        var graphWidth = width - marginLeft - marginRight;
        var graphHeight = height - marginTop - marginBottom;

        for (var index = 0; index <= 4; index++)
        {
            var y = marginTop + (graphHeight * index / 4.0);
            var gridLine = new Line
            {
                X1 = marginLeft,
                Y1 = y,
                X2 = width - marginRight,
                Y2 = y,
                Stroke = (Brush)FindResource("BorderBrush"),
                StrokeThickness = 0.5,
                Opacity = 0.5,
            };
            GraphCanvas.Children.Add(gridLine);
        }

        var accentBrush = (Brush)FindResource("AccentBrush");
        var errorBrush = (Brush)FindResource("ErrorBrush");
        var polyline = new Polyline
        {
            Stroke = accentBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };

        var stepX = dataPoints.Count > 1 ? graphWidth / (dataPoints.Count - 1) : 0;

        for (var index = 0; index < dataPoints.Count; index++)
        {
            var point = dataPoints[index];
            var x = marginLeft + (index * stepX);

            if (point.IsTimeout)
            {
                var marker = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = errorBrush,
                };
                Canvas.SetLeft(marker, x - 3);
                Canvas.SetTop(marker, marginTop + graphHeight - 3);
                GraphCanvas.Children.Add(marker);
            }
            else
            {
                var y = marginTop + graphHeight - (point.Latency * graphHeight / maxY);
                polyline.Points.Add(new System.Windows.Point(x, y));
            }
        }

        if (polyline.Points.Count > 1)
        {
            GraphCanvas.Children.Add(polyline);
        }
    }

    private int GetSelectedIntervalMs()
    {
        if (CmbInterval.SelectedItem is ComboBoxItem item &&
            item.Tag is string tagString &&
            int.TryParse(tagString, out var milliseconds))
        {
            return milliseconds;
        }

        return 1000;
    }

    private void ResetStats()
    {
        TxtMin.Text = "\u2014";
        TxtAvg.Text = "\u2014";
        TxtMax.Text = "\u2014";
        TxtJitter.Text = "\u2014";
        TxtLoss.Text = "\u2014";
    }

    private Func<string, string> CreateLocalize() => key => _localizer?[key] ?? key;

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsRunning;

    private void ReportRouteStatus(string message)
    {
        // Through the ViewModel, which is what the view's own refresh reads back.
        _vm.ShowError = false;
        _vm.ErrorText = message;
        _vm.ShowError = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _routeSelector?.Dispose();
        TxtHost.KeyDown -= OnHostKeyDown;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
}
