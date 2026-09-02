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
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

public partial class ServiceStatusView : UserControl, IToolView
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly ServiceStatusViewModel _vm;
    private LocalizationManager? _localizer;
    private Action<bool>? _setBusy;
    private DispatcherTimer? _autoRefreshTimer;
    private bool _disposed;
    private readonly ToolAsyncStateController _viewState;

    public ServiceStatusView()
    {
        _vm = new ServiceStatusViewModel();
        InitializeComponent();
        DataContext = _vm;
        _viewState = new ToolAsyncStateController(
            isBusy => _setBusy?.Invoke(isBusy),
            LoadingBar,
            TxtError,
            EmptyStatePanel,
            ServicesPanel,
            null,
            BtnRefresh,
            TxtFilter,
            ChkRunningOnly,
            ChkAutoRefresh);
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.CopyResultsRequested += OnCopyResultsRequested;
        UpdateStats();
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (_localizer is not null) _localizer.LocaleChanged -= OnLocaleChanged;
        _localizer = localizer;
        _setBusy = context?.SetBusyAction;
        if (_localizer is not null) _localizer.LocaleChanged += OnLocaleChanged;
        _vm.Initialize(localizer);
        ApplyLocalization();
        UpdateViewState();
        _ = _vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => TxtFilter.Focus());
    }

    private void ApplyLocalization()
    {
        AutomationProperties.SetName(TxtFilter, L("ToolServicesFilterTooltip"));
        AutomationProperties.SetName(BtnRefresh, L("ToolServicesBtnRefresh"));
        AutomationProperties.SetName(BtnCopy, L("ToolServicesBtnCopy"));
        AutomationProperties.SetName(ChkRunningOnly, L("ToolServicesRunningOnly"));
        AutomationProperties.SetName(ChkAutoRefresh, L("ToolServicesAutoRefresh"));
        AutomationProperties.SetName(ServicesGrid, L("ToolServicesTitle"));
        BtnHelp.ToolTip = L("ToolHelpTooltip");
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        AutomationProperties.SetName(LoadingBar, L("ToolServicesA11yLoading"));
        UpdateStats();
    }

    private void UpdateStats()
    {
        if (!_vm.HasRefreshSnapshot)
        {
            TxtTotal.Text = TxtRunning.Text = TxtStopped.Text = "-";
            return;
        }
        TxtTotal.Text = _vm.TotalCount.ToString();
        TxtRunning.Text = _vm.RunningCount.ToString();
        TxtStopped.Text = _vm.StoppedCount.ToString();
    }

    private void OnAutoRefreshChanged(object sender, RoutedEventArgs e)
    {
        _autoRefreshTimer ??= new DispatcherTimer { Interval = AutoRefreshInterval };
        _autoRefreshTimer.Tick -= OnAutoRefreshTick;
        _autoRefreshTimer.Tick += OnAutoRefreshTick;
        if (ChkAutoRefresh.IsChecked == true) _autoRefreshTimer.Start();
        else _autoRefreshTimer.Stop();
    }

    private void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        _ = _vm.RefreshCommand.ExecuteAsync(null);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ServiceStatusViewModel.IsBusy):
            case nameof(ServiceStatusViewModel.HasError):
            case nameof(ServiceStatusViewModel.ErrorText):
            case nameof(ServiceStatusViewModel.HasResults):
                UpdateViewState();
                break;
            case nameof(ServiceStatusViewModel.TotalCount):
            case nameof(ServiceStatusViewModel.RunningCount):
            case nameof(ServiceStatusViewModel.StoppedCount):
            case nameof(ServiceStatusViewModel.HasRefreshSnapshot):
                UpdateStats();
                break;
        }
    }

    private void UpdateViewState()
    {
        if (_vm.IsBusy)
        {
            _viewState.Begin();
            return;
        }
        _viewState.End();
        if (_vm.HasError)
        {
            _viewState.ShowError(
                _vm.ErrorText,
                showEmptyState: !_vm.HasResults,
                keepResultsVisible: _vm.HasResults);
            return;
        }
        if (_vm.HasResults) _viewState.ShowResults();
        else _viewState.Reset();
    }

    private void OnCopyResultsRequested(object? sender, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
            CopyFeedbackHelper.ShowCopyFeedback(BtnCopy);
        }
        catch (ExternalException) { }
    }

    private void OnLocaleChanged(string _) { ApplyLocalization(); UpdateStats(); }

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsBusy;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _setBusy?.Invoke(false);
        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localizer = null;
        }
        if (_autoRefreshTimer is not null)
        {
            _autoRefreshTimer.Stop();
            _autoRefreshTimer.Tick -= OnAutoRefreshTick;
        }
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.CopyResultsRequested -= OnCopyResultsRequested;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
}
