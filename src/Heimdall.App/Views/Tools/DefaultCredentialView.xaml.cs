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
using System.IO;
using System.Text;
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
using Heimdall.Core.Security;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Tests common default credentials against detected services on a target host.
/// </summary>
public partial class DefaultCredentialView : UserControl, IToolView
{
    private sealed class CredResultDisplayItem
    {
        public string Service { get; init; } = string.Empty;

        public int Port { get; init; }

        public string Username { get; init; } = string.Empty;

        public string DisplayPassword { get; init; } = string.Empty;

        public string StatusText { get; init; } = string.Empty;

        public Brush StatusBrush { get; init; } = Brushes.Transparent;

        public string Detail { get; init; } = string.Empty;
    }

    private const string MaskedPassword = "\u2022\u2022\u2022\u2022\u2022\u2022";

    private readonly DefaultCredentialViewModel _vm;
    private LocalizationManager? _localizer;
    private bool _disposed;
    private Action<bool>? _setBusy;
    private GatewayRouteSelector? _routeSelector;

    public DefaultCredentialView()
    {
        _vm = new DefaultCredentialViewModel();
        InitializeComponent();
        _vm.PropertyChanged += OnVmPropertyChanged;
        DataContext = _vm;

        TxtHost.KeyDown += OnInputKeyDown;
        RefreshUiFromVm();
    }

    /// <summary>
    /// Initializes the tool with optional context and localizer.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _setBusy = context?.SetBusyAction;
        _vm.Initialize(localizer);
        _vm.Host = context?.TargetHost ?? string.Empty;
        _vm.AutoDetect = true;
        _vm.ShowPasswords = false;

        ApplyLocalization();

