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
using System.Windows.Input;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// WPF surface for the HTTP header analyzer. Business logic lives in the engine, service, and VM.
/// </summary>
public partial class HttpHeaderAnalyzerView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private bool _disposed;
    private Action<bool>? _setBusy;
    private readonly ToolAsyncStateController _viewState;
    private readonly HttpHeaderAnalyzerViewModel _vm;
    private GatewayRouteSelector? _routeSelector;
    private SshGatewayDto? _selectedGateway;

    public HttpHeaderAnalyzerView()
    {
        _vm = new HttpHeaderAnalyzerViewModel();
        InitializeComponent();

        DataContext = _vm;
        _viewState = new ToolAsyncStateController(
            isBusy => _setBusy?.Invoke(isBusy),
            LoadingBar,
            TxtError,
            EmptyStatePanel,
            ResultsPanel,
            TxtStatus,
            TxtUrl,
            CmbRouteVia);

        _vm.PropertyChanged += OnVmPropertyChanged;
        TxtUrl.KeyDown += OnUrlKeyDown;
        RefreshUiFromVm();
    }

    /// <summary>
    /// Initializes the tool with optional context and localizer.
    /// </summary>
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

        _vm.UrlInput = string.Empty;
        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            var host = context.TargetHost;
            _vm.UrlInput = context.TargetPort is > 0 and not 80 and not 443
                ? $"https://{host}:{context.TargetPort.Value}"
                : $"https://{host}";
        }

        if (!string.IsNullOrWhiteSpace(context?.Argument))
        {
            _vm.UrlInput = context.Argument.Trim();
        }

        _routeSelector?.Dispose();
        _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportRouteStatus);
        _vm.SetGateway(_selectedGateway);
        RefreshUiFromVm();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtUrl.Focus();
            TxtUrl.SelectAll();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolHttpHeadersTitle");
        LblUrl.Text = L("ToolHttpHeadersUrl");
        LblRouteVia.Text = L("ToolTunnelRouteVia");
        TxtEmptyState.Text = L("ToolHttpHeadersEmptyState");
        BtnCopyReport.Content = L("ToolHttpHeadersBtnCopy");
        BtnCopyReport.ToolTip = L("ToolBtnCopyToClipboard");
        TxtGradeLabel.Text = L("ToolHttpHeadersGrade");
        TxtSecuritySection.Text = L("ToolHttpHeadersSectionSecurity");
        TxtDisclosureSection.Text = L("ToolHttpHeadersSectionDisclosure");
        RawHeadersExpander.Header = L("ToolHttpHeadersSectionRaw");

        AutomationProperties.SetName(TxtUrl, L("ToolHttpHeadersUrl"));
        AutomationProperties.SetName(BtnCopyReport, L("ToolHttpHeadersBtnCopy"));
        AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));
        AutomationProperties.SetName(LoadingBar, L("ToolHttpHeadersA11yLoading"));
        AutomationProperties.SetName(RawHeadersExpander, L("ToolHttpHeadersSectionRaw"));
        AutomationProperties.SetName(TxtRawHeaders, L("ToolHttpHeadersSectionRaw"));
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        BtnHelp.ToolTip = L("ToolHelpTooltip");
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        TxtUrl.Tag = L("ToolHttpHeadersUrlPlaceholder");

        ApplyBusyButtonState();
    }

    private void OnGatewaySelected(SshGatewayDto? gateway)
    {
        _selectedGateway = gateway;
        _vm.SetGateway(gateway);
    }

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ToggleCheck();
            e.Handled = true;
        }
    }

    private void OnCheckClick(object sender, RoutedEventArgs e)
    {
        ToggleCheck();
    }

    private void ToggleCheck()
    {
        if (_vm.IsBusy)
        {
            if (_vm.CancelCommand.CanExecute(null))
            {
                _vm.CancelCommand.Execute(null);
            }

            return;
        }

        if (_vm.CheckCommand.CanExecute(null))
        {
            _vm.CheckCommand.Execute(null);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HttpHeaderAnalyzerViewModel.IsBusy):
            case nameof(HttpHeaderAnalyzerViewModel.ShowError):
            case nameof(HttpHeaderAnalyzerViewModel.ErrorText):
            case nameof(HttpHeaderAnalyzerViewModel.HasResults):
            case nameof(HttpHeaderAnalyzerViewModel.StatusText):
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
            BtnCheck.Foreground = (Brush)FindResource("ErrorBrush");
            BtnCheck.Style = (Style)FindResource("SecondaryButtonStyle");
            AutomationProperties.SetName(BtnCheck, L("BtnCancel"));
            return;
        }

        BtnCheck.Content = L("ToolHttpHeadersBtnCheck");
        BtnCheck.Foreground = (Brush)FindResource("TextPrimaryBrush");
        BtnCheck.Style = (Style)FindResource("PrimaryButtonStyle");
        AutomationProperties.SetName(BtnCheck, L("ToolHttpHeadersBtnCheck"));
    }

    private void OnCopyReportClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.ReportText))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_vm.ReportText);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"HttpHeaderAnalyzer clipboard copy failed: {ex.Message}");
            _viewState.ShowError(
                string.Format(L("StatusClipboardCopyFailed"), ex.Message),
                showEmptyState: false,
                keepResultsVisible: true);
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpHTTPHEADERS").Replace("\\n", "\n", StringComparison.Ordinal);
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnLocaleChanged(string _)
    {
        ApplyLocalization();
    }

    private string L(string key) => _localizer?[key] ?? key;

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

        TxtUrl.KeyDown -= OnUrlKeyDown;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
}
