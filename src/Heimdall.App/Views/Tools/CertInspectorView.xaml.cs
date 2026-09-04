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
/// SSL/TLS certificate inspector that retrieves and displays certificate details
/// for any host:port combination, or scans multiple ports to discover TLS certificates.
/// </summary>
public partial class CertInspectorView : UserControl, IToolView
{
    private sealed class ScanResultDisplayItem
    {
        public int Port { get; init; }
        public string PortLabel { get; init; } = string.Empty;
        public string Subject { get; init; } = string.Empty;
        public string Issuer { get; init; } = string.Empty;
        public string ValidFrom { get; init; } = string.Empty;
        public string ValidTo { get; init; } = string.Empty;
        public string Serial { get; init; } = string.Empty;
        public string Thumbprint { get; init; } = string.Empty;
        public string SigAlgorithm { get; init; } = string.Empty;
        public string KeySizeText { get; init; } = string.Empty;
        public IReadOnlyList<string> Sans { get; init; } = [];
        public string TlsVersion { get; init; } = string.Empty;
        public string HostnameMatchText { get; init; } = string.Empty;
        public Brush HostnameMatchBrush { get; init; } = Brushes.Transparent;
        public string ExpirationText { get; init; } = string.Empty;
        public Brush ExpirationBrush { get; init; } = Brushes.Transparent;
        public IReadOnlyList<CertChainElement> Chain { get; init; } = [];
        public string ChainHeader { get; init; } = string.Empty;

        public string SubjectLabel { get; init; } = string.Empty;
        public string IssuerLabel { get; init; } = string.Empty;
        public string ValidFromLabel { get; init; } = string.Empty;
        public string ValidToLabel { get; init; } = string.Empty;
        public string SerialLabel { get; init; } = string.Empty;
        public string ThumbprintLabel { get; init; } = string.Empty;
        public string SigAlgorithmLabel { get; init; } = string.Empty;
        public string KeySizeLabel { get; init; } = string.Empty;
        public string TlsVersionLabel { get; init; } = string.Empty;
        public string SansLabel { get; init; } = string.Empty;
    }

    private readonly CertInspectorViewModel _vm;
    private LocalizationManager? _localizer;
    private bool _disposed;
    private Action<bool>? _setBusy;
    private GatewayRouteSelector? _routeSelector;

    public CertInspectorView()
    {
        InitializeComponent();
        _vm = new CertInspectorViewModel();
        _vm.PropertyChanged += OnVmPropertyChanged;
        DataContext = _vm;

        TxtHost.KeyDown += OnInputKeyDown;
        TxtPort.KeyDown += OnInputKeyDown;
        TxtCustomPorts.KeyDown += OnInputKeyDown;
        TxtPort.TextChanged += OnPortTextChanged;
    }

    /// <summary>
    /// Initializes the view with optional context and localizer.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _setBusy = context?.SetBusyAction;
        _vm.Initialize(localizer);
        _vm.Host = string.Empty;
        _vm.Port = string.Empty;
        _vm.CustomPorts = string.Empty;
        _vm.SelectedProfile = "quick";

        ApplyLocalization();

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
            _vm.Host = context.TargetHost;

        if (context?.TargetPort is > 0)
            _vm.Port = context.TargetPort.Value.ToString();

        if (!string.IsNullOrWhiteSpace(context?.Argument))
            ParseArgument(context.Argument);

