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

using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Scans visible Wi-Fi networks using <c>netsh wlan show networks mode=bssid</c>
/// and displays SSID, BSSID, signal strength, channel, authentication, encryption, and radio type.
/// </summary>
public partial class WifiNetworksView : UserControl, IToolView
{
    private readonly WifiNetworksViewModel _vm;
    private LocalizationManager? _localizer;
    private Action<bool>? _setBusy;
    private bool _disposed;

    public WifiNetworksView()
    {
        _vm = new WifiNetworksViewModel();
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Networks.CollectionChanged += OnNetworksCollectionChanged;
        _vm.CopyResultsRequested += OnCopyResultsRequested;
        UpdateResultsSurface();
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
        _setBusy = context?.SetBusyAction;
        if (_localizer is not null)
        {
            _localizer.LocaleChanged += OnLocaleChanged;
        }

        _vm.Initialize(localizer);
        ApplyLocalization();
        UpdateResultsSurface();
    }

    private void ApplyLocalization()
    {
        TxtEmptyState.Text = L("ToolWifiStatus");
        AutomationProperties.SetName(BtnScan, L("ToolWifiBtnScan"));
        AutomationProperties.SetName(BtnCopy, L("ToolBtnCopyToClipboard"));
        AutomationProperties.SetName(ResultsGrid, L("ToolWifiTitle"));
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WifiNetworksViewModel.IsBusy):
                _setBusy?.Invoke(_vm.IsBusy);
                UpdateResultsSurface();
                break;
            case nameof(WifiNetworksViewModel.HasError):
            case nameof(WifiNetworksViewModel.ErrorText):
                UpdateResultsSurface();
                break;
        }
    }

    private void OnNetworksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateResultsSurface();
    }

    private void UpdateResultsSurface()
    {
        var hasResults = _vm.Networks.Count > 0;
        var hasError = _vm.HasError && !string.IsNullOrWhiteSpace(_vm.ErrorText);

        TxtError.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        ResultsPanel.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasResults || _vm.IsBusy || hasError
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnCopyResultsRequested(object? sender, string text)
    {
        try
        {
            Clipboard.SetText(text);
            CopyFeedbackHelper.ShowCopyFeedback(BtnCopy);
        }
        catch (ExternalException ex)
        {
            Core.Logging.FileLogger.Warn($"WifiNetworks clipboard copy failed: {ex.Message}");
            _vm.ErrorText = string.Format(L("StatusClipboardCopyFailed"), ex.Message);
            _vm.HasError = true;
        }
    }

    private void OnLocaleChanged(string _)
    {
        ApplyLocalization();
        UpdateResultsSurface();
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
        _setBusy?.Invoke(false);

        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localizer = null;
        }

        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Networks.CollectionChanged -= OnNetworksCollectionChanged;
        _vm.CopyResultsRequested -= OnCopyResultsRequested;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
}
