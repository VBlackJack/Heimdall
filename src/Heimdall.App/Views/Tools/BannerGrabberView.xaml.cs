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
/// Banner grabber tool that connects to TCP ports on a target host
/// and displays raw service identification banners.
/// </summary>
public partial class BannerGrabberView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private bool _disposed;
    private List<SshGatewayDto>? _gateways;
    private Action<string, string, ToolContext?>? _openToolAction;
    private Action<bool>? _setBusy;
    private readonly ToolAsyncStateController _viewState;
    private readonly ObservableCollection<BannerResult> _results = [];
    private readonly BannerGrabViewModel _vm;

    public BannerGrabberView()
    {
        _vm = new BannerGrabViewModel();
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        _viewState = new ToolAsyncStateController(
            isBusy => _setBusy?.Invoke(isBusy),
            null,
            TxtError,
            EmptyStatePanel,
            ResultsPanel,
            null);

        ResultsGrid.ItemsSource = _results;
        TxtHost.KeyDown += OnHostKeyDown;
        TxtPorts.KeyDown += OnHostKeyDown;
        ResultsGrid.PreviewMouseRightButtonDown += ToolContextMenuHelper.SelectRowOnRightClick;
        ResultsGrid.ContextMenuOpening += OnResultsContextMenuOpening;
        RefreshUiFromVm();
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
        TxtPorts.Text = NetworkToolPresets.BannerGrabberDefaultPorts;

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            TxtHost.Text = context.TargetHost;
        }

        if (context?.SshGateways is IList gateways)
        {
            _gateways = gateways.Cast<SshGatewayDto>().ToList();
        }

        PopulateRouteSelector();
        _results.Clear();
        _viewState.Reset();
        RefreshUiFromVm();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtHost.Focus();
            TxtHost.SelectAll();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolBannerTitle");
        BtnGrab.Content = L("ToolBannerBtnGrab");
        BtnCopy.Content = L("ToolBannerBtnCopy");
        BtnExportCsv.Content = L("ToolBannerBtnExport");

        ColPort.Header = L("ToolBannerColPort");
        ColService.Header = L("ToolBannerColService");
        ColBanner.Header = L("ToolBannerColBanner");
        ColResponseTime.Header = L("ToolBannerColTime");

        System.Windows.Automation.AutomationProperties.SetName(BtnGrab, L("ToolBannerBtnGrab"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopy, L("ToolBannerBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(TxtHost, L("ToolBannerHostLabel"));
        System.Windows.Automation.AutomationProperties.SetName(TxtPorts, L("ToolBannerPortsLabel"));

        BtnCopy.ToolTip = L("ToolBtnCopyToClipboard");
        System.Windows.Automation.AutomationProperties.SetName(BtnExportCsv, L("ToolBannerBtnExport"));

        BtnPresetWeb.Content = L("ToolBannerPresetWeb");
        BtnPresetRemote.Content = L("ToolBannerPresetRemote");
        BtnPresetMail.Content = L("ToolBannerPresetMail");
        BtnPresetDatabase.Content = L("ToolBannerPresetDatabase");
        ChkBannerOnly.Content = L("ToolBannerChkBannerOnly");

        System.Windows.Automation.AutomationProperties.SetName(ChkBannerOnly, L("ToolBannerChkBannerOnly"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetWeb, L("ToolBannerPresetWeb"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetRemote, L("ToolBannerPresetRemote"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetMail, L("ToolBannerPresetMail"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetDatabase, L("ToolBannerPresetDatabase"));

        LblHost.Text = L("ToolBannerHostLabel");
        LblPorts.Text = L("ToolBannerPortsLabel");

        LblRouteVia.Text = L("ToolTunnelRouteVia");
        System.Windows.Automation.AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        System.Windows.Automation.AutomationProperties.SetName(GrabProgress, L("ToolBannerA11yProgress"));
        System.Windows.Automation.AutomationProperties.SetName(ResultsGrid, L("ToolBannerTitle"));

        TxtEmptyState.Text = L("ToolBannerEmptyState");

        TxtHost.Tag = L("ToolWatermarkHostnameOrIp");
        TxtPorts.Tag = L("ToolWatermarkPortList");
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpBANNER").Replace("\\n", "\n", StringComparison.Ordinal);
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
            ToggleGrab();
            e.Handled = true;
        }
    }

    private void OnGrabClick(object sender, RoutedEventArgs e)
    {
        ToggleGrab();
    }

    private void ToggleGrab()
    {
        if (_vm.IsGrabbing)
        {
            StopGrab();
        }
        else
        {
            _ = StartGrabAsync();
        }
    }

    private async Task StartGrabAsync()
    {
        _results.Clear();
        RefreshUiFromVm();

        await _vm.GrabAsync(TxtHost.Text.Trim(), TxtPorts.Text.Trim());

        ApplyBannerOnlyFilter();
        RefreshUiFromVm();
    }

    private void StopGrab()
    {
        _vm.CancelGrab();
    }

    private void SetGrabInputsEnabled(bool enabled)
    {
        TxtHost.IsReadOnly = !enabled;
        TxtPorts.IsReadOnly = !enabled;
        CmbRouteVia.IsEnabled = enabled;
        BtnPresetWeb.IsEnabled = enabled;
        BtnPresetRemote.IsEnabled = enabled;
        BtnPresetMail.IsEnabled = enabled;
        BtnPresetDatabase.IsEnabled = enabled;
        ChkBannerOnly.IsEnabled = enabled;
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

    private void OnBannerOnlyChanged(object sender, RoutedEventArgs e)
    {
        ApplyBannerOnlyFilter();
    }

    /// <summary>
    /// Applies the "banner only" filter to the results grid.
    /// </summary>
    private void ApplyBannerOnlyFilter()
    {
        _results.Clear();

        var bannerOnly = ChkBannerOnly?.IsChecked == true;
        foreach (var result in _vm.GetFilteredResults(bannerOnly))
        {
            _results.Add(result);
        }

        TxtResultCount.Text = _vm.ResultCountText;
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_vm.GetAllResults().Count == 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("FileDialogCsvFilter"),
            FileName = $"banners_{TxtHost.Text.Trim()}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _vm.BuildCsvExport(), Encoding.UTF8);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"BannerGrabber CSV export failed: {ex.Message}");
            _viewState.ShowError(
                string.Format(L("StatusExportFailed"), ex.Message),
                showEmptyState: false,
                keepResultsVisible: true);
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
            var text = _vm.BuildClipboardText([.. _results]);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            Clipboard.SetText(text);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"BannerGrabber clipboard copy failed: {ex.Message}");
            _viewState.ShowError(
                string.Format(L("StatusClipboardCopyFailed"), ex.Message),
                showEmptyState: false,
                keepResultsVisible: true);
        }
    }

    private void OnResultsContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not BannerResult row)
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

        if (_openToolAction is not null && row.HasBanner)
        {
            menu.Items.Add(new Separator());

            var portScan = new MenuItem { Header = L("ToolCtxOpenPortScan") };
            portScan.Click += (_, _) => _openToolAction(
                "PORTSCAN",
                L("PaletteToolPortScan"),
                new ToolContext(TargetHost: host, TargetPort: row.Port));
            menu.Items.Add(portScan);
        }

        menu.Items.Add(new Separator());
        var csvText = $"{row.Port}\t{row.Service}\t{row.Banner}\t{row.ResponseTime}";
        menu.Items.Add(ToolContextMenuHelper.BuildCopyRowAction(csvText, _localizer));
        menu.Items.Add(ToolContextMenuHelper.BuildCopyAllAction(ResultsGrid, _localizer));
        menu.Items.Add(new Separator());
        menu.Items.Add(ToolContextMenuHelper.BuildExportCsvAction(ResultsGrid, _localizer));

        ResultsGrid.ContextMenu = menu;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BannerGrabViewModel.Completed)
            or nameof(BannerGrabViewModel.ResultCountText))
        {
            ApplyBannerOnlyFilter();
        }

        if (e.PropertyName is nameof(BannerGrabViewModel.IsGrabbing)
            or nameof(BannerGrabViewModel.ShowError)
            or nameof(BannerGrabViewModel.ErrorText)
            or nameof(BannerGrabViewModel.Completed)
            or nameof(BannerGrabViewModel.Total)
            or nameof(BannerGrabViewModel.ProgressPercent)
            or nameof(BannerGrabViewModel.ProgressCountText)
            or nameof(BannerGrabViewModel.ResultCountText))
        {
            RefreshUiFromVm();
        }
    }

    private void RefreshUiFromVm()
    {
        if (_vm.IsGrabbing)
        {
            _setBusy?.Invoke(true);
            _viewState.Reset(showEmptyState: false);
            _viewState.ShowResults();
        }
        else if (_vm.ShowError)
        {
            _setBusy?.Invoke(false);
            _viewState.Reset(showEmptyState: _results.Count == 0);
            _viewState.ShowError(
                _vm.ErrorText,
                showEmptyState: _results.Count == 0,
                keepResultsVisible: _results.Count > 0);
        }
        else if (_results.Count > 0)
        {
            _setBusy?.Invoke(false);
            _viewState.Reset(showEmptyState: false);
            _viewState.ShowResults();
        }
        else
        {
            _setBusy?.Invoke(false);
            _viewState.Reset(showEmptyState: true);
        }

        SetGrabInputsEnabled(!_vm.IsGrabbing);
        GrabProgress.IsIndeterminate = false;
        GrabProgress.Maximum = Math.Max(_vm.Total, 1);
        GrabProgress.Value = Math.Min(_vm.Completed, GrabProgress.Maximum);
        TxtProgressPercent.Text = $"{_vm.ProgressPercent}%";
        TxtProgressCount.Text = _vm.ProgressCountText;
        ProgressPanel.Visibility = _vm.IsGrabbing ? Visibility.Visible : Visibility.Collapsed;
        TxtResultCount.Text = _vm.ResultCountText;

        if (_vm.IsGrabbing)
        {
            BtnGrab.Content = L("ToolBannerBtnStop");
            BtnGrab.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            BtnGrab.Style = (Style)FindResource("SecondaryButtonStyle");
            System.Windows.Automation.AutomationProperties.SetName(BtnGrab, L("ToolBannerBtnStop"));
        }
        else
        {
            BtnGrab.Content = L("ToolBannerBtnGrab");
            BtnGrab.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
            BtnGrab.Style = (Style)FindResource("PrimaryButtonStyle");
            System.Windows.Automation.AutomationProperties.SetName(BtnGrab, L("ToolBannerBtnGrab"));
        }
    }

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsGrabbing;

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
        _vm.Dispose();
        _setBusy?.Invoke(false);
        GC.SuppressFinalize(this);
    }
}