        _routeSelector?.Dispose();
        _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportRouteStatus);
        UpdateMode();
        UpdateProfileButtonStyles();
        RefreshUiFromVm();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtHost.Focus();
            TxtHost.SelectAll();
        });
    }

    private void ParseArgument(string argument)
    {
        var trimmed = argument.Trim();
        var colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(trimmed[(colonIndex + 1)..], out var port) && port is > 0 and <= 65535)
        {
            _vm.Host = trimmed[..colonIndex];
            _vm.Port = port.ToString();
        }
        else
        {
            _vm.Host = trimmed;
        }
    }

    private void OnPortTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMode();
    }

    private void UpdateMode()
    {
        if (ProfileBar is null)
            return;

        ProfileBar.Visibility = _vm.IsScanMode ? Visibility.Visible : Visibility.Collapsed;
        TxtCustomPorts.Visibility = string.Equals(_vm.SelectedProfile, "custom", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_vm.IsChecking)
        {
            BtnCheck.Content = _vm.IsScanMode ? L("ToolCertBtnScan") : L("ToolCertBtnCheck");
            AutomationProperties.SetName(BtnCheck, _vm.IsScanMode ? L("ToolCertBtnScan") : L("ToolCertBtnCheck"));
        }
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolCertTitle");
        LblHost.Text = L("ToolCertHostLabel");
        BtnCheck.Content = _vm.IsScanMode ? L("ToolCertBtnScan") : L("ToolCertBtnCheck");
        LblSubject.Text = L("ToolCertSubject");
        LblIssuer.Text = L("ToolCertIssuer");
        LblValidFrom.Text = L("ToolCertValidFrom");
        LblValidTo.Text = L("ToolCertValidTo");
        LblSerial.Text = L("ToolCertSerial");
        LblThumbprint.Text = L("ToolCertThumbprint");
        LblSigAlg.Text = L("ToolCertSigAlg");
        LblKeySize.Text = L("ToolCertKeySize");
        LblSans.Text = L("ToolCertSans");
        BtnCopy.Content = L("ToolCertBtnCopy");
        LblTlsVersion.Text = L("ToolCertTlsVersion");
        LblChainTitle.Text = L("ToolCertChainTitle");

        AutomationProperties.SetName(BtnCheck, _vm.IsScanMode ? L("ToolCertBtnScan") : L("ToolCertBtnCheck"));
        AutomationProperties.SetName(TxtHost, L("ToolCertHostLabel"));
        AutomationProperties.SetName(TxtPort, L("ToolCertPortLabel"));
        AutomationProperties.SetName(BtnCopy, L("ToolCertBtnCopy"));

        BtnCopy.ToolTip = L("ToolBtnCopyToClipboard");

        LblRouteVia.Text = L("ToolTunnelRouteVia");
        AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));

        TxtHost.Tag = L("ToolWatermarkHostname");
        TxtPort.Tag = L("ToolCertPortWatermark");
        TxtEmptyState.Text = L("ToolCertEmptyState");

        BtnProfileQuick.Content = L("ToolCertScanProfileQuick");
        BtnProfileExtended.Content = L("ToolCertScanProfileExtended");
        BtnProfileCustom.Content = L("ToolCertScanProfileCustom");
        BtnCopyAll.Content = L("ToolCertBtnCopyAll");
        BtnExportCsv.Content = L("ToolCertBtnExport");

        BtnProfileQuick.ToolTip = $"{L("ToolCertScanProfileQuick")} ({NetworkToolPresets.TlsQuickScanPorts.Length} ports)";
        BtnProfileExtended.ToolTip = $"{L("ToolCertScanProfileExtended")} ({NetworkToolPresets.TlsExtendedScanPorts.Length} ports)";
        AutomationProperties.SetName(BtnProfileQuick, L("ToolCertScanProfileQuick"));
        AutomationProperties.SetName(BtnProfileExtended, L("ToolCertScanProfileExtended"));
        AutomationProperties.SetName(BtnProfileCustom, L("ToolCertScanProfileCustom"));
        AutomationProperties.SetName(BtnCopyAll, L("ToolCertBtnCopyAll"));
        AutomationProperties.SetName(BtnExportCsv, L("ToolCertBtnExport"));
        AutomationProperties.SetName(TxtCustomPorts, L("ToolCertScanProfileCustom"));
        AutomationProperties.SetName(ScanProgress, L("ToolCertA11yScanProgress"));
        AutomationProperties.SetName(LoadingBar, L("ToolCertA11yLoading"));

        BtnCopyAll.ToolTip = L("ToolBtnCopyToClipboard");
        BtnExportCsv.ToolTip = L("ToolCertBtnExport");
        TxtCustomPorts.Tag = L("ToolWatermarkPortList");
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpCERT").Replace("\\n", "\n", StringComparison.Ordinal);
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (_vm.IsChecking)
            _vm.CancelCommand.Execute(null);
        else
            _ = _vm.CheckCommand.ExecuteAsync(null);

        e.Handled = true;
    }

    private void OnCheckClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsChecking)
            _vm.CancelCommand.Execute(null);
        else
            _ = _vm.CheckCommand.ExecuteAsync(null);
    }

    private void OnProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string profile)
        {
            _vm.SelectedProfile = profile;
            TxtCustomPorts.Visibility = string.Equals(profile, "custom", StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateProfileButtonStyles();
        }
    }

    private void UpdateProfileButtonStyles()
    {
        if (BtnProfileQuick is null)
            return;

        BtnProfileQuick.Style = (Style)FindResource(
            string.Equals(_vm.SelectedProfile, "quick", StringComparison.Ordinal)
                ? "PrimaryButtonStyle"
                : "SecondaryButtonStyle");
        BtnProfileExtended.Style = (Style)FindResource(
            string.Equals(_vm.SelectedProfile, "extended", StringComparison.Ordinal)
                ? "PrimaryButtonStyle"
                : "SecondaryButtonStyle");
        BtnProfileCustom.Style = (Style)FindResource(
            string.Equals(_vm.SelectedProfile, "custom", StringComparison.Ordinal)
                ? "PrimaryButtonStyle"
                : "SecondaryButtonStyle");
    }

    private void OnGatewaySelected(SshGatewayDto? gateway)
    {
        _vm.SetGateway(gateway);
    }

    private void SetScanInputsEnabled(bool enabled)
    {
        TxtHost.IsReadOnly = !enabled;
        TxtPort.IsReadOnly = !enabled;
        TxtCustomPorts.IsReadOnly = !enabled;
        CmbRouteVia.IsEnabled = enabled;
        BtnProfileQuick.IsEnabled = enabled;
        BtnProfileExtended.IsEnabled = enabled;
        BtnProfileCustom.IsEnabled = enabled;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;

        switch (e.PropertyName)
        {
            case nameof(CertInspectorViewModel.IsChecking):
            case nameof(CertInspectorViewModel.ShowError):
            case nameof(CertInspectorViewModel.ErrorMessage):
            case nameof(CertInspectorViewModel.ShowEmptyState):
            case nameof(CertInspectorViewModel.ShowProgress):
            case nameof(CertInspectorViewModel.ProgressMax):
            case nameof(CertInspectorViewModel.ProgressValue):
            case nameof(CertInspectorViewModel.ProgressPercent):
            case nameof(CertInspectorViewModel.ProgressCount):
            case nameof(CertInspectorViewModel.ShowSingleResult):
            case nameof(CertInspectorViewModel.SingleResult):
            case nameof(CertInspectorViewModel.SingleDetailsText):
            case nameof(CertInspectorViewModel.ShowScanResults):
            case nameof(CertInspectorViewModel.ShowScanNoResults):
            case nameof(CertInspectorViewModel.ShowScanFooter):
            case nameof(CertInspectorViewModel.ScanResults):
            case nameof(CertInspectorViewModel.ScanSummaryText):
            case nameof(CertInspectorViewModel.SelectedProfile):
                RefreshUiFromVm();
                break;
            case nameof(CertInspectorViewModel.Port):
                UpdateMode();
                break;
        }
    }

    private void RefreshUiFromVm()
    {
        _setBusy?.Invoke(_vm.IsChecking);

        UpdateMode();
        UpdateProfileButtonStyles();
        UpdateCheckButtonState();

        TxtError.Text = _vm.ErrorMessage;
        TxtError.Visibility = _vm.ShowError ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = _vm.ShowEmptyState ? Visibility.Visible : Visibility.Collapsed;

        LoadingBar.Visibility = _vm.IsChecking && !_vm.IsScanMode ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressPanel.Visibility = _vm.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.Maximum = _vm.ProgressMax;
        ScanProgress.Value = _vm.ProgressValue;
        TxtProgressPercent.Text = _vm.ProgressPercent;
        TxtProgressCount.Text = _vm.ProgressCount;

        SingleResultPanel.Visibility = _vm.ShowSingleResult ? Visibility.Visible : Visibility.Collapsed;
        ScanResultsPanel.Visibility = _vm.ShowScanResults ? Visibility.Visible : Visibility.Collapsed;
        TxtScanNoResults.Visibility = _vm.ShowScanNoResults ? Visibility.Visible : Visibility.Collapsed;
        ScanFooterPanel.Visibility = _vm.ShowScanFooter ? Visibility.Visible : Visibility.Collapsed;

        if (_vm.ShowSingleResult && _vm.SingleResult is not null)
            DisplaySingleResult(_vm.SingleResult);
        else
            ClearSingleResult();

        if (_vm.ShowScanResults)
            ProjectScanResults();
        else
            ClearScanResults();
    }

    private void UpdateCheckButtonState()
    {
        if (_vm.IsChecking)
        {
            BtnCheck.Content = L("ToolCertBtnStop");
            BtnCheck.Foreground = (Brush)FindResource("ErrorBrush");
            BtnCheck.Style = (Style)FindResource("SecondaryButtonStyle");
            AutomationProperties.SetName(BtnCheck, L("ToolCertBtnStop"));
            SetScanInputsEnabled(false);
            return;
        }

        BtnCheck.Content = _vm.IsScanMode ? L("ToolCertBtnScan") : L("ToolCertBtnCheck");
        BtnCheck.Foreground = (Brush)FindResource("TextPrimaryBrush");
        BtnCheck.Style = (Style)FindResource("PrimaryButtonStyle");
        AutomationProperties.SetName(BtnCheck, _vm.IsScanMode ? L("ToolCertBtnScan") : L("ToolCertBtnCheck"));
        SetScanInputsEnabled(true);
    }

    private void DisplaySingleResult(CertInspectionSummary summary)
    {
        TxtSubject.Text = summary.Subject;
        TxtIssuer.Text = summary.Issuer;
        TxtValidFrom.Text = summary.NotBefore.ToString("yyyy-MM-dd HH:mm:ss UTC");
        TxtValidTo.Text = $"{summary.NotAfter:yyyy-MM-dd HH:mm:ss UTC} ({summary.DaysRemaining} {L("ToolCertDaysRemaining")})";
        TxtSerial.Text = summary.Serial;
        TxtThumbprint.Text = summary.Thumbprint;
        TxtSigAlg.Text = summary.SigAlgorithm;
        TxtKeySize.Text = summary.KeySizeBits > 0 ? string.Format(L("ToolCertKeySizeBits"), summary.KeySizeBits) : "-";
        SansList.ItemsSource = summary.Sans;

        ExpirationBanner.Visibility = Visibility.Visible;
        ExpirationBanner.Background = GetExpirationBrush(summary.ExpirationStatus);
        TxtExpiration.Text = summary.ExpirationStatus switch
        {
            CertExpirationStatus.Expired => L("ToolCertExpired"),
            CertExpirationStatus.Warning => string.Format(L("ToolCertExpiringSoon"), summary.DaysRemaining),
            _ => string.Format(L("ToolCertValid"), summary.DaysRemaining)
        };

        TxtTlsVersion.Text = summary.TlsVersion;
        TxtHostnameMatch.Text = summary.HostnameMatches
            ? "\u2714 " + L("ToolCertHostnameMatch")
            : "\u2716 " + L("ToolCertHostnameMismatch");
        TxtHostnameMatch.Foreground = (Brush)FindResource(summary.HostnameMatches ? "SuccessBrush" : "ErrorBrush");
        TlsHostPanel.Visibility = Visibility.Visible;

        ChainList.ItemsSource = summary.Chain;
        ChainPanel.Visibility = summary.Chain.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        DetailsPanel.Visibility = Visibility.Visible;
        BtnCopy.Visibility = string.IsNullOrEmpty(_vm.SingleDetailsText) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearSingleResult()
    {
        DetailsPanel.Visibility = Visibility.Collapsed;
        ExpirationBanner.Visibility = Visibility.Collapsed;
        TlsHostPanel.Visibility = Visibility.Collapsed;
        ChainPanel.Visibility = Visibility.Collapsed;
        BtnCopy.Visibility = Visibility.Collapsed;
        TxtSubject.Text = string.Empty;
        TxtIssuer.Text = string.Empty;
        TxtValidFrom.Text = string.Empty;
        TxtValidTo.Text = string.Empty;
        TxtSerial.Text = string.Empty;
        TxtThumbprint.Text = string.Empty;
        TxtSigAlg.Text = string.Empty;
        TxtKeySize.Text = string.Empty;
        TxtTlsVersion.Text = string.Empty;
        TxtHostnameMatch.Text = string.Empty;
        SansList.ItemsSource = null;
        ChainList.ItemsSource = null;
    }

    private void ProjectScanResults()
    {
        if (_vm.ScanResults.Count == 0)
        {
            ScanResultsList.ItemsSource = null;
            TxtScanSummary.Text = _vm.ScanSummaryText;
            return;
        }

        var projected = _vm.ScanResults.Select(summary =>
        {
            var serviceLabel = NetworkToolPresets.GetTlsServiceLabel(summary.Port);
            var expirationText = summary.ExpirationStatus switch
            {
                CertExpirationStatus.Expired => L("ToolCertExpired"),
                CertExpirationStatus.Warning => string.Format(L("ToolCertExpiringSoon"), summary.DaysRemaining),
                _ => string.Format(L("ToolCertValid"), summary.DaysRemaining)
            };

            return new ScanResultDisplayItem
            {
                Port = summary.Port,
                PortLabel = string.Format(L("ToolCertScanPortHeader"), summary.Port, serviceLabel),
                Subject = summary.Subject,
                Issuer = summary.Issuer,
                ValidFrom = summary.NotBefore.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                ValidTo = $"{summary.NotAfter:yyyy-MM-dd HH:mm:ss UTC} ({summary.DaysRemaining} {L("ToolCertDaysRemaining")})",
                Serial = summary.Serial,
                Thumbprint = summary.Thumbprint,
                SigAlgorithm = summary.SigAlgorithm,
                KeySizeText = summary.KeySizeBits > 0 ? string.Format(L("ToolCertKeySizeBits"), summary.KeySizeBits) : "-",
                Sans = summary.Sans,
                TlsVersion = summary.TlsVersion,
                HostnameMatchText = summary.HostnameMatches
                    ? "\u2714 " + L("ToolCertHostnameMatch")
                    : "\u2716 " + L("ToolCertHostnameMismatch"),
                HostnameMatchBrush = (Brush)FindResource(summary.HostnameMatches ? "SuccessBrush" : "ErrorBrush"),
                ExpirationText = expirationText,
                ExpirationBrush = GetExpirationBrush(summary.ExpirationStatus),
                Chain = summary.Chain,
                ChainHeader = $"{L("ToolCertChainTitle")} ({summary.Chain.Count})",
                SubjectLabel = L("ToolCertSubject"),
                IssuerLabel = L("ToolCertIssuer"),
                ValidFromLabel = L("ToolCertValidFrom"),
                ValidToLabel = L("ToolCertValidTo"),
                SerialLabel = L("ToolCertSerial"),
                ThumbprintLabel = L("ToolCertThumbprint"),
                SigAlgorithmLabel = L("ToolCertSigAlg"),
                KeySizeLabel = L("ToolCertKeySize"),
                TlsVersionLabel = L("ToolCertTlsVersion"),
                SansLabel = L("ToolCertSans")
            };
        }).ToList();

        ScanResultsList.ItemsSource = projected;
        TxtScanSummary.Text = _vm.ScanSummaryText;
    }

    private void ClearScanResults()
    {
        ScanResultsList.ItemsSource = null;
        TxtScanSummary.Text = string.Empty;
    }

    private Brush GetExpirationBrush(CertExpirationStatus status) => status switch
    {
        CertExpirationStatus.Expired => (Brush)FindResource("ErrorBrush"),
        CertExpirationStatus.Warning => (Brush)FindResource("WarningBrush"),
        _ => (Brush)FindResource("SuccessBrush")
    };

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.SingleDetailsText))
            return;

        try
        {
            Clipboard.SetText(_vm.SingleDetailsText);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (_vm.ScanResults.Count == 0)
            return;

        try
        {
            Clipboard.SetText(_vm.BuildAllDetailsText());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_vm.ScanResults.Count == 0)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("FileDialogCsvFilter"),
            FileName = $"certscan_{CertInspectorEngine.SanitizeFileName(_vm.Host.Trim())}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() != true)
            return;

        File.WriteAllText(dialog.FileName, _vm.BuildCsvExport(), Encoding.UTF8);
        CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
    }

    public bool CanClose() => !_vm.IsChecking;

    private string L(string key) => _localizer?[key] ?? key;

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
            return;

        _disposed = true;
        _routeSelector?.Dispose();
        TxtHost.KeyDown -= OnInputKeyDown;
        TxtPort.KeyDown -= OnInputKeyDown;
        TxtCustomPorts.KeyDown -= OnInputKeyDown;
        TxtPort.TextChanged -= OnPortTextChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _setBusy?.Invoke(false);
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
}
