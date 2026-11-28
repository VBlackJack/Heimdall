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
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// SSH Server Cryptographic Auditor tool (<c>SSHSERVERAUDIT</c>).
/// Probes a target SSH server's <c>SSH_MSG_KEXINIT</c> algorithms without authenticating,
/// grades ANSSI/SecNumCloud and post-quantum readiness, and exports HTML/CSV reports.
/// </summary>
public partial class SshServerAuditView : UserControl, IToolView
{
    public sealed class AlgorithmDisplayItem
    {
        public string Category { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Rating { get; init; } = string.Empty;
        public string? Note { get; init; }
        public Brush Brush { get; init; } = Brushes.Transparent;
    }

    public sealed class FindingDisplayItem
    {
        public string Icon { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public Brush Brush { get; init; } = Brushes.Transparent;
    }

    public sealed class FleetRowDisplayItem
    {
        public SshFleetServerAuditEntry Entry { get; init; } = null!;
        public string DisplayName { get; init; } = string.Empty;
        public string Endpoint { get; init; } = string.Empty;
        public string Gateway { get; init; } = string.Empty;
        public string Grade { get; init; } = string.Empty;
        public string Anssi { get; init; } = string.Empty;
        public string PostQuantum { get; init; } = string.Empty;
        public string StrictKex { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
    }

    private readonly SshServerAuditViewModel _vm;
    private LocalizationManager? _localizer;
    private HeimdallThemeService? _themeService;
    private GatewayRouteSelector? _routeSelector;
    private Action<bool>? _setBusy;
    private bool _disposed;

    public SshServerAuditView()
    {
        InitializeComponent();
        _vm = new SshServerAuditViewModel();
        _vm.PropertyChanged += OnVmPropertyChanged;
        DataContext = _vm;

        TxtHost.KeyDown += OnInputKeyDown;
        TxtPort.KeyDown += OnInputKeyDown;
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (_localizer is not null) _localizer.LocaleChanged -= OnLocaleChanged;
        _localizer = localizer;
        if (_localizer is not null) _localizer.LocaleChanged += OnLocaleChanged;

        if (_themeService is not null) _themeService.ThemeChanged -= OnThemeChanged;
        _themeService = (Application.Current as App)?.Services?.GetService<HeimdallThemeService>();
        if (_themeService is not null) _themeService.ThemeChanged += OnThemeChanged;

        _setBusy = context?.SetBusyAction;
        _vm.Initialize(localizer);
        ApplyLocalization();

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            _vm.Host = context.TargetHost;
        }

        if (context?.TargetPort is > 0)
        {
            _vm.Port = context.TargetPort.Value.ToString();
        }

        if (!string.IsNullOrWhiteSpace(context?.Argument))
        {
            ParseArgument(context.Argument);
        }

        _routeSelector?.Dispose();
        _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportStatusText);
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
        if (System.Net.IPAddress.TryParse(trimmed, out _))
        {
            _vm.Host = trimmed;
            return;
        }

        if (trimmed.StartsWith('['))
        {
            int closeBracket = trimmed.IndexOf(']');
            if (closeBracket > 1)
            {
                _vm.Host = trimmed[1..closeBracket];
                if (closeBracket + 1 < trimmed.Length && trimmed[closeBracket + 1] == ':'
                    && int.TryParse(trimmed[(closeBracket + 2)..], out int bracketPort)
                    && bracketPort is > 0 and <= 65535)
                {
                    _vm.Port = bracketPort.ToString();
                }

                return;
            }
        }

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

    private static string SanitizeHostForFileName(string host)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(host.Length);
        foreach (char c in host)
        {
            sb.Append(c == ':' || Array.IndexOf(invalid, c) >= 0 ? '-' : c);
        }

