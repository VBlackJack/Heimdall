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

using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Network;
using Heimdall.Ssh;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Port scanner tool that probes TCP ports on a target host
/// and displays results with service identification.
/// </summary>
public partial class PortScannerView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private bool _disposed;
    private List<SshGatewayDto>? _gateways;
    private Action<string, string, ToolContext?>? _openToolAction;
    private Action<bool>? _setBusy;
    private readonly ObservableCollection<PortScanResult> _results = [];
    private readonly PortScanViewModel _vm;

    public PortScannerView()
    {
        _vm = new PortScanViewModel();
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        ResultsGrid.ItemsSource = _results;
        TxtHost.KeyDown += OnHostKeyDown;
        TxtPorts.KeyDown += OnHostKeyDown;
        ResultsGrid.PreviewMouseRightButtonDown += ToolContextMenuHelper.SelectRowOnRightClick;
        ResultsGrid.ContextMenuOpening += OnResultsContextMenuOpening;
        SizeChanged += OnViewSizeChanged;
        UpdateResultsSurface();
    }

    /// <summary>
    /// Initializes the tool with optional context and localizer.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _openToolAction = ToolContextMenuHelper.GetOpenToolAction(context);
        _setBusy = context?.SetBusyAction;
        _vm.Initialize(localizer);
        ApplyLocalization();

        TxtHost.Clear();
        TxtPorts.Text = NetworkToolPresets.PortScannerDefaultPorts;

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            TxtHost.Text = context.TargetHost;
        }

        if (context?.SshGateways is IList gateways)
        {
            _gateways = gateways.Cast<SshGatewayDto>().ToList();
        }

        PopulateRouteSelector();
        UpdateResponsiveLayout(ActualWidth);
        RefreshUiFromVm();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtHost.Focus();
            TxtHost.SelectAll();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolPortScanTitle");
        BtnScan.Content = L("ToolPortScanBtnStart");
        BtnStop.Content = L("ToolPortScanBtnStop");
        BtnCopy.Content = L("ToolPortScanBtnCopy");

        ColPort.Header = L("ToolPortScanColPort");
        ColStatus.Header = L("ToolPortScanColStatus");
        ColService.Header = L("ToolPortScanColService");
        ColResponseTime.Header = L("ToolPortScanColResponseTime");
        ColBanner.Header = L("ToolPortScanColBanner");
        BtnExportCsv.Content = L("ToolPortScanBtnExportCsv");

        System.Windows.Automation.AutomationProperties.SetName(BtnScan, L("ToolPortScanBtnStart"));
        System.Windows.Automation.AutomationProperties.SetName(BtnStop, L("ToolPortScanBtnStop"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopy, L("ToolPortScanBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(TxtHost, L("ToolPortScanHostLabel"));
        System.Windows.Automation.AutomationProperties.SetName(TxtPorts, L("ToolPortScanPortsLabel"));

        BtnStop.ToolTip = L("ToolPortScanBtnStop");
        BtnCopy.ToolTip = L("ToolBtnCopyToClipboard");
        System.Windows.Automation.AutomationProperties.SetName(BtnExportCsv, L("ToolPortScanBtnExportCsv"));

        BtnPresetWeb.Content = L("ToolPortScanPresetWeb");
        BtnPresetSshRemote.Content = L("ToolPortScanPresetSshRemote");
        BtnPresetDatabase.Content = L("ToolPortScanPresetDatabase");
        BtnPresetCommon.Content = L("ToolPortScanPresetCommon");
        BtnPresetFull.Content = L("ToolPortScanPresetFull");
        ChkOpenOnly.Content = L("ToolPortScanChkOpenOnly");

        System.Windows.Automation.AutomationProperties.SetName(ChkOpenOnly, L("ToolPortScanChkOpenOnly"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetWeb, L("ToolPortScanPresetWeb"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetSshRemote, L("ToolPortScanPresetSshRemote"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetDatabase, L("ToolPortScanPresetDatabase"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetCommon, L("ToolPortScanPresetCommon"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetFull, L("ToolPortScanPresetFull"));

        LblHost.Text = L("ToolPortScanHostLabel");
        LblPorts.Text = L("ToolPortScanPortsLabel");

        LblRouteVia.Text = L("ToolTunnelRouteVia");
        System.Windows.Automation.AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        TxtEmptyState.Text = L("ToolEmptyStatePortScan");

        TxtHost.Tag = L("ToolWatermarkHostnameOrIp");
        TxtPorts.Tag = L("ToolWatermarkPortList");

        System.Windows.Automation.AutomationProperties.SetName(ScanProgress, L("ToolPortScanA11yProgress"));
        System.Windows.Automation.AutomationProperties.SetName(ResultsGrid, L("ToolPortScanTitle"));
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpPORTSCAN").Replace("\\n", "\n", StringComparison.Ordinal);
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ToggleScan();
            e.Handled = true;
        }
    }

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        ToggleScan();
    }

    private void OnStopScanClick(object sender, RoutedEventArgs e)
    {
        StopScan();
    }

    private void ToggleScan()
    {
        if (_vm.IsScanning)
        {
            StopScan();
        }
        else
        {
            _ = StartScanAsync();
        }
    }

    private async Task StartScanAsync()
    {
        if (_disposed || _vm.IsScanning)
        {
            return;
        }

        var host = TxtHost.Text.Trim();
        var portsText = TxtPorts.Text.Trim();

        var (ports, errorKey) = _vm.ParseAndValidatePorts(portsText);
        if (ports is null)
        {
            TxtError.Text = L(errorKey ?? "ToolValidationPortRangeRequired");
            TxtError.Visibility = Visibility.Visible;
            UpdateResultsSurface();
            return;
        }

        if (ports.Count > PortScanEngine.LargePortCountWarningThreshold)
        {
            var message = string.Format(L("ToolPortScanLargeRangeWarning"), ports.Count);
            var dialogService = (Application.Current as App)?.Services
                ?.GetService(typeof(IDialogService)) as IDialogService;
            if (dialogService is null
                || !await dialogService.ShowConfirmAsync(L("ToolPortScanTitle"), message, "warning"))
            {
                return;
            }
        }

        _results.Clear();
        TxtError.Text = string.Empty;
        TxtError.Visibility = Visibility.Collapsed;

        SetScanInputsEnabled(false);
        ScanProgress.IsIndeterminate = false;
        ScanProgress.Value = 0;
        TxtProgressPercent.Text = "0%";
        TxtProgressCount.Text = string.Format(L("ToolPortScanProgressCount"), 0, ports.Count);
        ProgressPanel.Visibility = Visibility.Visible;
        TxtOpen.Text = FormatStat("ToolPortScanOpen", 0);
        TxtClosed.Text = FormatStat("ToolPortScanClosed", 0);
        TxtTotal.Text = FormatStat("ToolPortScanTotal", ports.Count);
        UpdateResultsSurface();

        await _vm.ScanAsync(host, ports);

        ApplyOpenOnlyFilter();
        RefreshUiFromVm();
    }

    private void StopScan()
    {
        _vm.CancelScan();
    }

    private void SetScanInputsEnabled(bool enabled)
    {
        TxtHost.IsReadOnly = !enabled;
        TxtPorts.IsReadOnly = !enabled;
        CmbRouteVia.IsEnabled = enabled;
        BtnPresetWeb.IsEnabled = enabled;
        BtnPresetSshRemote.IsEnabled = enabled;
        BtnPresetDatabase.IsEnabled = enabled;
        BtnPresetCommon.IsEnabled = enabled;
        BtnPresetFull.IsEnabled = enabled;
        ChkOpenOnly.IsEnabled = enabled;
    }

    private void PopulateRouteSelector()
    {
        CmbRouteVia.Items.Clear();
        CmbRouteVia.Items.Add(new ComboBoxItem { Content = L("ToolTunnelDirect") });

        if (_gateways is not null)
        {
            foreach (var gateway in _gateways)
            {
                var label = $"{gateway.Name} ({gateway.Host}:{gateway.Port})";
                CmbRouteVia.Items.Add(new ComboBoxItem { Content = label, Tag = gateway });
            }
        }

        CmbRouteVia.SelectedIndex = 0;
    }

    private void OnRouteViaChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbRouteVia.SelectedItem is ComboBoxItem item && item.Tag is SshGatewayDto gateway)
        {
            _vm.SetGateway(gateway);
        }
        else
        {
            _vm.SetGateway(null);
        }
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string ports)
        {
            TxtPorts.Text = ports;
        }
    }

    private void OnOpenOnlyChanged(object sender, RoutedEventArgs e)
    {
        ApplyOpenOnlyFilter();
    }

    /// <summary>
    /// Applies the "open only" filter to the results grid.
    /// </summary>
    private void ApplyOpenOnlyFilter()
    {
        _results.Clear();
        var openOnly = ChkOpenOnly?.IsChecked == true;

        foreach (var probe in _vm.GetAllResults())
        {
            if (openOnly && !probe.IsOpen)
            {
                continue;
            }

            _results.Add(new PortScanResult(
                probe.Port,
                probe.IsOpen,
                probe.Service,
                probe.ResponseTime,
                probe.IsOpen ? L("ToolPortScanStatusOpen") : L("ToolPortScanStatusClosed"),
                probe.Banner ?? string.Empty));
        }

        UpdateResultsSurface();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void UpdateResponsiveLayout(double width)
    {
        var showBanner = width >= 1100;
        var showResponseTime = width >= 880;
        var showService = width >= 720;

        ColBanner.Visibility = showBanner ? Visibility.Visible : Visibility.Collapsed;
        ColResponseTime.Visibility = showResponseTime ? Visibility.Visible : Visibility.Collapsed;
        ColService.Visibility = showService ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateResultsSurface()
    {
        var hasResults = _results.Count > 0;
        var hasError = TxtError.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(TxtError.Text);

        ResultsPanel.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasResults || _vm.IsScanning || hasError
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_results.Count == 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("FileDialogCsvFilter"),
            FileName = $"portscan_{TxtHost.Text.Trim()}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, PortScanEngine.BuildCsvExport([.. _results], CreateLocalize()), Encoding.UTF8);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"PortScanner CSV export failed: {ex.Message}");
            TxtError.Text = string.Format(L("StatusExportFailed"), ex.Message);
            TxtError.Visibility = Visibility.Visible;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_results.Count == 0)
        {
            return;
        }

        try
        {
            var text = PortScanEngine.BuildClipboardText([.. _results], CreateLocalize());
            Clipboard.SetText(text);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"PortScanner clipboard copy failed: {ex.Message}");
            TxtError.Text = string.Format(L("StatusClipboardCopyFailed"), ex.Message);
            TxtError.Visibility = Visibility.Visible;
        }
    }

    private void OnResultsContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not PortScanResult row)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();
        var host = TxtHost.Text.Trim();

        var copyPort = new MenuItem { Header = L("ToolCtxCopyPort") };
        copyPort.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(row.Port.ToString());
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Clipboard temporarily locked.
            }
        };
        menu.Items.Add(copyPort);

        if (!string.IsNullOrWhiteSpace(row.Service))
        {
            var copyService = new MenuItem { Header = L("ToolCtxCopyService") };
            copyService.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(row.Service);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Clipboard temporarily locked.
                }
            };
            menu.Items.Add(copyService);
        }

        if (!string.IsNullOrWhiteSpace(row.Banner))
        {
            var copyBanner = new MenuItem { Header = L("ToolCtxCopyBanner") };
            copyBanner.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(row.Banner);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Clipboard temporarily locked.
                }
            };
            menu.Items.Add(copyBanner);
        }

        if (_openToolAction is not null && row.IsOpen)
        {
            menu.Items.Add(new Separator());

            if (row.Port is 443 or 8443 or 636 or 993 or 995)
            {
                var cert = new MenuItem { Header = L("ToolCtxOpenCertInspector") };
                cert.Click += (_, _) => _openToolAction(
                    "CERT",
                    L("PaletteToolCert"),
                    new ToolContext(TargetHost: host, TargetPort: row.Port));
                menu.Items.Add(cert);
            }

            if (row.Port is 80 or 443 or 8080 or 8443 or 8006 or 5000 or 3000 or 9090)
            {
                var scheme = row.Port is 443 or 8443 ? "https" : "http";
                var url = $"{scheme}://{host}:{row.Port}";
                var browser = new MenuItem { Header = string.Format(L("ToolCtxOpenBrowser"), url) };
                browser.Click += (_, _) =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true,
                    })?.Dispose();
                };
                menu.Items.Add(browser);
            }
        }

        menu.Items.Add(new Separator());
        var csvText = $"{row.Port}\t{row.Status}\t{row.Service}\t{row.ResponseTime}\t{row.Banner}";
        menu.Items.Add(ToolContextMenuHelper.BuildCopyRowAction(csvText, _localizer));
        menu.Items.Add(ToolContextMenuHelper.BuildCopyAllAction(ResultsGrid, _localizer));
        menu.Items.Add(new Separator());
        menu.Items.Add(ToolContextMenuHelper.BuildExportCsvAction(ResultsGrid, _localizer));

        ResultsGrid.ContextMenu = menu;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PortScanViewModel.Completed)
            or nameof(PortScanViewModel.OpenCount)
            or nameof(PortScanViewModel.ClosedCount))
        {
            ApplyOpenOnlyFilter();
        }

        if (e.PropertyName is nameof(PortScanViewModel.IsScanning)
            or nameof(PortScanViewModel.ShowError)
            or nameof(PortScanViewModel.ErrorText)
            or nameof(PortScanViewModel.Completed)
            or nameof(PortScanViewModel.Total)
            or nameof(PortScanViewModel.ProgressPercent)
            or nameof(PortScanViewModel.ProgressCountText)
            or nameof(PortScanViewModel.OpenCount)
            or nameof(PortScanViewModel.ClosedCount))
        {
            RefreshUiFromVm();
        }
    }

    private void RefreshUiFromVm()
    {
        TxtError.Text = _vm.ShowError ? _vm.ErrorText : string.Empty;
        TxtError.Visibility = _vm.ShowError && !string.IsNullOrWhiteSpace(_vm.ErrorText)
            ? Visibility.Visible
            : Visibility.Collapsed;

        SetScanInputsEnabled(!_vm.IsScanning);
        ScanProgress.IsIndeterminate = false;
        ScanProgress.Maximum = Math.Max(_vm.Total, 1);
        ScanProgress.Value = Math.Min(_vm.Completed, ScanProgress.Maximum);
        TxtProgressPercent.Text = $"{_vm.ProgressPercent}%";
        TxtProgressCount.Text = _vm.ProgressCountText;
        TxtOpen.Text = FormatStat("ToolPortScanOpen", _vm.OpenCount);
        TxtClosed.Text = FormatStat("ToolPortScanClosed", _vm.ClosedCount);
        TxtTotal.Text = FormatStat("ToolPortScanTotal", _vm.Total);
        ProgressPanel.Visibility = _vm.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        _setBusy?.Invoke(_vm.IsScanning);
        BtnScan.Visibility = _vm.IsScanning ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(BtnScan, L("ToolPortScanBtnStart"));

        UpdateResultsSurface();
    }

    private string L(string key) => _localizer?[key] ?? key;

    private string FormatStat(string key, int value) => $"{L(key)}: {value}";

    private Func<string, string> CreateLocalize() => key => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsScanning;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        TxtHost.KeyDown -= OnHostKeyDown;
        TxtPorts.KeyDown -= OnHostKeyDown;
        ResultsGrid.PreviewMouseRightButtonDown -= ToolContextMenuHelper.SelectRowOnRightClick;
        ResultsGrid.ContextMenuOpening -= OnResultsContextMenuOpening;
        SizeChanged -= OnViewSizeChanged;
        _vm.Dispose();
        _setBusy?.Invoke(false);
        GC.SuppressFinalize(this);
    }
}
