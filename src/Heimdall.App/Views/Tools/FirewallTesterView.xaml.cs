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
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Network;
using Heimdall.Ssh;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Firewall rule tester that probes TCP connectivity across a matrix of
/// destinations and ports, displaying results as a color-coded heatmap grid.
/// </summary>
public partial class FirewallTesterView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private bool _disposed;
    private GatewayRouteSelector? _routeSelector;
    private Action<bool>? _setBusy;
    private readonly ToolAsyncStateController _viewState;
    private readonly FirewallTesterViewModel _vm;

    public FirewallTesterView()
    {
        _vm = new FirewallTesterViewModel();
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        _viewState = new ToolAsyncStateController(
            null,
            null,
            TxtError,
            EmptyStatePanel,
            ResultsPanel,
            null);

        TxtPorts.KeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                OnTestClick(s, e);
            }
        };

        RefreshUiFromVm();
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
        TxtHosts.Clear();
        TxtPorts.Text = NetworkToolPresets.FirewallTesterDefaultPorts;

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            TxtHosts.Text = context.TargetHost;
        }

        _routeSelector?.Dispose();
        _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportRouteStatus);
        LblSummary.Text = string.Empty;
        HeatmapGrid.Children.Clear();
        HeatmapGrid.RowDefinitions.Clear();
        HeatmapGrid.ColumnDefinitions.Clear();
        _viewState.Reset();
        RefreshUiFromVm();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => TxtHosts.Focus());
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolFwTitle");
        LblHosts.Text = L("ToolFwHostsLabel");
        LblPorts.Text = L("ToolFwPortsLabel");
        BtnTest.Content = L("ToolFwBtnTest");
        BtnCopy.Content = L("ToolFwBtnCopy");
        BtnExportCsv.Content = L("ToolFwBtnExport");

        BtnPresetWeb.Content = L("ToolFwPresetWeb");
        BtnPresetRemote.Content = L("ToolFwPresetRemote");
        BtnPresetCommon.Content = L("ToolFwPresetCommon");

        LblRouteVia.Text = L("ToolTunnelRouteVia");
        TxtEmptyState.Text = L("ToolFwEmptyState");
        TxtHosts.Tag = L("ToolFwTestHostsPlaceholder");
        TxtPorts.Tag = L("ToolWatermarkPortList");
        LblSummary.Text = string.Empty;

        System.Windows.Automation.AutomationProperties.SetName(BtnTest, L("ToolFwBtnTest"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopy, L("ToolFwBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(BtnExportCsv, L("ToolFwBtnExport"));
        System.Windows.Automation.AutomationProperties.SetName(TxtHosts, L("ToolFwHostsLabel"));
        System.Windows.Automation.AutomationProperties.SetName(TxtPorts, L("ToolFwPortsLabel"));
        System.Windows.Automation.AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetWeb, L("ToolFwPresetWeb"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetRemote, L("ToolFwPresetRemote"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetCommon, L("ToolFwPresetCommon"));
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        System.Windows.Automation.AutomationProperties.SetName(TestProgress, L("ToolFwA11yProgress"));
        System.Windows.Automation.AutomationProperties.SetName(HeatmapGrid, L("ToolFwTitle"));

        BtnCopy.ToolTip = L("ToolBtnCopyToClipboard");
        BtnHelp.ToolTip = L("ToolHelpTooltip");
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpFWTEST").Replace("\\n", "\n", StringComparison.Ordinal);
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsTesting)
        {
            StopTest();
        }
        else
        {
            _ = StartTestAsync();
        }
    }

    private async Task StartTestAsync()
    {
        if (_disposed || _vm.IsTesting)
        {
            return;
        }

        LblSummary.Text = string.Empty;
        HeatmapGrid.Children.Clear();
        HeatmapGrid.RowDefinitions.Clear();
        HeatmapGrid.ColumnDefinitions.Clear();

        var (hosts, ports, errorKey) = _vm.ParseAndValidateInputs(TxtHosts.Text, TxtPorts.Text);
        if (errorKey is not null)
        {
            _viewState.ShowError(L(errorKey));
            return;
        }

        TxtHosts.Text = string.Join(Environment.NewLine, hosts!);
        TxtPorts.Text = string.Join(',', ports!);

        await _vm.TestAsync(hosts!, ports!);

        if (_vm.ShowError)
        {
            return;
        }

        var results = _vm.GetAllResults();
        if (results.Count > 0)
        {
            BuildHeatmap(_vm.GetLastHosts().ToList(), _vm.GetLastPorts().ToList());
            LblSummary.Text = BuildLocalizedSummary(results);
        }
        else
        {
            _viewState.Reset();
            LblSummary.Text = string.Empty;
        }
    }

    private string BuildLocalizedSummary(IReadOnlyList<FwProbeResult> results)
    {
        var summary = FirewallProbeEngine.ComputeSummary(results);
        return string.Format(L("ToolFwSummary"), summary.Open, summary.Closed, summary.Timeout, summary.Total);
    }

    private void StopTest()
    {
        _vm.CancelTest();
    }

    private void SetTestInputsEnabled(bool enabled)
    {
        TxtHosts.IsReadOnly = !enabled;
        TxtPorts.IsReadOnly = !enabled;
        CmbRouteVia.IsEnabled = enabled;
        BtnPresetWeb.IsEnabled = enabled;
        BtnPresetRemote.IsEnabled = enabled;
        BtnPresetCommon.IsEnabled = enabled;
    }

    private void BuildHeatmap(List<string> hosts, List<int> ports)
    {
        HeatmapGrid.Children.Clear();
        HeatmapGrid.RowDefinitions.Clear();
        HeatmapGrid.ColumnDefinitions.Clear();

        var results = _vm.GetAllResults();
        var lookup = results.ToDictionary(result => (result.Host, result.Port));

        var successBrush = FindResource("SuccessBrush") as Brush ?? Brushes.Green;
        var errorBrush = FindResource("ErrorBrush") as Brush ?? Brushes.Red;
        var disabledBrush = FindResource("TextDisabledBrush") as Brush ?? Brushes.Gray;
        var textPrimary = FindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
        var textSecondary = FindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;

        HeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        foreach (var _ in ports)
        {
            HeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        }

        HeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var cornerBlock = new TextBlock { Text = string.Empty, Margin = new Thickness(2) };
        Grid.SetRow(cornerBlock, 0);
        Grid.SetColumn(cornerBlock, 0);
        HeatmapGrid.Children.Add(cornerBlock);

        for (var column = 0; column < ports.Count; column++)
        {
            var header = new TextBlock
            {
                Text = ports[column].ToString(),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = (double)FindResource("FontSizeCaption"),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontFamilyMonospace"),
                FontWeight = FontWeights.SemiBold,
                Foreground = textPrimary,
                Margin = new Thickness(2, 4, 2, 4),
            };
            System.Windows.Automation.AutomationProperties.SetName(header, string.Format(L("ToolFwA11yPort"), ports[column]));
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, column + 1);
            HeatmapGrid.Children.Add(header);
        }

        for (var row = 0; row < hosts.Count; row++)
        {
            HeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var hostLabel = new TextBlock
            {
                Text = hosts[row],
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = (double)FindResource("FontSizeCaption"),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontFamilyMonospace"),
                Foreground = textSecondary,
                Margin = new Thickness(4, 2, 8, 2),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 150,
                ToolTip = hosts[row],
            };
            System.Windows.Automation.AutomationProperties.SetName(hostLabel, hosts[row]);
            Grid.SetRow(hostLabel, row + 1);
            Grid.SetColumn(hostLabel, 0);
            HeatmapGrid.Children.Add(hostLabel);

            for (var column = 0; column < ports.Count; column++)
            {
                var host = hosts[row];
                var port = ports[column];
                lookup.TryGetValue((host, port), out var result);

                var cellBrush = result?.Status switch
                {
                    ProbeStatus.Open => successBrush,
                    ProbeStatus.Closed => errorBrush,
                    ProbeStatus.Timeout => disabledBrush,
                    _ => disabledBrush,
                };

                var tooltipText = result?.Status switch
                {
                    ProbeStatus.Open => $"{L("ToolFwStatusOpen")} - {result.ResponseTimeMs} ms",
                    ProbeStatus.Closed => L("ToolFwStatusClosed"),
                    ProbeStatus.Timeout => L("ToolFwStatusTimeout"),
                    _ => L("ToolFwStatusTimeout"),
                };

                var localizedStatus = result?.Status switch
                {
                    ProbeStatus.Open => L("ToolFwStatusOpen"),
                    ProbeStatus.Closed => L("ToolFwStatusClosed"),
                    ProbeStatus.Timeout => L("ToolFwStatusTimeout"),
                    _ => L("ToolFwStatusTimeout"),
                };

                var cell = new Border
                {
                    Background = cellBrush,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(1),
                    MinHeight = 24,
                    MinWidth = 24,
                    ToolTip = tooltipText,
                };
                System.Windows.Automation.AutomationProperties.SetName(
                    cell,
                    string.Format(L("ToolFwA11yCell"), host, port, localizedStatus));
                Grid.SetRow(cell, row + 1);
                Grid.SetColumn(cell, column + 1);
                HeatmapGrid.Children.Add(cell);
            }
        }

        _viewState.ShowResults();
    }

    private void OnGatewaySelected(SshGatewayDto? gateway)
    {
        _vm.SetGateway(gateway);
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string ports)
        {
            TxtPorts.Text = ports;
        }
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        var results = _vm.GetAllResults();
        if (results.Count == 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("FileDialogCsvFilter"),
            FileName = $"firewall_test_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var csv = FirewallProbeEngine.BuildMatrixCsv(
                results,
                _vm.GetLastHosts(),
                _vm.GetLastPorts(),
                L);
            File.WriteAllText(dialog.FileName, csv, Encoding.UTF8);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"FirewallTester CSV export failed: {ex.Message}");
            _viewState.ShowError(
                string.Format(L("StatusExportFailed"), ex.Message),
                showEmptyState: false,
                keepResultsVisible: true);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var results = _vm.GetAllResults();
        if (results.Count == 0)
        {
            return;
        }

        try
        {
            var text = FirewallProbeEngine.BuildMatrixText(
                results,
                _vm.GetLastHosts(),
                _vm.GetLastPorts(),
                L);
            Clipboard.SetText(text);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"FirewallTester clipboard copy failed: {ex.Message}");
            _viewState.ShowError(
                string.Format(L("StatusClipboardCopyFailed"), ex.Message),
                showEmptyState: false,
                keepResultsVisible: true);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshUiFromVm();
    }

    private void RefreshUiFromVm()
    {
        _setBusy?.Invoke(_vm.IsTesting);

        if (_vm.IsTesting)
        {
            BtnTest.Content = L("ToolFwBtnStop");
            BtnTest.Foreground = (Brush)FindResource("ErrorBrush");
            BtnTest.Style = (Style)FindResource("SecondaryButtonStyle");
            System.Windows.Automation.AutomationProperties.SetName(BtnTest, L("ToolFwBtnStop"));
            ProgressPanel.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            ResultsPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            BtnTest.Content = L("ToolFwBtnTest");
            BtnTest.Foreground = (Brush)FindResource("TextPrimaryBrush");
            BtnTest.Style = (Style)FindResource("PrimaryButtonStyle");
            System.Windows.Automation.AutomationProperties.SetName(BtnTest, L("ToolFwBtnTest"));
            ProgressPanel.Visibility = Visibility.Collapsed;
        }

        SetTestInputsEnabled(!_vm.IsTesting);

        TxtProgressPercent.Text = $"{_vm.ProgressPercent}%";
        TxtProgressCount.Text = _vm.ProgressCountText;
        TestProgress.IsIndeterminate = false;
        TestProgress.Maximum = _vm.Total <= 0 ? 1 : _vm.Total;
        TestProgress.Value = _vm.Completed;
        LblSummary.Text = _vm.SummaryText;

        if (_vm.ShowError)
        {
            _viewState.ShowError(_vm.ErrorText);
        }
        else if (!_vm.IsTesting && _vm.GetAllResults().Count == 0)
        {
            _viewState.Reset();
        }
    }

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsTesting;

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
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
}
