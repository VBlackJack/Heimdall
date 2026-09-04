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
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// SSL/TLS Auditor tool that evaluates a server's TLS configuration by testing
/// protocol version support, enumerating cipher suites, and grading the results.
/// </summary>
public partial class TlsAuditView : UserControl, IToolView
{
    public sealed class ProtocolDisplayItem
    {
        public string Name { get; init; } = string.Empty;
        public Brush RatingBrush { get; init; } = Brushes.Transparent;
        public string Icon { get; init; } = string.Empty;
        public string StatusText { get; init; } = string.Empty;
    }

    public sealed class CipherDisplayItem
    {
        public string Name { get; init; } = string.Empty;
        public string Strength { get; init; } = string.Empty;
        public Brush StrengthBrush { get; init; } = Brushes.Transparent;
        public string KeyExchange { get; init; } = string.Empty;
        public string Authentication { get; init; } = string.Empty;
        public string Encryption { get; init; } = string.Empty;
    }

    public sealed class FindingDisplayItem
    {
        public string Icon { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public Brush Brush { get; init; } = Brushes.Transparent;
    }

    private readonly TlsAuditViewModel _vm;
    private LocalizationManager? _localizer;
    private bool _disposed;
    private Action<bool>? _setBusy;
    private GatewayRouteSelector? _routeSelector;

    public TlsAuditView()
    {
        InitializeComponent();
        _vm = new TlsAuditViewModel();
        _vm.PropertyChanged += OnVmPropertyChanged;
        DataContext = _vm;

        TxtHost.KeyDown += OnInputKeyDown;
        TxtPort.KeyDown += OnInputKeyDown;
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _setBusy = context?.SetBusyAction;
        _vm.Initialize(localizer);
        ApplyLocalization();

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
            _vm.Host = context.TargetHost;

        if (context?.TargetPort is > 0)
            _vm.Port = context.TargetPort.Value.ToString();

        if (!string.IsNullOrWhiteSpace(context?.Argument))
            ParseArgument(context.Argument);

        _routeSelector?.Dispose();
        _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportRouteStatus);
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

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolTlsAuditTitle");
        LblHost.Text = L("ToolTlsAuditHostLabel");
        BtnAudit.Content = L("ToolTlsAuditBtnAudit");
        LblProtocols.Text = L("ToolTlsAuditProtocols");
        LblCiphers.Text = L("ToolTlsAuditCiphers");
        LblCertSection.Text = L("ToolTlsAuditCertSection");
        LblFindings.Text = L("ToolTlsAuditFindings");
        BtnCopy.Content = L("ToolTlsAuditBtnCopy");
        TxtEmptyState.Text = L("ToolTlsAuditEmptyState");
        LblGradeCaption.Text = L("ToolTlsAuditGrade");

        LblCertSubject.Text = L("ToolCertSubject");
        LblCertIssuer.Text = L("ToolCertIssuer");
        LblCertExpiry.Text = L("ToolCertValidTo");
        LblCertKeySize.Text = L("ToolCertKeySize");