        _routeSelector?.Dispose();
        _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportRouteStatus);
        ChkAutoDetect.IsChecked = true;
        ChkShowPasswords.IsChecked = false;
        RefreshUiFromVm();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtHost.Focus();
            TxtHost.SelectAll();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolDefCredTitle");
        BtnScan.Content = L("ToolDefCredBtnScan");
        BtnCopy.Content = L("ToolDefCredBtnCopy");
        BtnExportCsv.Content = L("ToolDefCredBtnExport");
        TxtWarning.Text = L("ToolDefCredWarning");
        TxtEmptyState.Text = L("ToolDefCredEmptyState");

        ChkAutoDetect.Content = L("ToolDefCredAutoDetect");
        ChkShowPasswords.Content = L("ToolDefCredShowPasswords");

        ColService.Header = L("ToolDefCredColService");
        ColPort.Header = L("ToolDefCredColPort");
        ColUser.Header = L("ToolDefCredColUser");
        ColPass.Header = L("ToolDefCredColPass");
        ColStatus.Header = L("ToolDefCredColStatus");
        ColDetail.Header = L("ToolDefCredColDetail");

        LblRouteVia.Text = L("ToolTunnelRouteVia");

        AutomationProperties.SetName(BtnScan, L("ToolDefCredBtnScan"));
        AutomationProperties.SetName(BtnCopy, L("ToolDefCredBtnCopy"));
        AutomationProperties.SetName(BtnExportCsv, L("ToolDefCredBtnExport"));
        AutomationProperties.SetName(TxtHost, L("ToolDefCredHostLabel"));
        AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));
        AutomationProperties.SetName(ChkAutoDetect, L("ToolDefCredAutoDetect"));
        AutomationProperties.SetName(ChkShowPasswords, L("ToolDefCredShowPasswords"));
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        AutomationProperties.SetName(ScanProgress, L("ToolDefCredA11yProgress"));
        AutomationProperties.SetName(ResultsGrid, L("ToolDefCredTitle"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        BtnCopy.ToolTip = L("ToolBtnCopyToClipboard");
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        TxtHost.Tag = L("ToolWatermarkHostnameOrIp");
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpDEFAULTCREDS").Replace("\\n", "\n", StringComparison.Ordinal);
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (_vm.IsScanning)
        {
            _vm.CancelCommand.Execute(null);
        }
        else
        {
            _ = _vm.ScanCommand.ExecuteAsync(null);
        }

        e.Handled = true;
    }

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsScanning)
        {
            _vm.CancelCommand.Execute(null);
        }
        else
        {
            _ = _vm.ScanCommand.ExecuteAsync(null);
        }
    }

    private void OnGatewaySelected(SshGatewayDto? gateway)
    {
        _vm.SetGateway(gateway);
    }

    private void OnShowPasswordsChanged(object sender, RoutedEventArgs e)
    {
        _vm.ShowPasswords = ChkShowPasswords?.IsChecked == true;
        ProjectResults();
    }

    private void OnAutoDetectChanged(object sender, RoutedEventArgs e)
    {
        _vm.AutoDetect = ChkAutoDetect?.IsChecked == true;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_vm.ScanResults.Count == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_vm.BuildReportText());
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"DefaultCredential scanner clipboard copy failed: {ex.Message}");
            TxtError.Text = string.Format(L("StatusClipboardCopyFailed"), ex.Message);
            TxtError.Visibility = Visibility.Visible;
        }
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_vm.ScanResults.Count == 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("FileDialogCsvFilter"),
            FileName = $"defaultcreds_{_vm.Host.Trim()}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
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
            Core.Logging.FileLogger.Warn($"DefaultCredential scanner CSV export failed: {ex.Message}");
            TxtError.Text = string.Format(L("StatusExportFailed"), ex.Message);
            TxtError.Visibility = Visibility.Visible;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(DefaultCredentialViewModel.IsScanning):
            case nameof(DefaultCredentialViewModel.ShowError):
            case nameof(DefaultCredentialViewModel.ErrorMessage):
            case nameof(DefaultCredentialViewModel.ShowEmptyState):
            case nameof(DefaultCredentialViewModel.ShowProgress):
            case nameof(DefaultCredentialViewModel.ProgressIsIndeterminate):
            case nameof(DefaultCredentialViewModel.ProgressValue):
            case nameof(DefaultCredentialViewModel.ProgressStatus):
            case nameof(DefaultCredentialViewModel.ShowResults):
            case nameof(DefaultCredentialViewModel.ScanResults):
            case nameof(DefaultCredentialViewModel.SummaryText):
            case nameof(DefaultCredentialViewModel.ShowPasswords):
                RefreshUiFromVm();
                break;
        }
    }

    private void RefreshUiFromVm()
    {
        _setBusy?.Invoke(_vm.IsScanning);

        TxtError.Text = _vm.ErrorMessage;
        TxtError.Visibility = _vm.ShowError ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = _vm.ShowEmptyState ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = _vm.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.IsIndeterminate = _vm.ProgressIsIndeterminate;
        ScanProgress.Value = _vm.ProgressValue;
        TxtProgressStatus.Text = _vm.ProgressStatus;
        ResultsBorder.Visibility = _vm.IsScanning || _vm.ShowResults ? Visibility.Visible : Visibility.Collapsed;
        TxtSummary.Text = _vm.SummaryText;

        UpdateScanButtonState();
        SetScanInputsEnabled(!_vm.IsScanning);

        if (_vm.ShowResults)
        {
            ProjectResults();
        }
        else
        {
            ResultsGrid.ItemsSource = null;
        }
    }

    private void UpdateScanButtonState()
    {
        if (_vm.IsScanning)
        {
            BtnScan.Content = L("ToolDefCredBtnStop");
            BtnScan.Foreground = (Brush)FindResource("ErrorBrush");
            BtnScan.Style = (Style)FindResource("SecondaryButtonStyle");
            AutomationProperties.SetName(BtnScan, L("ToolDefCredBtnStop"));
            return;
        }

        BtnScan.Content = L("ToolDefCredBtnScan");
        BtnScan.Foreground = (Brush)FindResource("TextPrimaryBrush");
        BtnScan.Style = (Style)FindResource("PrimaryButtonStyle");
        AutomationProperties.SetName(BtnScan, L("ToolDefCredBtnScan"));
    }

    private void SetScanInputsEnabled(bool enabled)
    {
        TxtHost.IsReadOnly = !enabled;
        CmbRouteVia.IsEnabled = enabled;
        ChkAutoDetect.IsEnabled = enabled;
        ChkShowPasswords.IsEnabled = enabled;
        BtnCopy.IsEnabled = enabled && _vm.ScanResults.Count > 0;
        BtnExportCsv.IsEnabled = enabled && _vm.ScanResults.Count > 0;
    }

    private void ProjectResults()
    {
        if (_vm.ScanResults.Count == 0)
        {
            ResultsGrid.ItemsSource = null;
            return;
        }

        var projected = _vm.ScanResults.Select(dto => new CredResultDisplayItem
        {
            Service = dto.Service,
            Port = dto.Port,
            Username = dto.Username,
            DisplayPassword = _vm.ShowPasswords ? dto.Password : MaskedPassword,
            StatusText = DefaultCredentialEngine.StatusToLabel(dto.Status, L),
            StatusBrush = dto.Status switch
            {
                CredTestStatus.Default => (Brush)FindResource("ErrorBrush"),
                CredTestStatus.Changed => (Brush)FindResource("SuccessBrush"),
                CredTestStatus.Error => (Brush)FindResource("WarningBrush"),
                _ => Brushes.Transparent
            },
            Detail = dto.Status == CredTestStatus.Error
                ? dto.ErrorDetail ?? string.Empty
                : DefaultCredentialEngine.StatusToDetail(dto.Status, dto.Service, L)
        }).ToList();

        ResultsGrid.ItemsSource = projected;
    }

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsScanning;

    private void ReportRouteStatus(string message)
    {
        // Through the ViewModel, which is what the view's own refresh reads back.
        _vm.ShowError = false;
        _vm.ErrorMessage = message;
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
        TxtHost.KeyDown -= OnInputKeyDown;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _setBusy?.Invoke(false);
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
}