        return sb.Length > 0 ? sb.ToString() : "host";
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolSshServerAuditTitle");
        LblHost.Text = L("ToolSshServerAuditHostLabel");
        LblRouteVia.Text = L("ToolTunnelRouteVia");
        TxtEmptyState.Text = L("ToolSshServerAuditEmptyState");
        LblGradeCaption.Text = L("ToolTlsAuditGrade");
        LblFindings.Text = L("ToolTlsAuditFindings");
        LblAlgorithms.Text = L("ToolSshServerAuditAlgorithms");
        LblRecommendation.Text = L("ToolSshServerAuditRecommendation");
        BtnCopy.Content = L("ToolTlsAuditBtnCopy");
        BtnExportHtml.Content = L("ToolSshServerAuditBtnExportHtml");
        BtnExportCsv.Content = L("ToolSshServerAuditBtnExportCsv");
        BtnAuditFleet.Content = L("ToolSshServerAuditBtnAuditFleet");
        LblFleetTitle.Text = L("ToolSshServerAuditFleetTitle");
        BtnExportFleetHtml.Content = L("ToolSshServerAuditFleetBtnExportHtml");
        BtnExportFleetCsv.Content = L("ToolSshServerAuditFleetBtnExportCsv");
        ColFleetName.Header = L("ToolSshServerAuditFleetColName");
        ColFleetEndpoint.Header = L("ToolSshServerAuditFleetColEndpoint");
        ColFleetGateway.Header = L("ToolSshServerAuditFleetColGateway");
        ColFleetGrade.Header = L("ToolSshServerAuditFleetColGrade");
        ColFleetAnssi.Header = L("ToolSshServerAuditFleetColAnssi");
        ColFleetPq.Header = L("ToolSshServerAuditFleetColPq");
        ColFleetStrictKex.Header = L("ToolSshServerAuditFleetColStrictKex");
        ColFleetStatus.Header = L("ToolSshServerAuditFleetColStatus");

        TxtHost.Tag = L("ToolWatermarkHostname");
        TxtPort.Tag = L("ToolTlsAuditPortLabel");

        AutomationProperties.SetName(TxtHost, L("ToolSshServerAuditHostLabel"));
        AutomationProperties.SetName(TxtPort, L("ToolTlsAuditPortLabel"));
        AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));
        AutomationProperties.SetName(BtnAuditFleet, L("ToolSshServerAuditBtnAuditFleet"));
        AutomationProperties.SetName(BtnExportFleetHtml, L("ToolSshServerAuditFleetBtnExportHtml"));
        AutomationProperties.SetName(BtnExportFleetCsv, L("ToolSshServerAuditFleetBtnExportCsv"));
        AutomationProperties.SetName(FleetResultsGrid, L("ToolSshServerAuditFleetTitle"));
        AutomationProperties.SetName(BtnCopy, L("ToolTlsAuditBtnCopy"));
        AutomationProperties.SetName(BtnExportHtml, L("ToolSshServerAuditBtnExportHtml"));
        AutomationProperties.SetName(BtnExportCsv, L("ToolSshServerAuditBtnExportCsv"));
        AutomationProperties.SetName(TxtRecommendation, L("ToolSshServerAuditRecommendation"));
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        BtnHelp.ToolTip = L("ToolHelpTooltip");

        if (_vm.ShowFleetResults)
        {
            RefreshFleetGrid();
        }

