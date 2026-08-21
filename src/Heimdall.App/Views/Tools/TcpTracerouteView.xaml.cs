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
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Network;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// TCP traceroute tool view. Keeps only WPF wiring and visual state.
/// </summary>
public partial class TcpTracerouteView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private bool _disposed;
    private List<SshGatewayDto>? _gateways;
    private SshGatewayDto? _selectedGateway;
    private Action<bool>? _setBusy;
    private readonly ToolAsyncStateController _viewState;
    private readonly TracerouteViewModel _vm;

    public TcpTracerouteView()
    {
        _vm = new TracerouteViewModel();
        InitializeComponent();

        _viewState = new ToolAsyncStateController(
            null,
            null,
            TxtError,
            EmptyStatePanel,
            ResultsPanel,
            null);

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Hops.CollectionChanged += OnHopsCollectionChanged;
        ResultsGrid.ItemsSource = _vm.Hops;
        TxtHost.KeyDown += OnHostKeyDown;
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

        TxtHost.Clear();
        TxtMaxHops.Text = TracerouteEngine.DefaultMaxHops.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            TxtHost.Text = context.TargetHost.Trim();
        }

        if (context?.SshGateways is IList gateways)
        {
            _gateways = gateways.Cast<SshGatewayDto>().ToList();
        }

        PopulateRouteSelector();
        _vm.SetGateway(_selectedGateway);

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtHost.Focus();
            TxtHost.SelectAll();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolTraceTitle");
        BtnTrace.Content = L("ToolTraceBtnTrace");
        BtnCopy.Content = L("ToolTraceBtnCopy");
        LblMaxHops.Text = L("ToolTraceMaxHops");

        ColHop.Header = L("ToolTraceColHop");
        ColAddress.Header = L("ToolTraceColAddress");
        ColHostname.Header = L("ToolTraceColHostname");
        ColLatency.Header = L("ToolTraceColLatency");
        ColStatus.Header = L("ToolTraceColStatus");

        System.Windows.Automation.AutomationProperties.SetName(BtnTrace, L("ToolTraceBtnTrace"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopy, L("ToolTraceBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(TxtHost, L("ToolTraceHostLabel"));
        System.Windows.Automation.AutomationProperties.SetName(TxtMaxHops, L("ToolTraceMaxHops"));

        BtnCopy.ToolTip = L("ToolBtnCopyToClipboard");

        LblHost.Text = L("ToolTraceHostLabel");
        LblRouteVia.Text = L("ToolTunnelRouteVia");
        System.Windows.Automation.AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        System.Windows.Automation.AutomationProperties.SetName(TraceProgress, L("ToolTraceA11yProgress"));
        System.Windows.Automation.AutomationProperties.SetName(ResultsGrid, L("ToolTraceTitle"));

        TxtEmptyState.Text = L("ToolTraceEmptyState");
        TxtHost.Tag = L("ToolWatermarkHostnameOrIp");
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
            _selectedGateway = gateway;
        }
        else
        {
            _selectedGateway = null;
        }

        _vm.SetGateway(_selectedGateway);
    }

    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ToggleTrace();
            e.Handled = true;
        }
    }

    private void OnTraceClick(object sender, RoutedEventArgs e)
    {
        ToggleTrace();
    }

    private void ToggleTrace()
    {
        if (_vm.IsTracing)
        {
            StopTrace();
        }
        else
        {
            _ = StartTraceAsync();
        }
    }

    private async Task StartTraceAsync()
    {
        var (inputs, errorKey) = _vm.ValidateInputs(TxtHost.Text, TxtMaxHops.Text);
        _viewState.Reset();
        SetStatus(string.Empty);

        if (errorKey is not null)
        {
            ShowTraceError(L(errorKey));
            return;
        }

        TxtMaxHops.Text = inputs!.MaxHops.ToString(CultureInfo.InvariantCulture);
        _setBusy?.Invoke(true);
        BtnTrace.Content = L("ToolTraceBtnStop");
        BtnTrace.Foreground = (Brush)FindResource("ErrorBrush");
        BtnTrace.Style = (Style)FindResource("SecondaryButtonStyle");
        System.Windows.Automation.AutomationProperties.SetName(BtnTrace, L("ToolTraceBtnStop"));
        TxtHost.IsReadOnly = true;
        TxtMaxHops.IsReadOnly = true;
        CmbRouteVia.IsEnabled = false;
        _viewState.ShowResults();
        TraceProgress.Maximum = inputs.MaxHops;
        TraceProgress.Value = 0;
        TraceProgress.IsIndeterminate = false;
        TxtProgressStatus.Text = L("ToolTraceResolving");
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            await _vm.TraceAsync(inputs);
        }
        finally
        {
            ReleaseTraceUi();
        }
    }

    private void ReleaseTraceUi()
    {
        _setBusy?.Invoke(false);
        BtnTrace.Content = L("ToolTraceBtnTrace");
        BtnTrace.Foreground = (Brush)FindResource("TextPrimaryBrush");
        BtnTrace.Style = (Style)FindResource("PrimaryButtonStyle");
        System.Windows.Automation.AutomationProperties.SetName(BtnTrace, L("ToolTraceBtnTrace"));
        TxtHost.IsReadOnly = false;
        TxtMaxHops.IsReadOnly = false;
        CmbRouteVia.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Collapsed;

        if (_vm.Hops.Count == 0 && TxtError.Visibility != Visibility.Visible)
        {
            _viewState.Reset();
        }
    }

    private void StopTrace()
    {
        _vm.Stop();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TracerouteViewModel.CurrentHop):
            case nameof(TracerouteViewModel.MaxHops):
                TraceProgress.Value = _vm.CurrentHop;
                TraceProgress.Maximum = _vm.MaxHops;
                break;

            case nameof(TracerouteViewModel.ProgressIndeterminate):
                TraceProgress.IsIndeterminate = _vm.ProgressIndeterminate;
                break;

            case nameof(TracerouteViewModel.StatusText):
                TxtProgressStatus.Text = _vm.StatusText;
                TxtStatus.Text = _vm.StatusText;
                break;

            case nameof(TracerouteViewModel.SessionCompleted) when _vm.SessionCompleted:
                SetStatus(string.Format(
                    CultureInfo.InvariantCulture,
                    L("ToolTraceComplete"),
                    _vm.Hops.Count));
                break;

            case nameof(TracerouteViewModel.ShowError) when _vm.ShowError:
                ShowTraceError(_vm.ErrorText, keepResultsVisible: _vm.Hops.Count > 0);
                break;
        }
    }

    private void OnHopsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm.Hops.Count > 0)
        {
            _viewState.ShowResults();
        }
    }

    private void SetStatus(string message, string brushResourceKey = "TextSecondaryBrush")
    {
        TxtStatus.Text = message;
        TxtStatus.Foreground = (Brush)FindResource(brushResourceKey);
    }

    private void ShowTraceError(string message, bool keepResultsVisible = false)
    {
        _viewState.ShowError(
            message,
            showEmptyState: !keepResultsVisible,
            keepResultsVisible: keepResultsVisible);
        SetStatus(message, "ErrorTextBrush");
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Hops.Count == 0)
        {
            return;
        }

        try
        {
            var text = TracerouteEngine.BuildClipboardText([.. _vm.Hops], CreateLocalize());
            Clipboard.SetText(text);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Traceroute clipboard copy failed: {ex.Message}");
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

        TxtHelpContent.Text = L("ToolHelpTCPTRACE").Replace("\\n", "\n", StringComparison.Ordinal);
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private Func<string, string> CreateLocalize() => key => _localizer?[key] ?? key;

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsTracing;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TxtHost.KeyDown -= OnHostKeyDown;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Hops.CollectionChanged -= OnHopsCollectionChanged;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
}
