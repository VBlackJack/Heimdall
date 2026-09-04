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
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// WPF shell for the DNS security checker. Business logic lives in the service, engine, and VM.
/// </summary>
public partial class DnsSecurityView : UserControl, IToolView
{
    private readonly DnsSecurityCheckerViewModel _vm;
    private LocalizationManager? _localizer;
    private bool _disposed;
    private Action<bool>? _setBusy;
    private readonly ToolAsyncStateController _viewState;
    private GatewayRouteSelector? _routeSelector;
    private SshGatewayDto? _selectedGateway;

    public DnsSecurityView()
    {
        _vm = new DnsSecurityCheckerViewModel();
        InitializeComponent();
        DataContext = _vm;

        _viewState = new ToolAsyncStateController(
            isBusy => _setBusy?.Invoke(isBusy),
            LoadingBar,
            TxtError,
            EmptyStatePanel,
            ResultsPanel,
            TxtStatus,
            BtnCheck,
            TxtDomain,
            CmbRouteVia);

        _vm.PropertyChanged += OnVmPropertyChanged;
        RefreshUiFromVm();
    }

    /// <inheritdoc />
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
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

        _setBusy = context?.SetBusyAction;
        _vm.Initialize(localizer);
        ApplyLocalization();

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            _vm.Input = context.TargetHost!;
        }

        if (!string.IsNullOrWhiteSpace(context?.Argument))
        {
            _vm.Input = context.Argument!;
        }

        _routeSelector?.Dispose();
        _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportRouteStatus);
        _vm.SetGateway(_selectedGateway);
        RefreshUiFromVm();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtDomain.Focus();
            TxtDomain.SelectAll();
        });
    }

    public bool CanClose() => !_vm.IsBusy;

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

        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
        }

        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ApplyLocalization()
    {
        _routeSelector?.Relocalize();
        ApplyBusyButtonState();
        BtnCopyReport.ToolTip = L("ToolBtnCopyToClipboard");
        AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));
        AutomationProperties.SetName(LoadingBar, L("ToolDnsSecA11yLoading"));
    }

    private void OnGatewaySelected(SshGatewayDto? gateway)
    {
        _selectedGateway = gateway;
        _vm.SetGateway(gateway);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DnsSecurityCheckerViewModel.IsBusy):
            case nameof(DnsSecurityCheckerViewModel.ShowError):
            case nameof(DnsSecurityCheckerViewModel.ErrorText):
            case nameof(DnsSecurityCheckerViewModel.HasResults):
            case nameof(DnsSecurityCheckerViewModel.StatusText):
                RefreshUiFromVm();
                break;
        }
    }

    private void RefreshUiFromVm()
    {
        ApplyBusyButtonState();

        if (_vm.IsBusy)
        {
            _viewState.Begin(_vm.StatusText);
            return;
        }

        _viewState.End();

        if (_vm.ShowError)
        {
            _viewState.ShowError(
                _vm.ErrorText,
                _vm.StatusText,
                showEmptyState: !_vm.HasResults,
                keepResultsVisible: _vm.HasResults);
            return;
        }

        if (_vm.HasResults)
        {
            _viewState.ShowResults(_vm.StatusText);
            return;
        }

        _viewState.Reset();
    }

    private void ApplyBusyButtonState()
    {
        if (_vm.IsBusy)
        {
            BtnCheck.Content = L("BtnCancel");
            BtnCheck.Style = (Style)FindResource("SecondaryButtonStyle");
            BtnCheck.Command = _vm.CancelCommand;
            AutomationProperties.SetName(BtnCheck, L("BtnCancel"));
            return;
        }

        BtnCheck.Content = L("ToolDnsSecBtnCheck");
        BtnCheck.Style = (Style)FindResource("PrimaryButtonStyle");
        BtnCheck.Command = _vm.CheckCommand;
        AutomationProperties.SetName(BtnCheck, L("ToolDnsSecBtnCheck"));
    }

    private void OnCopyReportClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.CopyReportCommand.CanExecute(null))
        {
            return;
        }

        _vm.CopyReportCommand.Execute(null);
        CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
    }

    private void OnLocaleChanged(string _)
    {
        ApplyLocalization();
    }

    private string L(string key) => _localizer?[key] ?? key;
}
