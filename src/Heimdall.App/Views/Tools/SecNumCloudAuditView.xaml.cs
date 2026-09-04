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

using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.Core.Discovery;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// SecNumCloud v3.2 compliance audit tool. Scans infrastructure targets against
/// ANSSI SecNumCloud requirements across four chapters: Network, Cryptography,
/// Access Control, and Operations. Supports CIDR and host-list scopes with
/// optional gateway tunneling and multi-format report export.
/// </summary>
public partial class SecNumCloudAuditView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private SecNumCloudAuditEngine? _engine;
    private AuditReport? _lastReport;
    private CancellationTokenSource? _cts;
    private bool _isAuditing;
    private bool _disposed;
    private GatewayRouteSelector? _routeSelector;
    private Heimdall.Core.Configuration.SshGatewayDto? _selectedGateway;
    private Action<bool>? _setBusy;
    private CancellationTokenSource? _subnetDetectCts;

    // ── Display model for individual checks ──────────────────────────

    private sealed class CheckDisplayItem
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Clause { get; init; } = "";
        public string StatusText { get; init; } = "";
        public Brush StatusBrush { get; init; } = Brushes.Transparent;
        public string Summary { get; init; } = "";
        public List<AuditEvidence> Evidence { get; init; } = [];
        public Visibility EvidenceVisibility => Evidence.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public SecNumCloudAuditView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _setBusy = context?.SetBusyAction;
        ApplyLocalization();

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            TxtScope.Text = context.TargetHost;
        }
        else
        {
            // Auto-detect local subnet when no target is provided
            var localCidr = SubnetDetector.DetectLocalSubnet();
            if (localCidr is not null)
            {
                TxtScope.Text = localCidr;
            }
        }

        _routeSelector?.Dispose();
        _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportRouteStatus);

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtScope.Focus();
        });
    }

    // ── Localization ─────────────────────────────────────────────────

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolAuditTitle");
        LblScope.Text = L("ToolAuditScope");
        TxtScope.Tag = L("ToolAuditScopeWatermark");
        LblDepth.Text = L("ToolAuditDepth");
        LblRouteVia.Text = L("ToolTunnelRouteVia");
        LblCompliance.Text = L("ToolAuditCompliance");

        ChkNetwork.Content = L("ToolAuditChkNetwork");
        ChkCrypto.Content = L("ToolAuditChkCrypto");
        ChkAccess.Content = L("ToolAuditChkAccess");
        ChkOperations.Content = L("ToolAuditChkOperations");

        BtnStartStop.Content = L("ToolAuditBtnStart");
        BtnExportHtml.Content = L("ToolAuditBtnExportHtml");
        BtnExportCsv.Content = L("ToolAuditBtnExportCsv");
        BtnExportDrawio.Content = L("ToolAuditBtnExportDrawio");

        TxtEmptyState.Text = L("ToolAuditEmptyState");

        CmbDepth.Items.Clear();
        CmbDepth.Items.Add(new ComboBoxItem { Content = L("ToolAuditDepthQuick"), Tag = AuditDepth.Quick });
        CmbDepth.Items.Add(new ComboBoxItem { Content = L("ToolAuditDepthStandard"), Tag = AuditDepth.Standard });
        CmbDepth.Items.Add(new ComboBoxItem { Content = L("ToolAuditDepthDeep"), Tag = AuditDepth.Deep });
        CmbDepth.SelectedIndex = 1;

        // Accessibility names
        System.Windows.Automation.AutomationProperties.SetName(TxtScope, L("ToolAuditScope"));
        System.Windows.Automation.AutomationProperties.SetName(CmbDepth, L("ToolAuditDepth"));
        System.Windows.Automation.AutomationProperties.SetName(ChkNetwork, L("ToolAuditChkNetwork"));
        System.Windows.Automation.AutomationProperties.SetName(ChkCrypto, L("ToolAuditChkCrypto"));
        System.Windows.Automation.AutomationProperties.SetName(ChkAccess, L("ToolAuditChkAccess"));
        System.Windows.Automation.AutomationProperties.SetName(ChkOperations, L("ToolAuditChkOperations"));
        System.Windows.Automation.AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));
        System.Windows.Automation.AutomationProperties.SetName(BtnStartStop, L("ToolAuditBtnStart"));
        System.Windows.Automation.AutomationProperties.SetName(BtnExportHtml, L("ToolAuditBtnExportHtml"));
        System.Windows.Automation.AutomationProperties.SetName(BtnExportCsv, L("ToolAuditBtnExportCsv"));
        System.Windows.Automation.AutomationProperties.SetName(BtnExportDrawio, L("ToolAuditBtnExportDrawio"));
        System.Windows.Automation.AutomationProperties.SetName(AuditProgress, L("ToolAuditA11yProgress"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
    }

    // ── Gateway selector ─────────────────────────────────────────────

    private void OnGatewaySelected(Heimdall.Core.Configuration.SshGatewayDto? gateway)
    {
        _selectedGateway = gateway;
        if (gateway is null)
        {
            // Revert to local subnet when switching back to Direct
            var localCidr = SubnetDetector.DetectLocalSubnet();
            if (localCidr is not null)
            {
                TxtScope.Text = localCidr;
            }
            return;
        }

        _ = DetectRemoteSubnetsAsync(gateway);
    }

    private async Task DetectRemoteSubnetsAsync(Heimdall.Core.Configuration.SshGatewayDto gateway)
    {
        _subnetDetectCts?.Cancel();
        _subnetDetectCts = new CancellationTokenSource();
        var ct = _subnetDetectCts.Token;

        TxtDetected.Text = string.Format(L("ToolSubnetDetecting"), gateway.Name);

        try
        {
            var subnets = await SubnetDetector.DetectRemoteSubnetsAsync(gateway, ct);

            if (subnets.Count > 0)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    TxtScope.Text = subnets[0];
                    TxtDetected.Text = string.Format(
                        L("ToolSubnetDetected"),
                        subnets.Count,
                        gateway.Name);
                    if (subnets.Count > 1)
                    {
                        TxtScope.ToolTip = string.Join("\n", subnets);
                    }
                });
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                    TxtDetected.Text = L("ToolSubnetDetectFailed"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Subnet detection failed for {gateway.Name}: {ex.Message}");
            await Dispatcher.InvokeAsync(() =>
                TxtDetected.Text = L("ToolSubnetDetectFailed"));
        }
    }

    // ── Scope auto-detect ────────────────────────────────────────────

    private void OnScopeTextChanged(object sender, TextChangedEventArgs e)
    {
        var text = TxtScope.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            TxtDetected.Text = "";
            return;
        }

        if (text.Contains('/'))
        {
            TxtDetected.Text = string.Format(L("ToolAuditDetectedCidr"), text.Split('\n', '\r')[0].Trim());
        }
        else
        {
            var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            var count = lines.Count(l => !string.IsNullOrWhiteSpace(l));
            TxtDetected.Text = string.Format(L("ToolAuditDetectedHosts"), count);
        }
    }

    // ── Help ─────────────────────────────────────────────────────────

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtHelpContent.Text = L("ToolHelpSECNUMCLOUD").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    // ── Start / Stop ─────────────────────────────────────────────────

    private void OnStartStopClick(object sender, RoutedEventArgs e)
    {
        if (_isAuditing)
        {
            StopAudit();
        }
        else
        {
            _ = StartAuditAsync();
        }
    }

    private async Task StartAuditAsync()
    {
        var scopeText = TxtScope.Text.Trim();
        HideInlineError();

        if (string.IsNullOrWhiteSpace(scopeText))
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            TxtPhase.Text = string.Empty;
            TxtCurrentCheck.Text = string.Empty;
            ShowInlineError(L("ToolAuditErrorScope"));
            RestoreContentState();
            return;
        }

        // Parse scope: CIDR or host list
        string? subnet = null;
        var targets = new List<string>();

        var lines = scopeText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var entry = line.Trim();
            if (string.IsNullOrWhiteSpace(entry)) continue;

            if (entry.Contains('/'))
            {
                subnet = entry;
            }
            else
            {
                targets.Add(entry);
            }
        }

        var scope = new AuditScope(targets, subnet, _selectedGateway?.Id);

        var depth = CmbDepth.SelectedItem is ComboBoxItem item && item.Tag is AuditDepth d
            ? d : AuditDepth.Standard;

        var options = new AuditOptions(
            Depth: depth,
            CheckNetwork: ChkNetwork.IsChecked == true,
            CheckCrypto: ChkCrypto.IsChecked == true,
            CheckAccess: ChkAccess.IsChecked == true,
            CheckOperations: ChkOperations.IsChecked == true);

        // Transition to auditing state
        _cts = new CancellationTokenSource();
        _isAuditing = true;
        _setBusy?.Invoke(true);
        SetUiAuditing(true);

        _engine = new SecNumCloudAuditEngine(key => L(key));

        _engine.PhaseProgress += (phaseName, completed, total) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                AuditProgress.IsIndeterminate = false;
                AuditProgress.Maximum = total;
                AuditProgress.Value = completed;
                TxtPhase.Text = string.Format(L("ToolAuditProgress"), phaseName, completed, total);
            });
        };

        _engine.StatusChanged += status =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                TxtCurrentCheck.Text = status;
            });
        };

        _engine.CheckCompleted += check =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                TxtCurrentCheck.Text = $"{check.Id}: {GetStatusLabel(check.Status)}";
            });
        };

        try
        {
            var report = await _engine.RunAuditAsync(scope, options, _cts.Token).ConfigureAwait(false);
            _lastReport = report;

            await Dispatcher.InvokeAsync(() =>
            {
                HideInlineError();
                DisplayResults(report);

                var totalChecks = report.Chapters.Sum(c => c.Checks.Count);
                var passChecks = report.Chapters.Sum(c => c.PassCount);
                var compliance = totalChecks > 0 ? (int)Math.Round(100.0 * passChecks / totalChecks) : 0;

                var duration = report.EndTime - report.StartTime;
                var hostCount = (scope.Targets.Count > 0 ? scope.Targets.Count : 0) +
                                (scope.Subnet is not null ? 1 : 0);

                var stats = new List<string>
                {
                    string.Format(L("ToolAuditDuration"), FormatDuration(duration)),
                    string.Format(L("ToolAuditCheckCount"), totalChecks),
                };
                TxtFooterStats.Text = string.Join(" | ", stats);

                var completeMsg = string.Format(L("ToolAuditComplete"), compliance);
                TxtPhase.Text = completeMsg;
                TxtCurrentCheck.Text = "";
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                ProgressPanel.Visibility = Visibility.Collapsed;
                TxtPhase.Text = string.Empty;
                TxtCurrentCheck.Text = string.Empty;
                HideInlineError();
                RestoreContentState();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                ProgressPanel.Visibility = Visibility.Collapsed;
                TxtPhase.Text = string.Empty;
                TxtCurrentCheck.Text = string.Empty;
                ShowInlineError($"{L("ToolAuditError")}: {ex.Message}");
                RestoreContentState();
            });
        }
        finally
        {
            _isAuditing = false;
            _cts?.Dispose();
            _cts = null;
            _engine = null;
            _setBusy?.Invoke(false);
            await Dispatcher.InvokeAsync(() => SetUiAuditing(false));
        }
    }

    private void StopAudit()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void SetUiAuditing(bool auditing)
    {
        if (auditing)
        {
            BtnStartStop.Content = L("ToolAuditBtnStop");
            BtnStartStop.Style = (Style)FindResource("SecondaryButtonStyle");
            BtnStartStop.Foreground = (Brush)FindResource("ErrorBrush");
            System.Windows.Automation.AutomationProperties.SetName(BtnStartStop, L("ToolAuditBtnStop"));

            TxtScope.IsReadOnly = true;
            CmbDepth.IsEnabled = false;
            ChkNetwork.IsEnabled = false;
            ChkCrypto.IsEnabled = false;
            ChkAccess.IsEnabled = false;
            ChkOperations.IsEnabled = false;
            CmbRouteVia.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            AuditProgress.Value = 0;
            AuditProgress.IsIndeterminate = true;
            TxtPhase.Text = "";
            TxtCurrentCheck.Text = "";
            HideInlineError();
        }
        else
        {
            BtnStartStop.Content = L("ToolAuditBtnStart");
            BtnStartStop.Style = (Style)FindResource("PrimaryButtonStyle");
            BtnStartStop.ClearValue(ForegroundProperty);
            System.Windows.Automation.AutomationProperties.SetName(BtnStartStop, L("ToolAuditBtnStart"));

            TxtScope.IsReadOnly = false;
            CmbDepth.IsEnabled = true;
            ChkNetwork.IsEnabled = true;
            ChkCrypto.IsEnabled = true;
            ChkAccess.IsEnabled = true;
            ChkOperations.IsEnabled = true;
            CmbRouteVia.IsEnabled = true;

            var hasResults = _lastReport is not null;
            BtnExportHtml.IsEnabled = hasResults;
            BtnExportCsv.IsEnabled = hasResults;
            BtnExportDrawio.IsEnabled = hasResults && _lastReport!.NetworkSnapshot is not null;
        }
    }

    // ── Results display ──────────────────────────────────────────────

    private void DisplayResults(AuditReport report)
    {
        ResultsPanel.Children.Clear();
        ComplianceBadgePanel.Visibility = Visibility.Visible;
        ResultsScroller.Visibility = Visibility.Visible;
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        HideInlineError();

        // Overall compliance
        var totalChecks = report.Chapters.Sum(c => c.Checks.Count);
        var passChecks = report.Chapters.Sum(c => c.PassCount);
        var warnChecks = report.Chapters.Sum(c => c.WarnCount);
        var failChecks = report.Chapters.Sum(c => c.FailCount);
        var compliance = totalChecks > 0 ? (int)Math.Round(100.0 * passChecks / totalChecks) : 0;

        TxtCompliancePercent.Text = $"{compliance}%";
        TxtCompliancePercent.Foreground = GetComplianceBrush(compliance);

        TxtPassCount.Text = $"{L("ToolAuditPass")}: {passChecks}";
        TxtPassCount.Foreground = (Brush)FindResource("SuccessBrush");
        TxtWarnCount.Text = $"{L("ToolAuditWarn")}: {warnChecks}";
        TxtWarnCount.Foreground = (Brush)FindResource("WarningBrush");
        TxtFailCount.Text = $"{L("ToolAuditFail")}: {failChecks}";
        TxtFailCount.Foreground = (Brush)FindResource("ErrorBrush");

        // Chapters as Expanders
        foreach (var chapter in report.Chapters)
        {
            var expander = BuildChapterExpander(chapter);
            ResultsPanel.Children.Add(expander);
        }
    }

    private Expander BuildChapterExpander(AuditChapter chapter)
    {
        // Header: Chapter name + SecNumCloud ref + pass/warn/fail counts
        var headerPanel = new DockPanel();

        var countsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        countsPanel.SetValue(DockPanel.DockProperty, Dock.Right);

        var passLabel = new TextBlock
        {
            Text = $"{chapter.PassCount}",
            Foreground = (Brush)FindResource("SuccessBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = (double)FindResource("FontSizeBody"),
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(passLabel, $"{L("ToolAuditPass")}: {chapter.PassCount}");
        countsPanel.Children.Add(passLabel);

        var warnLabel = new TextBlock
        {
            Text = $"{chapter.WarnCount}",
            Foreground = (Brush)FindResource("WarningBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = (double)FindResource("FontSizeBody"),
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(warnLabel, $"{L("ToolAuditWarn")}: {chapter.WarnCount}");
        countsPanel.Children.Add(warnLabel);

        var failLabel = new TextBlock
        {
            Text = $"{chapter.FailCount}",
            Foreground = (Brush)FindResource("ErrorBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = (double)FindResource("FontSizeBody"),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(failLabel, $"{L("ToolAuditFail")}: {chapter.FailCount}");
        countsPanel.Children.Add(failLabel);

        headerPanel.Children.Add(countsPanel);

        var titleText = new TextBlock
        {
            Text = $"{chapter.Name} ({chapter.SecNumCloudRef})",
            FontSize = (double)FindResource("FontSizeBodyLarge"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerPanel.Children.Add(titleText);

        // Content: stacked check cards
        var checksPanel = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };

        foreach (var check in chapter.Checks)
        {
            var checkCard = BuildCheckCard(check);
            checksPanel.Children.Add(checkCard);
        }

        var expander = new Expander
        {
            Header = headerPanel,
            Content = checksPanel,
            IsExpanded = chapter.FailCount > 0 || chapter.WarnCount > 0,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
        };
        System.Windows.Automation.AutomationProperties.SetName(expander, $"{chapter.Name} {chapter.SecNumCloudRef}");

        return expander;
    }

    private Border BuildCheckCard(AuditCheck check)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Status badge
        var statusBorder = new Border
        {
            Background = GetStatusBrush(check.Status),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        var statusText = new TextBlock
        {
            Text = GetStatusLabel(check.Status),
            Foreground = (Brush)FindResource("TextOnAccentBrush"),
            FontSize = (double)FindResource("FontSizeSmallCaption"),
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        statusBorder.Child = statusText;
        Grid.SetColumn(statusBorder, 0);
        grid.Children.Add(statusBorder);

        // Check details
        var detailsPanel = new StackPanel();

        var headerLine = new TextBlock
        {
            FontSize = (double)FindResource("FontSizeBody"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
        };
        headerLine.Inlines.Add(new System.Windows.Documents.Run(check.Id) { FontWeight = FontWeights.SemiBold });
        headerLine.Inlines.Add(new System.Windows.Documents.Run($" - {check.Name}"));
        detailsPanel.Children.Add(headerLine);

        var clauseText = new TextBlock
        {
            Text = check.SecNumCloudClause,
            FontSize = (double)FindResource("FontSizeCaption"),
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 2, 0, 2),
        };
        detailsPanel.Children.Add(clauseText);

        if (!string.IsNullOrWhiteSpace(check.Summary))
        {
            var summaryText = new TextBlock
            {
                Text = check.Summary,
                FontSize = (double)FindResource("FontSizeBody"),
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            };
            detailsPanel.Children.Add(summaryText);
        }

        // Evidence (expandable if present)
        if (check.Evidence.Count > 0)
        {
            var evidenceExpander = new Expander
            {
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = (double)FindResource("FontSizeCaption"),
                IsExpanded = false,
                Margin = new Thickness(0, 2, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(evidenceExpander, string.Format(L("ToolAuditEvidenceFor"), check.Id));

            var evidenceHeader = new TextBlock
            {
                Text = string.Format(L("ToolAuditEvidenceCount"), check.Evidence.Count),
                FontSize = (double)FindResource("FontSizeCaption"),
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            };
            evidenceExpander.Header = evidenceHeader;

            var evidencePanel = new StackPanel();
            foreach (var ev in check.Evidence)
            {
                var evBorder = new Border
                {
                    Background = (Brush)FindResource("SurfaceBrush"),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 2, 0, 2),
                };

                var evStack = new StackPanel();
                var evHost = new TextBlock
                {
                    Text = ev.Host,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = (double)FindResource("FontSizeCaption"),
                    Foreground = (Brush)FindResource("TextPrimaryBrush"),
                };
                evStack.Children.Add(evHost);

                var evDetail = new TextBlock
                {
                    Text = ev.Detail,
                    FontSize = (double)FindResource("FontSizeCaption"),
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                };
                evStack.Children.Add(evDetail);

                if (!string.IsNullOrWhiteSpace(ev.RawData))
                {
                    var evRaw = new TextBox
                    {
                        Text = ev.RawData,
                        IsReadOnly = true,
                        FontSize = (double)FindResource("FontSizeSmallCaption"),
                        FontFamily = (System.Windows.Media.FontFamily)FindResource("FontFamilyMonospace"),
                        Foreground = (Brush)FindResource("TextSecondaryBrush"),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 100,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Margin = new Thickness(0, 2, 0, 0),
                    };
                    System.Windows.Automation.AutomationProperties.SetName(evRaw, string.Format(L("ToolAuditRawDataFor"), ev.Host));
                    evStack.Children.Add(evRaw);
                }

                evBorder.Child = evStack;
                evidencePanel.Children.Add(evBorder);
            }

            evidenceExpander.Content = evidencePanel;
            detailsPanel.Children.Add(evidenceExpander);
        }

        Grid.SetColumn(detailsPanel, 1);
        grid.Children.Add(detailsPanel);

        var cardBorder = new Border
        {
            Child = grid,
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("CornerRadiusSm"),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 2, 0, 2),
        };
        System.Windows.Automation.AutomationProperties.SetName(cardBorder, $"{check.Id} {GetStatusLabel(check.Status)}");

        return cardBorder;
    }

    // ── Export handlers ──────────────────────────────────────────────

    private void OnExportHtmlClick(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "HTML files (*.html)|*.html",
            FileName = $"SecNumCloud_Audit_{DateTime.Now:yyyyMMdd_HHmmss}.html",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var html = HtmlReportGenerator.Generate(_lastReport, key => L(key));
            File.WriteAllText(dialog.FileName, html, Encoding.UTF8);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SecNumCloud HTML export failed: {ex.Message}");
            ShowInlineError(string.Format(L("StatusExportFailed"), ex.Message));
        }
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;

        var dialog = new SaveFileDialog
        {
            Filter = L("FileDialogCsvFilter"),
            FileName = $"SecNumCloud_Evidence_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var csv = CsvEvidenceExporter.Generate(_lastReport);
            File.WriteAllText(dialog.FileName, csv, Encoding.UTF8);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SecNumCloud CSV export failed: {ex.Message}");
            ShowInlineError(string.Format(L("StatusExportFailed"), ex.Message));
        }
    }

    private void OnExportDrawioClick(object sender, RoutedEventArgs e)
    {
        if (_lastReport?.NetworkSnapshot is not NetworkScanSnapshot snapshot) return;

        var dialog = new SaveFileDialog
        {
            Filter = "Draw.io files (*.drawio)|*.drawio",
            FileName = $"SecNumCloud_Network_{DateTime.Now:yyyyMMdd_HHmmss}.drawio",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var xml = DrawIoExporter.Generate(snapshot);
            File.WriteAllText(dialog.FileName, xml, Encoding.UTF8);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SecNumCloud Draw.io export failed: {ex.Message}");
            ShowInlineError(string.Format(L("StatusExportFailed"), ex.Message));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private string GetStatusLabel(AuditStatus status) => status switch
    {
        AuditStatus.Pass => L("ToolAuditPass"),
        AuditStatus.Warning => L("ToolAuditWarn"),
        AuditStatus.Fail => L("ToolAuditFail"),
        AuditStatus.Error => L("ToolAuditError"),
        AuditStatus.Skipped => L("ToolAuditSkipped"),
        _ => status.ToString(),
    };

    private Brush GetStatusBrush(AuditStatus status) => status switch
    {
        AuditStatus.Pass => (Brush)FindResource("SuccessBrush"),
        AuditStatus.Warning => (Brush)FindResource("WarningBrush"),
        AuditStatus.Fail => (Brush)FindResource("ErrorBrush"),
        AuditStatus.Error => (Brush)FindResource("ErrorBrush"),
        AuditStatus.Skipped => (Brush)FindResource("TextDisabledBrush"),
        _ => (Brush)FindResource("TextDisabledBrush"),
    };

    private Brush GetComplianceBrush(int percent) => percent switch
    {
        >= 80 => (Brush)FindResource("SuccessBrush"),
        >= 50 => (Brush)FindResource("WarningBrush"),
        _ => (Brush)FindResource("ErrorBrush"),
    };

    private static string FormatDuration(TimeSpan ts) => ts.TotalMinutes >= 1
        ? $"{ts.Minutes}m {ts.Seconds}s"
        : $"{ts.TotalSeconds:F1}s";

    private void ShowInlineError(string message)
    {
        TxtError.Text = message;
        TxtError.Visibility = Visibility.Visible;
    }

    private void HideInlineError()
    {
        TxtError.Text = string.Empty;
        TxtError.Visibility = Visibility.Collapsed;
    }

    private void RestoreContentState()
    {
        var hasResults = _lastReport is not null;
        ComplianceBadgePanel.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroller.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
    }

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_isAuditing;

    private void ReportRouteStatus(string message)
    {
        TxtError.Text = message;
        TxtError.Visibility = Visibility.Visible;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _routeSelector?.Dispose();
        StopAudit();
        GC.SuppressFinalize(this);
    }
}
