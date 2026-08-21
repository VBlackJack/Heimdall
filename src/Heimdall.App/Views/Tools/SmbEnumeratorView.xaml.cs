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
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// SMB Enumerator WPF surface. Business logic lives in the engine, service, and VM.
/// </summary>
public partial class SmbEnumeratorView : UserControl, IToolView
{
    private readonly SmbEnumeratorViewModel _vm;
    private LocalizationManager? _localizer;
    private Action<bool>? _setBusy;
    private List<SshGatewayDto>? _gateways;
    private readonly ToolAsyncStateController _viewState;
    private bool _disposed;

    public SmbEnumeratorView()
    {
        _vm = new SmbEnumeratorViewModel(new SmbEnumerationService());
        InitializeComponent();
        DataContext = _vm;

        _viewState = new ToolAsyncStateController(
            isBusy => _setBusy?.Invoke(isBusy),
            LoadingBar,
            TxtError,
            EmptyStatePanel,
            ResultsPanel,
            TxtStatus,
            TxtHost,
            CmbRouteVia);

        TxtHost.KeyDown += OnHostKeyDown;
        _vm.PropertyChanged += OnVmPropertyChanged;
        RefreshUiFromVm();
    }

    /// <summary>
    /// Initializes the tool with optional context and localizer.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _setBusy = context?.SetBusyAction;
        _vm.UpdateLocalizer(localizer);
        ApplyLocalization();

        _vm.HostInput = string.Empty;
        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            _vm.HostInput = context.TargetHost!;
        }

        if (context?.SshGateways is IList gateways)
        {
            _gateways = gateways.Cast<SshGatewayDto>().ToList();
        }

        PopulateRouteSelector();
        RefreshUiFromVm();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtHost.Focus();
            TxtHost.SelectAll();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolSmbTitle");
        LblHost.Text = L("ToolSmbHostLabel");
        BtnCopy.Content = L("ToolSmbBtnCopy");
        BtnCopy.ToolTip = L("ToolBtnCopyToClipboard");
        TxtCardIdentity.Text = L("ToolSmbCardIdentity");
        TxtCardProtocol.Text = L("ToolSmbCardProtocol");
        TxtCardFindings.Text = L("ToolSmbFindings");
        LblComputerName.Text = L("ToolSmbComputerName");
        LblDomain.Text = L("ToolSmbDomain");
        LblDnsName.Text = L("ToolSmbDnsName");
        LblDnsDomain.Text = L("ToolSmbDnsDomain");
        LblForest.Text = L("ToolSmbForest");
        LblOsBuild.Text = L("ToolSmbOsBuild");
        LblMac.Text = L("ToolSmbMac");
        LblDialect.Text = L("ToolSmbDialect");
        LblSigning.Text = L("ToolSmbSigning");
        LblServerGuid.Text = L("ToolSmbServerGuid");
        LblSystemTime.Text = L("ToolSmbSystemTime");
        LblBootTime.Text = L("ToolSmbBootTime");
        LblRouteVia.Text = L("ToolTunnelRouteVia");
        TxtHost.Tag = L("ToolWatermarkHostnameOrIp");
        TxtEmptyState.Text = L("ToolSmbEmptyState");
        TxtStatus.Text = string.Empty;

        AutomationProperties.SetName(TxtHost, L("ToolSmbHostLabel"));
        AutomationProperties.SetName(BtnCopy, L("ToolSmbBtnCopy"));
        AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        AutomationProperties.SetName(LoadingBar, L("ToolSmbA11yLoading"));
        AutomationProperties.SetName(ValComputerName, L("ToolSmbComputerName"));
        AutomationProperties.SetName(ValDomain, L("ToolSmbDomain"));
        AutomationProperties.SetName(ValDnsName, L("ToolSmbDnsName"));
        AutomationProperties.SetName(ValDnsDomain, L("ToolSmbDnsDomain"));
        AutomationProperties.SetName(ValForest, L("ToolSmbForest"));
        AutomationProperties.SetName(ValOsBuild, L("ToolSmbOsBuild"));
        AutomationProperties.SetName(ValMac, L("ToolSmbMac"));
        AutomationProperties.SetName(ValDialect, L("ToolSmbDialect"));
        AutomationProperties.SetName(ValSigning, L("ToolSmbSigning"));
        AutomationProperties.SetName(ValServerGuid, L("ToolSmbServerGuid"));
        AutomationProperties.SetName(ValSystemTime, L("ToolSmbSystemTime"));
        AutomationProperties.SetName(ValBootTime, L("ToolSmbBootTime"));
        BtnHelp.ToolTip = L("ToolHelpTooltip");

        ApplyBusyButtonState();
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
        _vm.SetGateway(null);
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

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpSMBENUM").Replace("\\n", "\n", StringComparison.Ordinal);
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
            ToggleEnumeration();
            e.Handled = true;
        }
    }

    private void OnEnumerateClick(object sender, RoutedEventArgs e)
    {
        ToggleEnumeration();
    }

    private void ToggleEnumeration()
    {
        if (_vm.IsBusy)
        {
            if (_vm.CancelCommand.CanExecute(null))
            {
                _vm.CancelCommand.Execute(null);
            }

            return;
        }

        if (_vm.EnumerateCommand.CanExecute(null))
        {
            _vm.EnumerateCommand.Execute(null);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SmbEnumeratorViewModel.IsBusy):
            case nameof(SmbEnumeratorViewModel.HasError):
            case nameof(SmbEnumeratorViewModel.ErrorMessage):
            case nameof(SmbEnumeratorViewModel.HasResults):
            case nameof(SmbEnumeratorViewModel.StatusMessage):
                RefreshUiFromVm();
                break;
        }
    }

    private void RefreshUiFromVm()
    {
        ApplyBusyButtonState();

        if (_vm.IsBusy)
        {
            _viewState.Begin(_vm.StatusMessage);
            return;
        }

        _viewState.End();

        if (_vm.HasError)
        {
            _viewState.ShowError(
                _vm.ErrorMessage,
                _vm.StatusMessage,
                showEmptyState: !_vm.HasResults,
                keepResultsVisible: _vm.HasResults);
            return;
        }

        if (_vm.HasResults)
        {
            _viewState.ShowResults(_vm.StatusMessage);
            return;
        }

        _viewState.Reset();
    }

    private void ApplyBusyButtonState()
    {
        if (_vm.IsBusy)
        {
            BtnEnumerate.Content = L("BtnCancel");
            BtnEnumerate.Foreground = (Brush)FindResource("ErrorBrush");
            BtnEnumerate.Style = (Style)FindResource("SecondaryButtonStyle");
            AutomationProperties.SetName(BtnEnumerate, L("BtnCancel"));
            return;
        }

        BtnEnumerate.Content = L("ToolSmbBtnEnum");
        BtnEnumerate.Foreground = (Brush)FindResource("TextPrimaryBrush");
        BtnEnumerate.Style = (Style)FindResource("PrimaryButtonStyle");
        AutomationProperties.SetName(BtnEnumerate, L("ToolSmbBtnEnum"));
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.LastReport))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_vm.LastReport);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SmbEnumerator clipboard copy failed: {ex.Message}");
            _viewState.ShowError(
                string.Format(L("StatusClipboardCopyFailed"), ex.Message),
                showEmptyState: false,
                keepResultsVisible: true);
        }
    }

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsBusy;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Dispose();
        _setBusy?.Invoke(false);
        GC.SuppressFinalize(this);
    }
}
