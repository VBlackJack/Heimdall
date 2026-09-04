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
/// SNMP Walker tool that queries SNMP agents to enumerate device information.
/// </summary>
public partial class SnmpWalkerView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private bool _disposed;
    private bool _isTestingCommunities;
    private Action<bool>? _setBusy;
    private GatewayRouteSelector? _routeSelector;
    private readonly ToolAsyncStateController _viewState;
    private readonly ObservableCollection<SnmpEntry> _results = [];
    private readonly ObservableCollection<CommunityResult> _communityResults = [];
    private readonly SnmpWalkerViewModel _vm;

    public SnmpWalkerView()
    {
        _vm = new SnmpWalkerViewModel();
        InitializeComponent();
        _vm.PropertyChanged += OnVmPropertyChanged;

        _viewState = new ToolAsyncStateController(
            isBusy => _setBusy?.Invoke(isBusy),
            LoadingBar,
            TxtError,
            EmptyStatePanel,
            ResultsBorder,
            TxtStatus,
            TxtHost,
            TxtCommunity,
            TxtOid,
            CmbRouteVia,
            BtnPresetSystem,
            BtnPresetInterfaces,
            BtnPresetIp,
            BtnPresetTcp,
            BtnPresetUdp,
            BtnTestCommunities);

        ResultsGrid.ItemsSource = _results;
        CommunityResults.ItemsSource = _communityResults;
        TxtHost.KeyDown += OnHostKeyDown;
        TxtOid.KeyDown += OnHostKeyDown;
        RefreshUiFromVm();
    }

    /// <summary>
    /// Initializes the tool with optional context and localization.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _setBusy = context?.SetBusyAction;
        _vm.Initialize(localizer);
        ApplyLocalization();

        TxtHost.Clear();
        TxtCommunity.Text = NetworkToolPresets.SnmpDefaultCommunity;
        TxtOid.Text = NetworkToolPresets.SnmpDefaultOid;

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            TxtHost.Text = context.TargetHost;
        }

        _routeSelector?.Dispose();
        _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportRouteStatus);
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
        HeaderTitle.Text = L("ToolSnmpTitle");
        BtnWalk.Content = L("ToolSnmpBtnWalk");
        BtnCopy.Content = L("ToolSnmpBtnCopy");
        BtnExportCsv.Content = L("ToolSnmpBtnExport");
        BtnTestCommunities.Content = L("ToolSnmpBtnTestCommunities");

        ColOid.Header = L("ToolSnmpColOid");
        ColName.Header = L("ToolSnmpColName");
        ColType.Header = L("ToolSnmpColType");
        ColValue.Header = L("ToolSnmpColValue");

        BtnPresetSystem.Content = L("ToolSnmpPresetSystem");
        BtnPresetInterfaces.Content = L("ToolSnmpPresetInterfaces");
        BtnPresetIp.Content = L("ToolSnmpPresetIp");
        BtnPresetTcp.Content = L("ToolSnmpPresetTcp");
        BtnPresetUdp.Content = L("ToolSnmpPresetUdp");

        System.Windows.Automation.AutomationProperties.SetName(BtnWalk, L("ToolSnmpBtnWalk"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopy, L("ToolSnmpBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(BtnExportCsv, L("ToolSnmpBtnExport"));
        System.Windows.Automation.AutomationProperties.SetName(BtnTestCommunities, L("ToolSnmpBtnTestCommunities"));
        System.Windows.Automation.AutomationProperties.SetName(TxtHost, L("ToolSnmpHostLabel"));
        System.Windows.Automation.AutomationProperties.SetName(TxtCommunity, L("ToolSnmpCommunity"));
        System.Windows.Automation.AutomationProperties.SetName(TxtOid, L("ToolSnmpOid"));
        System.Windows.Automation.AutomationProperties.SetName(ResultsGrid, L("ToolSnmpTitle"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetSystem, L("ToolSnmpPresetSystem"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetInterfaces, L("ToolSnmpPresetInterfaces"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetIp, L("ToolSnmpPresetIp"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetTcp, L("ToolSnmpPresetTcp"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetUdp, L("ToolSnmpPresetUdp"));

        LblHost.Text = L("ToolSnmpHostLabel");
        LblCommunity.Text = L("ToolSnmpCommunity");
        LblOid.Text = L("ToolSnmpOid");

        BtnCopy.ToolTip = L("ToolBtnCopyToClipboard");
        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        System.Windows.Automation.AutomationProperties.SetName(LoadingBar, L("ToolSnmpA11yLoading"));

        LblRouteVia.Text = L("ToolTunnelRouteVia");
        System.Windows.Automation.AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));

        TxtHost.Tag = L("ToolWatermarkHostnameOrIp");
        TxtCommunity.Tag = L("ToolSnmpCommunity");
        TxtOid.Tag = L("ToolSnmpOid");
        TxtEmptyState.Text = L("ToolSnmpEmptyState");
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpSNMPWALK").Replace("\\n", "\n", StringComparison.Ordinal);
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
            ToggleWalk();
            e.Handled = true;
        }
    }

    private void OnWalkClick(object sender, RoutedEventArgs e)
    {
        ToggleWalk();
    }

    private void ToggleWalk()
    {
        if (_vm.IsWalking)
        {
            StopWalk();
        }
        else
        {
            _ = StartWalkAsync();
        }
    }

    private void StopWalk()
    {
        _vm.CancelWalk();
    }

    private async Task StartWalkAsync()
    {
        _isTestingCommunities = false;
        _results.Clear();
        _communityResults.Clear();

        await _vm.WalkAsync(TxtHost.Text.Trim(), TxtCommunity.Text.Trim(), TxtOid.Text.Trim());
        SyncResults();
        RefreshUiFromVm();
    }

    private void OnTestCommunitiesClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsWalking)
        {
            return;
        }

        _ = StartCommunityTestAsync();
    }

    private async Task StartCommunityTestAsync()
    {
        _isTestingCommunities = true;
        _communityResults.Clear();
        RefreshUiFromVm();

        await _vm.TestCommunitiesAsync(TxtHost.Text.Trim());
        SyncCommunityResults();
        _isTestingCommunities = false;
        RefreshUiFromVm();
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string oid)
        {
            TxtOid.Text = oid;
        }
    }

    private void OnGatewaySelected(SshGatewayDto? gateway)
    {
        _vm.SetGateway(gateway);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_results.Count == 0)
        {
            return;
        }

        try
        {
            var text = _vm.BuildClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            Clipboard.SetText(text);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SNMP Walker clipboard copy failed: {ex.Message}");
            _viewState.ShowError(
                string.Format(L("StatusClipboardCopyFailed"), ex.Message),
                showEmptyState: false,
                keepResultsVisible: true);
        }
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
            FileName = $"snmpwalk_{TxtHost.Text.Trim()}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
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
            Core.Logging.FileLogger.Warn($"SNMP Walker CSV export failed: {ex.Message}");
            _viewState.ShowError(
                string.Format(L("StatusExportFailed"), ex.Message),
                showEmptyState: false,
                keepResultsVisible: true);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SnmpWalkerViewModel.IsWalking)
            or nameof(SnmpWalkerViewModel.StatusText)
            or nameof(SnmpWalkerViewModel.ShowError)
            or nameof(SnmpWalkerViewModel.ErrorText)
            or nameof(SnmpWalkerViewModel.ShowResults))
        {
            RefreshUiFromVm();
        }
    }

    private void RefreshUiFromVm()
    {
        if (_vm.IsWalking)
        {
            _viewState.Begin(_vm.StatusText);
            _viewState.ShowResults(_vm.StatusText);
        }
        else if (_vm.ShowError)
        {
            _viewState.End();
            _viewState.Reset(showEmptyState: _results.Count == 0);
            _viewState.ShowError(
                _vm.ErrorText,
                _vm.StatusText,
                showEmptyState: _results.Count == 0,
                keepResultsVisible: _results.Count > 0);
        }
        else if (_vm.ShowResults)
        {
            _viewState.End();
            _viewState.Reset(showEmptyState: false);
            _viewState.ShowResults(_vm.StatusText);
        }
        else
        {
            _viewState.End();
            _viewState.Reset(showEmptyState: true);
        }

        BtnWalk.Content = _vm.IsWalking && !_isTestingCommunities
            ? L("ToolSnmpBtnStop")
            : L("ToolSnmpBtnWalk");
        BtnWalk.IsEnabled = !_isTestingCommunities;
    }

    private void SyncResults()
    {
        _results.Clear();
        foreach (var entry in _vm.GetEntries())
        {
            _results.Add(entry);
        }
    }

    private void SyncCommunityResults()
    {
        _communityResults.Clear();
        foreach (var result in _vm.GetCommunityResults())
        {
            _communityResults.Add(result);
        }
    }

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsWalking;

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
        TxtHost.KeyDown -= OnHostKeyDown;
        TxtOid.KeyDown -= OnHostKeyDown;
        _vm.Dispose();
        _setBusy?.Invoke(false);
        GC.SuppressFinalize(this);
    }
}