        LblRouteVia.Text = L("ToolTunnelRouteVia");
        AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));

        AutomationProperties.SetName(BtnAudit, L("ToolTlsAuditBtnAudit"));
        AutomationProperties.SetName(TxtHost, L("ToolTlsAuditHostLabel"));
        AutomationProperties.SetName(TxtPort, L("ToolTlsAuditPortLabel"));
        AutomationProperties.SetName(BtnCopy, L("ToolTlsAuditBtnCopy"));
        AutomationProperties.SetName(AuditProgress, L("ToolTlsAuditA11yProgress"));
        AutomationProperties.SetName(TxtGrade, L("ToolTlsAuditGrade"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        BtnCopy.ToolTip = L("ToolBtnCopyToClipboard");

        TxtHost.Tag = L("ToolWatermarkHostname");
        TxtPort.Tag = L("ToolTlsAuditPortLabel");
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpTLSAUDIT").Replace("\\n", "\n", StringComparison.Ordinal);
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

        if (_vm.IsAuditing)
            _vm.CancelCommand.Execute(null);
        else
            _ = _vm.RunAuditCommand.ExecuteAsync(null);

        e.Handled = true;
    }

    private void OnAuditClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsAuditing)
            _vm.CancelCommand.Execute(null);
        else
            _ = _vm.RunAuditCommand.ExecuteAsync(null);
    }

    private void OnGatewaySelected(SshGatewayDto? gateway)
    {
        _vm.SetGateway(gateway);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;

        switch (e.PropertyName)
        {
            case nameof(TlsAuditViewModel.IsAuditing):
            case nameof(TlsAuditViewModel.ShowProgress):
            case nameof(TlsAuditViewModel.ProgressPercent):
            case nameof(TlsAuditViewModel.ProgressText):
            case nameof(TlsAuditViewModel.ShowError):
            case nameof(TlsAuditViewModel.ErrorMessage):
            case nameof(TlsAuditViewModel.ShowEmptyState):
            case nameof(TlsAuditViewModel.ShowResults):
            case nameof(TlsAuditViewModel.ShowCipherPanel):
            case nameof(TlsAuditViewModel.ShowCertPanel):
            case nameof(TlsAuditViewModel.ShowFindingsPanel):
            case nameof(TlsAuditViewModel.Protocols):
            case nameof(TlsAuditViewModel.Ciphers):
            case nameof(TlsAuditViewModel.Findings):
            case nameof(TlsAuditViewModel.CertificateSummary):
            case nameof(TlsAuditViewModel.Grade):
            case nameof(TlsAuditViewModel.ReportText):
                RefreshUiFromVm();
                break;
        }
    }

    private void RefreshUiFromVm()
    {
        _setBusy?.Invoke(_vm.IsAuditing);

        ProgressPanel.Visibility = _vm.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        AuditProgress.Value = _vm.ProgressPercent;
        TxtProgressStatus.Text = _vm.ProgressText;

        TxtError.Text = _vm.ErrorMessage;
        TxtError.Visibility = _vm.ShowError ? Visibility.Visible : Visibility.Collapsed;

        EmptyStatePanel.Visibility = _vm.ShowEmptyState ? Visibility.Visible : Visibility.Collapsed;
        ResultsPanel.Visibility = _vm.ShowResults ? Visibility.Visible : Visibility.Collapsed;
        CipherPanel.Visibility = _vm.ShowCipherPanel ? Visibility.Visible : Visibility.Collapsed;
        CertPanel.Visibility = _vm.ShowCertPanel ? Visibility.Visible : Visibility.Collapsed;
        FindingsPanel.Visibility = _vm.ShowFindingsPanel ? Visibility.Visible : Visibility.Collapsed;
        BtnCopy.Visibility = string.IsNullOrEmpty(_vm.ReportText) ? Visibility.Collapsed : Visibility.Visible;

        UpdateAuditButtonState();

        if (_vm.ShowResults)
            ProjectResults();
        else
            ClearProjectedResults();
    }

    private void UpdateAuditButtonState()
    {
        if (_vm.IsAuditing)
        {
            BtnAudit.Content = L("ToolCertBtnStop");
            BtnAudit.Foreground = (Brush)FindResource("ErrorBrush");
            BtnAudit.Style = (Style)FindResource("SecondaryButtonStyle");
            AutomationProperties.SetName(BtnAudit, L("ToolCertBtnStop"));
            TxtHost.IsReadOnly = true;
            TxtPort.IsReadOnly = true;
            CmbRouteVia.IsEnabled = false;
            return;
        }

        BtnAudit.Content = L("ToolTlsAuditBtnAudit");
        BtnAudit.Foreground = (Brush)FindResource("TextPrimaryBrush");
        BtnAudit.Style = (Style)FindResource("PrimaryButtonStyle");
        AutomationProperties.SetName(BtnAudit, L("ToolTlsAuditBtnAudit"));
        TxtHost.IsReadOnly = false;
        TxtPort.IsReadOnly = false;
        CmbRouteVia.IsEnabled = true;
    }

    private void ProjectResults()
    {
        TxtGrade.Text = TlsAuditEngine.GradeToString(_vm.Grade);
        GradeBadge.Background = GetGradeBrush(_vm.Grade);

        ProtocolList.ItemsSource = _vm.Protocols.Select(protocol => new ProtocolDisplayItem
        {
            Name = protocol.Name,
            RatingBrush = (Brush)FindResource(GetProtocolRatingBrushKey(protocol.Supported, protocol.Rating)),
            Icon = protocol.Supported ? "\u2714" : "\u2716",
            StatusText = protocol.Supported ? L("ToolTlsAuditSupported") : L("ToolTlsAuditNotSupported")
        }).ToList();

        CipherList.ItemsSource = _vm.Ciphers.Select(cipher => new CipherDisplayItem
        {
            Name = cipher.SuiteName,
            Strength = cipher.Strength switch
            {
                CipherStrength.Strong => L("ToolTlsAuditStrong"),
                CipherStrength.Acceptable => L("ToolTlsAuditAcceptable"),
                _ => L("ToolTlsAuditWeak")
            },
            StrengthBrush = (Brush)FindResource(GetCipherStrengthBrushKey(cipher.Strength)),
            KeyExchange = cipher.KeyExchange,
            Authentication = cipher.Authentication,
            Encryption = cipher.Encryption
        }).ToList();

        FindingsList.ItemsSource = _vm.Findings.Select(finding => new FindingDisplayItem
        {
            Icon = finding.Severity switch
            {
                TlsFindingSeverity.Pass => "\u2714",
                TlsFindingSeverity.Info => "\u2139",
                TlsFindingSeverity.Warning => "\u26A0",
                _ => "\u26A0"
            },
            Message = finding.Message,
            Brush = (Brush)FindResource(GetFindingBrushKey(finding.Severity))
        }).ToList();

        if (_vm.CertificateSummary is not null)
        {
            var daysRemaining = (_vm.CertificateSummary.NotAfter - DateTime.UtcNow).Days;
            TxtCertSubject.Text = _vm.CertificateSummary.Subject;
            TxtCertIssuer.Text = _vm.CertificateSummary.Issuer;
            TxtCertExpiry.Text = $"{_vm.CertificateSummary.NotAfter:yyyy-MM-dd HH:mm:ss UTC} ({daysRemaining}d)";
            TxtCertKeySize.Text = _vm.CertificateSummary.KeySizeBits > 0
                ? string.Format(L("ToolTlsAuditBits"), _vm.CertificateSummary.KeySizeBits)
                : "-";
        }
        else
        {
            TxtCertSubject.Text = string.Empty;
            TxtCertIssuer.Text = string.Empty;
            TxtCertExpiry.Text = string.Empty;
            TxtCertKeySize.Text = string.Empty;
        }
    }

    private void ClearProjectedResults()
    {
        ProtocolList.ItemsSource = null;
        CipherList.ItemsSource = null;
        FindingsList.ItemsSource = null;
        TxtGrade.Text = string.Empty;
        TxtCertSubject.Text = string.Empty;
        TxtCertIssuer.Text = string.Empty;
        TxtCertExpiry.Text = string.Empty;
        TxtCertKeySize.Text = string.Empty;
    }

    private static string GetProtocolRatingBrushKey(bool supported, TlsProtocolRating rating)
    {
        if (!supported)
            return "TextDisabledBrush";

        return rating switch
        {
            TlsProtocolRating.Critical => "ErrorBrush",
            TlsProtocolRating.Weak => "WarningBrush",
            TlsProtocolRating.Strong => "SuccessBrush",
            _ => "TextPrimaryBrush"
        };
    }

    private static string GetCipherStrengthBrushKey(CipherStrength strength) => strength switch
    {
        CipherStrength.Strong => "SuccessBrush",
        CipherStrength.Acceptable => "WarningBrush",
        _ => "ErrorBrush"
    };

    private static string GetFindingBrushKey(TlsFindingSeverity severity) => severity switch
    {
        TlsFindingSeverity.Pass => "SuccessBrush",
        TlsFindingSeverity.Info => "AccentBrush",
        TlsFindingSeverity.Warning => "WarningBrush",
        _ => "ErrorBrush"
    };

    private Brush GetGradeBrush(TlsGrade grade) => grade switch
    {
        TlsGrade.APlus or TlsGrade.A => (Brush)FindResource("SuccessBrush"),
        TlsGrade.B => (Brush)FindResource("AccentBrush"),
        TlsGrade.C => (Brush)FindResource("WarningBrush"),
        _ => (Brush)FindResource("ErrorBrush")
    };

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.ReportText))
            return;

        try
        {
            Clipboard.SetText(_vm.ReportText);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
    }

    public bool CanClose() => !_vm.IsAuditing;

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
        _setBusy?.Invoke(false);
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
}