        RefreshUiFromVm();
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpSSHSERVERAUDIT").Replace("\\n", "\n", StringComparison.Ordinal);
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e) => HelpPanel.Visibility = Visibility.Collapsed;

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_vm.IsAuditing) _vm.CancelCommand.Execute(null);
        else _ = _vm.RunAuditCommand.ExecuteAsync(null);
        e.Handled = true;
    }

    private void OnAuditClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsAuditing) _vm.CancelCommand.Execute(null);
        else _ = _vm.RunAuditCommand.ExecuteAsync(null);
    }

    private async void OnAuditFleetClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _vm.IsAuditing) return;
        var configManager = (Application.Current as App)?.Services?.GetService<IConfigManager>();
        if (configManager is null)
        {
            ReportStatusText(L("ToolSshServerAuditFleetNoServers"));
            return;
        }

        try
        {
            var servers = await configManager.LoadServersAsync().ConfigureAwait(true);
            var settings = await configManager.LoadSettingsAsync().ConfigureAwait(true);
            if (_disposed) return;
            await _vm.RunFleetAuditAsync(servers, settings).ConfigureAwait(true);
            if (!_disposed)
            {
                RefreshFleetGrid();
                RefreshUiFromVm();
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshServerAudit] fleet audit failed: {ex.Message}");
            if (!_disposed) ReportStatusText(ex.Message);
        }
    }

    private void OnFleetRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FleetResultsGrid.SelectedItem is FleetRowDisplayItem { Entry.Report: { } selectedReport })
        {
            _vm.Report = selectedReport;
            _vm.ShowResults = true;
        }
    }

    private void OnExportFleetHtmlClick(object sender, RoutedEventArgs e)
    {
        if (_vm.FleetResults.Count == 0) return;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = L("FileDialogHtmlFilter"),
                FileName = $"ssh-fleet-audit-{DateTime.UtcNow:yyyyMMdd}.html"
            };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, SshServerAuditEngine.FormatBatchHtmlReport(_vm.FleetResults), new UTF8Encoding(false));
                CopyFeedbackHelper.ShowCopyFeedback(BtnExportFleetHtml);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshServerAudit] Fleet HTML export failed: {ex.Message}");
            ReportStatusText(ex.Message);
        }
    }

    private void OnExportFleetCsvClick(object sender, RoutedEventArgs e)
    {
        if (_vm.FleetResults.Count == 0) return;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = L("FileDialogCsvFilter"),
                FileName = $"ssh-fleet-audit-{DateTime.UtcNow:yyyyMMdd}.csv"
            };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, SshServerAuditEngine.FormatBatchCsvReport(_vm.FleetResults), new UTF8Encoding(false));
                CopyFeedbackHelper.ShowCopyFeedback(BtnExportFleetCsv);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshServerAudit] Fleet CSV export failed: {ex.Message}");
            ReportStatusText(ex.Message);
        }
    }

    private void OnGatewaySelected(SshGatewayDto? gateway) => _vm.SetGateway(gateway);

    private void ReportStatusText(string message)
    {
        TxtError.Text = message;
        TxtError.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed) RefreshUiFromVm();
    }

    private void RefreshUiFromVm()
    {
        _setBusy?.Invoke(_vm.IsAuditing);
        AuditProgress.Visibility = _vm.IsAuditing ? Visibility.Visible : Visibility.Collapsed;

        TxtError.Text = _vm.ErrorMessage;
        TxtError.Visibility = _vm.ShowError ? Visibility.Visible : Visibility.Collapsed;

        EmptyStatePanel.Visibility = _vm.ShowEmptyState ? Visibility.Visible : Visibility.Collapsed;
        FleetResultsPanel.Visibility = _vm.ShowFleetResults ? Visibility.Visible : Visibility.Collapsed;
        ResultsPanel.Visibility = _vm.ShowResults ? Visibility.Visible : Visibility.Collapsed;

        if (_vm.IsAuditing)
        {
            BtnAudit.Content = L("ToolCertBtnStop");
            BtnAudit.Style = (Style)FindResource("SecondaryButtonStyle");
            AutomationProperties.SetName(BtnAudit, L("ToolCertBtnStop"));
            BtnAuditFleet.IsEnabled = false;
            TxtHost.IsReadOnly = true;
            TxtPort.IsReadOnly = true;
            CmbRouteVia.IsEnabled = false;
        }
        else
        {
            BtnAudit.Content = L("ToolSshServerAuditBtnAudit");
            BtnAudit.Style = (Style)FindResource("PrimaryButtonStyle");
            AutomationProperties.SetName(BtnAudit, L("ToolSshServerAuditBtnAudit"));
            BtnAuditFleet.IsEnabled = true;
            TxtHost.IsReadOnly = false;
            TxtPort.IsReadOnly = false;
            CmbRouteVia.IsEnabled = true;
        }

        if (_vm.ShowFleetResults)
        {
            TxtFleetSummary.Text = _vm.FleetSummaryText;
        }

        if (_vm.Report is { } report)
        {
            ProjectReport(report);
        }
    }

    private void RefreshFleetGrid()
    {
        TxtFleetSummary.Text = _vm.FleetSummaryText;
        FleetResultsGrid.ItemsSource = _vm.FleetResults.Select(entry => new FleetRowDisplayItem
        {
            Entry = entry,
            DisplayName = entry.DisplayName,
            Endpoint = $"{entry.Host}:{entry.Port}",
            Gateway = entry.GatewayName ?? "-",
            Grade = entry.Report is { } r ? SshServerAuditEngine.GradeToString(r.Grade) : "-",
            Anssi = entry.Report is { } r1 ? (r1.IsAnssiCompliant ? "PASS" : "FAIL") : "-",
            PostQuantum = entry.Report is { } r2 ? (r2.HasPostQuantumKex ? "YES" : "NO") : "-",
            StrictKex = entry.Report is { } r3 ? (r3.HasStrictKex ? "YES" : "NO") : "-",
            Status = entry.Report is { } r4 ? r4.Snapshot.ServerBanner : (entry.ErrorMessage ?? L("SshSessionStatusError"))
        }).ToList();
    }

    private void ProjectReport(SshServerAuditReport report)
    {
        TxtGrade.Text = SshServerAuditEngine.GradeToString(report.Grade);
        GradeBadge.Background = GetGradeBrush(report.Grade);
        TxtBanner.Text = $"{report.Snapshot.Host}:{report.Snapshot.Port} - {report.Snapshot.ServerBanner}";

        BadgeAnssi.Background = (Brush)FindResource(report.IsAnssiCompliant ? "SuccessBrush" : "ErrorBrush");
        TxtBadgeAnssi.Text = report.IsAnssiCompliant
            ? L("ToolSshServerAuditBadgeAnssiPass")
            : L("ToolSshServerAuditBadgeAnssiFail");

        BadgePq.Background = (Brush)FindResource(report.HasPostQuantumKex ? "SuccessBrush" : "InfoBrush");
        TxtBadgePq.Text = report.HasPostQuantumKex
            ? L("ToolSshServerAuditBadgePqYes")
            : L("ToolSshServerAuditBadgePqNo");

        BadgeStrictKex.Background = (Brush)FindResource(report.HasStrictKex ? "SuccessBrush" : "WarningBrush");
        TxtBadgeStrictKex.Text = report.HasStrictKex
            ? L("ToolSshServerAuditBadgeStrictKexYes")
            : L("ToolSshServerAuditBadgeStrictKexNo");

        FindingsList.ItemsSource = report.Findings.Select(f => new FindingDisplayItem
        {
            Icon = f.Severity switch
            {
                SshAuditFindingSeverity.Pass => "\u2714",
                SshAuditFindingSeverity.Info => "\u2139",
                _ => "\u26A0"
            },
            Message = f.Message,
            Brush = (Brush)FindResource(f.Severity switch
            {
                SshAuditFindingSeverity.Pass => "SuccessTextBrush",
                SshAuditFindingSeverity.Info => "TextSecondaryBrush",
                SshAuditFindingSeverity.Warning => "WarningTextBrush",
                _ => "ErrorTextBrush"
            })
        }).ToList();

        AlgorithmsList.ItemsSource = report.Algorithms.Select(a => new AlgorithmDisplayItem
        {
            Category = a.Category.ToString(),
            Name = a.Name,
            Rating = a.Rating.ToString(),
            Note = a.Note,
            Brush = (Brush)FindResource(a.Rating switch
            {
                SshAlgorithmRating.PostQuantum => "SuccessTextBrush",
                SshAlgorithmRating.Strong => "SuccessTextBrush",
                SshAlgorithmRating.Acceptable => "TextSecondaryBrush",
                SshAlgorithmRating.Weak => "WarningTextBrush",
                _ => "ErrorTextBrush"
            })
        }).ToList();

        TxtRecommendation.Text = report.RecommendedSshdConfig;
    }

    private Brush GetGradeBrush(SshAuditGrade grade) => (Brush)FindResource(grade switch
    {
        SshAuditGrade.APlus or SshAuditGrade.A => "SuccessBrush",
        SshAuditGrade.B => "InfoBrush",
        SshAuditGrade.C => "WarningBrush",
        _ => "ErrorBrush"
    });

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Report is null) return;
        try
        {
            Clipboard.SetText(SshServerAuditEngine.FormatTextReport(_vm.Report));
            CopyFeedbackHelper.ShowCopyFeedback(BtnCopy);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshServerAudit] clipboard copy failed: {ex.Message}");
            ReportStatusText(ex.Message);
        }
    }

    private void OnExportHtmlClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Report is null) return;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = L("FileDialogHtmlFilter"),
                FileName = $"ssh-audit-{SanitizeHostForFileName(_vm.Report.Snapshot.Host)}.html"
            };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, SshServerAuditEngine.FormatHtmlReport(_vm.Report), new UTF8Encoding(false));
                CopyFeedbackHelper.ShowCopyFeedback(BtnExportHtml);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshServerAudit] HTML export failed: {ex.Message}");
            ReportStatusText(ex.Message);
        }
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Report is null) return;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = L("FileDialogCsvFilter"),
                FileName = $"ssh-audit-{SanitizeHostForFileName(_vm.Report.Snapshot.Host)}.csv"
            };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, SshServerAuditEngine.FormatCsvReport(_vm.Report), new UTF8Encoding(false));
                CopyFeedbackHelper.ShowCopyFeedback(BtnExportCsv);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshServerAudit] CSV export failed: {ex.Message}");
            ReportStatusText(ex.Message);
        }
    }

    private void OnLocaleChanged(string locale) => Dispatcher.BeginInvoke(ApplyLocalization);

    private void OnThemeChanged(string _) => Dispatcher.BeginInvoke(RefreshUiFromVm);

    private string L(string key) => _localizer?[key] ?? key;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_localizer is not null) _localizer.LocaleChanged -= OnLocaleChanged;
        if (_themeService is not null) _themeService.ThemeChanged -= OnThemeChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _routeSelector?.Dispose();
        _vm.Dispose();
    }
}
