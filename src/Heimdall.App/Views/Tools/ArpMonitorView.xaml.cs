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
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Monitors the local ARP table for changes and anomalies (new hosts, MAC changes, disappearances).
/// </summary>
public partial class ArpMonitorView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private Action<bool>? _setBusy;
    private bool _disposed;
    private readonly ToolAsyncStateController _viewState;
    private readonly ArpMonitorViewModel _vm;

    public ArpMonitorView()
    {
        InitializeComponent();
        var services = (Application.Current as App)?.Services;
        var reader = services?.GetService<IArpTableReader>()
            ?? new DefaultArpTableReader();
        var uiDispatcher = services?.GetRequiredService<IUiDispatcher>()
            ?? throw new InvalidOperationException("IUiDispatcher is not registered.");
        _vm = new ArpMonitorViewModel(uiDispatcher, reader);
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.CopyResultsRequested += OnCopyResultsRequested;
        DataContext = _vm;
        _viewState = new ToolAsyncStateController(
            null,
            LoadingBar,
            TxtError,
            EmptyStatePanel,
            GridPanel,
            null);
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && !e.Handled)
            {
                OnToggleClick(s, e);
                e.Handled = true;
            }
        };
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
        SyncViewShell();
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolArpTitle");
        BtnScanNow.Content = L("ToolArpBtnScanNow");
        LblInterval.Text = L("ToolArpInterval");
        BtnCopy.Content = L("ToolArpBtnCopy");

        Interval5s.Content = L("ToolArpInterval5");
        Interval10s.Content = L("ToolArpInterval10");
        Interval30s.Content = L("ToolArpInterval30");
        Interval60s.Content = L("ToolArpInterval60");

        ColIp.Header = L("ToolArpColIp");
        ColMac.Header = L("ToolArpColMac");
        ColVendor.Header = L("ToolArpColVendor");
        ColStatus.Header = L("ToolArpColStatus");
        ColFirstSeen.Header = L("ToolArpColFirstSeen");
        ColLastSeen.Header = L("ToolArpColLastSeen");

        AutomationProperties.SetName(BtnScanNow, L("ToolArpBtnScanNow"));
        AutomationProperties.SetName(BtnCopy, L("ToolArpBtnCopy"));
        AutomationProperties.SetName(CmbInterval, L("ToolArpInterval"));
        BtnHelp.ToolTip = L("ToolHelpTooltip");
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        AutomationProperties.SetName(ArpGrid, L("ToolArpTitle"));
        AutomationProperties.SetName(BtnDismissAlert, L("ToolArpDismissAlert"));
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        AutomationProperties.SetName(LoadingBar, L("ToolArpA11yLoading"));
        BtnCopy.ToolTip = L("ToolBtnCopyToClipboard");
        UpdateMonitoringUi();
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsRunning)
        {
            _vm.Stop();
        }
        else
        {
            _ = _vm.StartAsync(GetSelectedIntervalMs());
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpARPMON").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.PropertyName is nameof(ArpMonitorViewModel.IsRunning) or nameof(ArpMonitorViewModel.IsRefreshing))
        {
            UpdateMonitoringUi();
        }

        if (e.PropertyName is nameof(ArpMonitorViewModel.IsRefreshing)
            or nameof(ArpMonitorViewModel.HasError)
            or nameof(ArpMonitorViewModel.HasResults))
        {
            SyncViewShell();
        }
    }

    private void UpdateMonitoringUi()
    {
        var toggleKey = _vm.IsRunning ? "ToolArpBtnStop" : "ToolArpBtnStart";
        BtnToggle.Content = L(toggleKey);
        BtnToggle.Foreground = (Brush)FindResource(_vm.IsRunning ? "ErrorBrush" : "TextPrimaryBrush");
        BtnToggle.Style = (Style)FindResource(_vm.IsRunning ? "SecondaryButtonStyle" : "PrimaryButtonStyle");
        AutomationProperties.SetName(BtnToggle, L(toggleKey));

        BtnScanNow.IsEnabled = _vm.IsRunning && !_vm.IsRefreshing;
        CmbInterval.IsEnabled = !_vm.IsRunning;
        _setBusy?.Invoke(_vm.IsRunning);
    }

    private void SyncViewShell()
    {
        if (_vm.IsRefreshing)
        {
            _viewState.Begin();
            return;
        }

        _viewState.End();

        if (_vm.HasError)
        {
            _viewState.ShowError(
                _vm.ErrorMessage,
                showEmptyState: !_vm.HasResults,
                keepResultsVisible: _vm.HasResults);
            return;
        }

        if (_vm.HasResults)
        {
            _viewState.ShowResults();
        }
        else
        {
            _viewState.Reset();
        }
    }

    /// <summary>
    /// Returns the selected refresh interval in milliseconds from the ComboBox.
    /// </summary>
    private int GetSelectedIntervalMs()
    {
        if (CmbInterval.SelectedItem is ComboBoxItem item && item.Tag is string tagStr &&
            int.TryParse(tagStr, out var ms))
        {
            return ms;
        }

        return 10000;
    }

    private void OnCopyResultsRequested(object? sender, string text)
    {
        try
        {
            Clipboard.SetText(text);
            CopyFeedbackHelper.ShowCopyFeedback(BtnCopy);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"ArpMonitor clipboard copy failed: {ex.Message}");
            _viewState.ShowError(
                string.Format(L("StatusClipboardCopyFailed"), ex.Message),
                showEmptyState: false,
                keepResultsVisible: true);
        }
    }

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsRunning;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.CopyResultsRequested -= OnCopyResultsRequested;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
}
