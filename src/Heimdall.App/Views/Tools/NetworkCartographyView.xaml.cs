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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Discovery;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Network cartography tool that performs subnet-wide discovery with port scanning,
/// service identification, banner grabbing, and heuristic role classification.
/// </summary>
public partial class NetworkCartographyView : UserControl, IToolView
{
    private const int LargeSubnetThreshold = 512;

    private LocalizationManager? _localizer;
    private bool _disposed;
    private List<(string FileName, DateTime Timestamp, string Subnet)> _historyList = [];
    private List<Heimdall.Core.Configuration.SshGatewayDto>? _gateways;
    private Action<string, string, ToolContext?>? _openToolAction;
    private Action<bool>? _setBusy;
    private int _renderedHostCount;

    private readonly NetworkCartographyViewModel _vm;
    private readonly ObservableCollection<CartographyRowViewModel> _results = [];

    public NetworkCartographyView()
    {
        _vm = new NetworkCartographyViewModel();
        InitializeComponent();
        _vm.PropertyChanged += OnVmPropertyChanged;
        DataContext = _vm;

        ResultsGrid.ItemsSource = _results;
        TxtSubnet.KeyDown += OnSubnetKeyDown;
        ResultsGrid.PreviewMouseRightButtonDown += ToolContextMenuHelper.SelectRowOnRightClick;
        ResultsGrid.ContextMenuOpening += OnResultsContextMenuOpening;
        ResultsGrid.LoadingRow += OnResultsLoadingRow;
        SizeChanged += OnViewSizeChanged;
        RefreshUiFromVm();
    }

    /// <summary>
    /// Adapts column visibility based on available width for split pane support.
    /// </summary>
    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        var showDetails = width >= 1180;
        ColManufacturer.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        ColTls.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        ColDetails.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        ColVlan.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;

        var showServices = width >= 960;
        ColPorts.Visibility = showServices ? Visibility.Visible : Visibility.Collapsed;
        ColServices.Visibility = showServices ? Visibility.Visible : Visibility.Collapsed;

        var showSecondary = width >= 760;
        ColConfidence.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
        ColOs.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _openToolAction = ToolContextMenuHelper.GetOpenToolAction(context);
        _setBusy = context?.SetBusyAction;
        ApplyLocalization();

        _vm.Initialize(localizer);

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            _vm.Subnet = context.TargetHost;
        }
        else
        {
            var localSubnet = DetectLocalSubnet();
            if (localSubnet is not null)
            {
                _vm.Subnet = localSubnet;
            }
        }

        if (context?.SshGateways is System.Collections.IList gateways)
        {
            _gateways = gateways.Cast<Heimdall.Core.Configuration.SshGatewayDto>().ToList();
        }

        PopulateRouteSelector();
        PopulateHistory();
        UpdateResponsiveLayout(ActualWidth);
        RefreshUiFromVm();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtSubnet.Focus();
            TxtSubnet.SelectAll();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolNetMapTitle");
        LblSubnet.Text = L("ToolNetMapSubnet");
        LblDepth.Text = L("ToolNetMapDepth");
        BtnStart.Content = L("ToolNetMapBtnStart");
        BtnStop.Content = L("ToolNetMapBtnStop");
        BtnExportCsv.Content = L("ToolNetMapBtnExport");

        ChkSkipPing.Content = L("ToolNetMapSkipPing");
        ChkReverseDns.Content = L("ToolNetMapReverseDns");
        LblRouteVia.Text = L("ToolTunnelRouteVia");

        BtnExportDrawio.Content = L("ToolNetMapBtnExportDrawio");
        BtnEditDiagram.Content = L("ToolDiagramBtnEdit");
        ChkUseKnowledgeBase.Content = L("ToolNetMapUseKb");
        BtnClearKb.Content = L("ToolNetMapBtnClearKb");

        ColIp.Header = L("ToolNetMapColIp");
        ColMac.Header = L("ToolNetMapColMac");
        ColHostname.Header = L("ToolNetMapColHostname");
        ColPorts.Header = L("ToolNetMapColPorts");
        ColServices.Header = L("ToolNetMapColServices");
        ColTls.Header = L("ToolNetMapColTls");
        ColRole.Header = L("ToolNetMapColRole");
        ColConfidence.Header = L("ToolNetMapColConfidence");
        ColOs.Header = L("ToolNetMapColOs");
        ColLatency.Header = L("ToolNetMapColLatency");
        ColVlan.Header = L("ToolNetMapColVlan");
        ColDetails.Header = L("ToolNetMapColDetails");
        ColManufacturer.Header = L("ToolNetMapColManufacturer");

        CmbDepth.Items.Clear();
        CmbDepth.Items.Add(new ComboBoxItem { Content = L("ToolNetMapDepthQuick"), Tag = ScanDepth.Quick });
        CmbDepth.Items.Add(new ComboBoxItem { Content = L("ToolNetMapDepthStandard"), Tag = ScanDepth.Standard });
        CmbDepth.Items.Add(new ComboBoxItem { Content = L("ToolNetMapDepthDeep"), Tag = ScanDepth.Deep });
        CmbDepth.SelectedIndex = 0;

        System.Windows.Automation.AutomationProperties.SetName(TxtSubnet, L("ToolNetMapSubnet"));
        System.Windows.Automation.AutomationProperties.SetName(CmbDepth, L("ToolNetMapDepth"));
        System.Windows.Automation.AutomationProperties.SetName(BtnStart, L("ToolNetMapBtnStart"));
        System.Windows.Automation.AutomationProperties.SetName(BtnStop, L("ToolNetMapBtnStop"));
        System.Windows.Automation.AutomationProperties.SetName(BtnExportCsv, L("ToolNetMapBtnExport"));
        System.Windows.Automation.AutomationProperties.SetName(BtnExportDrawio, L("ToolNetMapBtnExportDrawio"));
        System.Windows.Automation.AutomationProperties.SetName(BtnEditDiagram, L("ToolDiagramBtnEdit"));
        System.Windows.Automation.AutomationProperties.SetName(CmbHistory, L("ToolNetMapCompareWith"));
        System.Windows.Automation.AutomationProperties.SetName(ChkSkipPing, L("ToolNetMapSkipPing"));
        System.Windows.Automation.AutomationProperties.SetName(ChkReverseDns, L("ToolNetMapReverseDns"));
        System.Windows.Automation.AutomationProperties.SetName(CmbRouteVia, L("ToolTunnelRouteVia"));
        ChkUseKnowledgeBase.ToolTip = L("ToolNetMapKbTooltip");
        System.Windows.Automation.AutomationProperties.SetName(ChkUseKnowledgeBase, L("ToolNetMapUseKb"));
        System.Windows.Automation.AutomationProperties.SetName(BtnClearKb, L("ToolNetMapBtnClearKb"));

        TxtSubnet.Tag = L("ToolWatermarkSubnetCidr");

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        TxtEmptyState.Text = L("ToolEmptyStateNetMap");

        System.Windows.Automation.AutomationProperties.SetName(ScanProgress, L("ToolNetMapA11yProgress"));
        System.Windows.Automation.AutomationProperties.SetName(ResultsGrid, L("ToolNetMapTitle"));
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshUiFromVm(e.PropertyName);
    }

    private void RefreshUiFromVm(string? propertyName = null)
    {
        _setBusy?.Invoke(_vm.IsScanning);
        SetScanUiState(_vm.IsScanning);

        TxtStatus.Text = _vm.StatusText;
        TxtStats.Text = _vm.StatsText;
        TxtKbStats.Text = _vm.KbStatsText;
        TxtStatsSeparator.Visibility = !string.IsNullOrWhiteSpace(_vm.StatsText)
            && !string.IsNullOrWhiteSpace(_vm.KbStatsText)
                ? Visibility.Visible
                : Visibility.Collapsed;

        ProgressPanel.Visibility = _vm.ShowProgress || !string.IsNullOrEmpty(_vm.StatusText)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScanProgress.IsIndeterminate = _vm.ProgressIsIndeterminate;
        ScanProgress.Maximum = Math.Max(1, _vm.ProgressMaximum);
        ScanProgress.Value = Math.Min(_vm.ProgressValue, ScanProgress.Maximum);
        TxtScanProgress.Text = _vm.ProgressText;
        TxtScanProgress.Visibility = string.IsNullOrEmpty(_vm.ProgressText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        BtnExportCsv.IsEnabled = !_vm.IsScanning && _results.Count > 0;
        BtnExportDrawio.IsEnabled = !_vm.IsScanning && _vm.LastSnapshot is not null;
        BtnEditDiagram.IsEnabled = !_vm.IsScanning
            && _vm.LastSnapshot is not null
            && _openToolAction is not null;
        BtnClearKb.IsEnabled = !_vm.IsScanning && _vm.CanClearKb;

        DiffPanel.Visibility = _vm.ShowDiff ? Visibility.Visible : Visibility.Collapsed;
        TxtDiff.Text = _vm.DiffText;

        if (propertyName is null || propertyName == nameof(NetworkCartographyViewModel.HostResults))
        {
            SyncProjectedResults();
        }

        if (propertyName == nameof(NetworkCartographyViewModel.LastSnapshot))
        {
            RebuildProjectedResults();
            PopulateHistory();
        }

        UpdateResultsSurface();
    }

    private void SyncProjectedResults()
    {
        if (_vm.HostResults.Count < _renderedHostCount)
        {
            RebuildProjectedResults();
            return;
        }

        for (var index = _renderedHostCount; index < _vm.HostResults.Count; index++)
        {
            _results.Add(ToRow(_vm.HostResults[index]));
        }

        _renderedHostCount = _vm.HostResults.Count;
    }

    private void RebuildProjectedResults()
    {
        _results.Clear();
        foreach (var host in _vm.HostResults)
        {
            _results.Add(ToRow(host));
        }

        _renderedHostCount = _vm.HostResults.Count;
        ResultsGrid.Items.Refresh();
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpNETMAP").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnSubnetKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_vm.IsScanning)
        {
            await TriggerScanAsync();
            e.Handled = true;
        }
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        await TriggerScanAsync();
    }

    private async Task TriggerScanAsync()
    {
        if (_disposed || _vm.IsScanning)
        {
            return;
        }

        _vm.SelectedDepth = CmbDepth.SelectedItem is ComboBoxItem item && item.Tag is ScanDepth depth
            ? depth
            : ScanDepth.Quick;
        _vm.SkipPing = ChkSkipPing.IsChecked == true;
        _vm.ReverseDns = ChkReverseDns.IsChecked == true;
        _vm.UseKnowledgeBase = ChkUseKnowledgeBase.IsChecked == true;

        if (_vm.LargeSubnetHostCount > LargeSubnetThreshold)
        {
            var warning = string.Format(L("ToolNetMapWarningLargeSubnet"), _vm.LargeSubnetHostCount);
            var dialogService = (Application.Current as App)?.Services
                ?.GetService(typeof(IDialogService)) as IDialogService;
            if (dialogService is null
                || !await dialogService.ShowConfirmAsync(L("ToolNetMapTitle"), warning, "warning"))
            {
                return;
            }
        }

        await _vm.ScanCommand.ExecuteAsync(null);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _vm.CancelCommand.Execute(null);
    }

    private void SetScanUiState(bool isScanning)
    {
        BtnStart.IsEnabled = !isScanning;
        BtnStop.IsEnabled = isScanning;
        TxtSubnet.IsReadOnly = isScanning;
        CmbDepth.IsEnabled = !isScanning;
        ChkSkipPing.IsEnabled = !isScanning;
        ChkReverseDns.IsEnabled = !isScanning;
        ChkUseKnowledgeBase.IsEnabled = !isScanning;
        CmbRouteVia.IsEnabled = !isScanning;
        CmbHistory.IsEnabled = !isScanning;
    }

    private void UpdateResultsSurface()
    {
        var hasResults = _results.Count > 0;
        ResultsPanel.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasResults || _vm.IsScanning
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private CartographyRowViewModel ToRow(HostScanResult host)
    {
        var openPortsList = host.Services.Where(s => s.IsOpen).Select(s => s.Port).OrderBy(p => p).ToList();
        var openPorts = openPortsList.AsEnumerable();
        var services = host.Services
            .Where(s => s.IsOpen && s.ServiceName is not null)
            .Select(s => s.ServiceName!)
            .Distinct()
            .OrderBy(s => s);

        var tlsCert = host.Services.FirstOrDefault(s => s.Certificate is not null)?.Certificate;
        var tlsStatus = "";
        if (tlsCert is not null)
        {
            if (tlsCert.IsExpired)
                tlsStatus = $"{L("ToolNetMapCertExpired")}!";
            else if (tlsCert.ExpiresSoon)
                tlsStatus = $"{tlsCert.TlsVersion} ({L("ToolNetMapCertExpiring")} {tlsCert.NotAfter:yyyy-MM-dd})";
            else
                tlsStatus = $"{tlsCert.TlsVersion} ({L("ToolNetMapCertValid")} {tlsCert.NotAfter:yyyy-MM-dd})";
        }

        var osGuess = host.OsFingerprint?.OsGuess ?? "\u2014";

        var detailParts = new List<string>();
        if (!string.IsNullOrEmpty(host.NetBiosName))
            detailParts.Add($"NB:{host.NetBiosName}");
        if (host.SnmpInfo?.SysName is not null)
            detailParts.Add($"SNMP:{host.SnmpInfo.SysName}");
        if (host.MdnsServices is { Count: > 0 })
            detailParts.Add($"mDNS:{host.MdnsServices.Count}");
        if (host.SsdpInfo?.DeviceType is not null)
            detailParts.Add($"UPnP:{host.SsdpInfo.DeviceType}");
        if (host.NtlmInfo?.DnsComputerName is not null)
            detailParts.Add($"NTLM:{host.NtlmInfo.DnsComputerName}");
        if (host.SshHashFingerprint is not null)
            detailParts.Add($"HASSH:{host.SshHashFingerprint[..8]}");
        if (host.FaviconHash is not null && FaviconHasher.KnownHashes.TryGetValue(host.FaviconHash.Value, out var favName))
            detailParts.Add($"Fav:{favName}");
        if (host.HttpFingerprint?.ProductName is not null)
            detailParts.Add($"HTTP:{host.HttpFingerprint.ProductName}");
        if (host.SmbInfo is not null)
            detailParts.Add($"SMB:{host.SmbInfo.DialectRevision:X4}");
        var detailsSummary = detailParts.Count > 0 ? string.Join(" | ", detailParts) : "\u2014";

        var tooltipParts = new List<string>
        {
            $"{L("ToolNetMapTipIp")}: {host.IpAddress}"
        };
        if (host.Hostname is not null) tooltipParts.Add($"{L("ToolNetMapTipHostname")}: {host.Hostname}");
        if (host.OsFingerprint is not null)
            tooltipParts.Add($"{L("ToolNetMapTipOs")}: {host.OsFingerprint.OsGuess} ({host.OsFingerprint.Source}, {host.OsFingerprint.Confidence}%)");
        if (host.PingLatencyMs > 0) tooltipParts.Add($"{L("ToolNetMapTipLatency")}: {host.PingLatencyMs}ms");
        if (host.MacAddress is not null) tooltipParts.Add($"{L("ToolNetMapTipMac")}: {host.MacAddress}");
        if (host.Manufacturer is not null) tooltipParts.Add($"{L("ToolNetMapTipManufacturer")}: {host.Manufacturer}");
        if (host.NetBiosName is not null) tooltipParts.Add($"{L("ToolNetMapTipNetBios")}: {host.NetBiosName}");
        if (host.NetBiosDomain is not null) tooltipParts.Add($"{L("ToolNetMapTipDomain")}: {host.NetBiosDomain}");
        if (host.SnmpInfo is not null)
        {
            if (host.SnmpInfo.SysDescr is not null) tooltipParts.Add($"{L("ToolNetMapTipSnmpDescr")}: {host.SnmpInfo.SysDescr}");
            if (host.SnmpInfo.SysName is not null) tooltipParts.Add($"{L("ToolNetMapTipSnmpName")}: {host.SnmpInfo.SysName}");
            if (host.SnmpInfo.SysLocation is not null) tooltipParts.Add($"{L("ToolNetMapTipSnmpLocation")}: {host.SnmpInfo.SysLocation}");
        }
        if (host.MdnsServices is { Count: > 0 })
            tooltipParts.Add($"{L("ToolNetMapTipMdns")}: {string.Join(", ", host.MdnsServices)}");
        if (host.SsdpInfo is not null)
        {
            if (host.SsdpInfo.FriendlyName is not null)
                tooltipParts.Add($"{L("ToolNetMapTipSsdpName")}: {host.SsdpInfo.FriendlyName}");
            if (host.SsdpInfo.Manufacturer is not null)
                tooltipParts.Add($"{L("ToolNetMapTipSsdpMfr")}: {host.SsdpInfo.Manufacturer}");
            if (host.SsdpInfo.ModelName is not null)
                tooltipParts.Add($"{L("ToolNetMapTipSsdpModel")}: {host.SsdpInfo.ModelName}");
            if (host.SsdpInfo.ModelNumber is not null)
                tooltipParts.Add($"{L("ToolNetMapTipSsdpModelNum")}: {host.SsdpInfo.ModelNumber}");
            if (host.SsdpInfo.SerialNumber is not null)
                tooltipParts.Add($"{L("ToolNetMapTipSsdpSerial")}: {host.SsdpInfo.SerialNumber}");
            if (host.SsdpInfo.DeviceType is not null)
                tooltipParts.Add($"{L("ToolNetMapTipSsdpDevice")}: {host.SsdpInfo.DeviceType}");
            if (host.SsdpInfo.Server is not null)
                tooltipParts.Add($"{L("ToolNetMapTipSsdpServer")}: {host.SsdpInfo.Server}");
        }
        if (host.NtlmInfo is not null)
        {
            if (host.NtlmInfo.DnsComputerName is not null)
                tooltipParts.Add($"{L("ToolNetMapTipNtlmDns")}: {host.NtlmInfo.DnsComputerName}");
            if (host.NtlmInfo.DnsDomainName is not null)
                tooltipParts.Add($"{L("ToolNetMapTipNtlmDomain")}: {host.NtlmInfo.DnsDomainName}");
            if (host.NtlmInfo.DnsForestName is not null)
                tooltipParts.Add($"{L("ToolNetMapTipNtlmForest")}: {host.NtlmInfo.DnsForestName}");
            if (host.NtlmInfo.OsBuild is not null)
                tooltipParts.Add($"{L("ToolNetMapTipNtlmBuild")}: {host.NtlmInfo.OsBuild}");
        }
        if (host.SshHashFingerprint is not null)
            tooltipParts.Add($"{L("ToolNetMapTipHashsh")}: {host.SshHashFingerprint}");
        if (host.SmbInfo is not null)
        {
            var dialectStr = host.SmbInfo.DialectRevision switch
            {
                0x0202 => "SMB 2.0.2",
                0x0210 => "SMB 2.1",
                0x0300 => "SMB 3.0",
                0x0302 => "SMB 3.0.2",
                0x0311 => "SMB 3.1.1",
                _ => $"SMB 0x{host.SmbInfo.DialectRevision:X4}"
            };
            tooltipParts.Add($"{L("ToolNetMapTipSmb")}: {dialectStr}{(host.SmbInfo.SigningRequired ? $" ({L("ToolNetMapTipSmbSigning")})" : "")}");
            if (host.SmbInfo.ServerGuid is not null)
                tooltipParts.Add($"{L("ToolNetMapTipSmbGuid")}: {host.SmbInfo.ServerGuid}");
            if (host.SmbInfo.ServerStartTime is not null)
            {
                var uptime = DateTime.UtcNow - host.SmbInfo.ServerStartTime.Value;
                tooltipParts.Add($"{L("ToolNetMapTipUptime")}: {uptime.Days}d {uptime.Hours}h");
            }
        }
        if (host.HttpFingerprint is not null)
        {
            if (host.HttpFingerprint.Framework is not null)
                tooltipParts.Add($"{L("ToolNetMapTipHttpFramework")}: {host.HttpFingerprint.Framework}");
            if (host.HttpFingerprint.ProductName is not null)
                tooltipParts.Add($"{L("ToolNetMapTipHttpProduct")}: {host.HttpFingerprint.ProductName}");
        }
        if (host.FaviconHash is not null)
        {
            var deviceName = FaviconHasher.KnownHashes.TryGetValue(host.FaviconHash.Value, out var name)
                ? name : null;
            tooltipParts.Add(deviceName is not null
                ? $"{L("ToolNetMapTipFavicon")}: {host.FaviconHash.Value} ({deviceName})"
                : $"{L("ToolNetMapTipFavicon")}: {host.FaviconHash.Value}");
        }
        if (host.HttpHeaders is { Count: > 0 })
        {
            foreach (var (key, value) in host.HttpHeaders)
                tooltipParts.Add($"{key}: {value}");
        }
        var detailTooltip = string.Join("\n", tooltipParts);

        var mdnsServicesList = host.MdnsServices is { Count: > 0 }
            ? string.Join("; ", host.MdnsServices) : null;

        return new CartographyRowViewModel
        {
            IpAddress = host.IpAddress,
            Hostname = host.Hostname ?? "\u2014",
            OsGuess = osGuess,
            PortSummary = string.Join(", ", openPorts),
            ServiceSummary = string.Join(", ", services),
            TlsStatus = tlsStatus,
            CertSubject = tlsCert?.Subject,
            CertExpires = tlsCert?.NotAfter.ToString("yyyy-MM-dd"),
            CertAlgorithm = tlsCert?.KeyAlgorithm,
            CertStatus = tlsCert is null ? null
                : tlsCert.IsExpired ? L("ToolNetMapCertExpired")
                : tlsCert.ExpiresSoon ? L("ToolNetMapCertExpiring")
                : L("ToolNetMapCertValid"),
            PrimaryRoleName = host.PrimaryRole?.Role
                ?? (host.Manufacturer is not null ? host.Manufacturer : "\u2014"),
            Confidence = host.PrimaryRole is not null ? $"{host.PrimaryRole.Confidence}%"
                : (host.Manufacturer is not null ? "MAC" : "\u2014"),
            DetailsSummary = detailsSummary,
            DetailTooltip = detailTooltip,
            VlanSegment = _vm.LastSnapshot?.DetectedVlans?
                .FirstOrDefault(v => v.MemberIps.Contains(host.IpAddress))
                is { } vlan ? $"VLAN {vlan.VlanId} ({vlan.Subnet})" : "\u2014",
            Manufacturer = host.Manufacturer ?? "\u2014",
            MacAddress = host.MacAddress ?? "\u2014",
            Latency = host.PingLatencyMs > 0 ? $"{host.PingLatencyMs}ms" : "\u2014",
            NetBiosName = host.NetBiosName,
            NetBiosDomain = host.NetBiosDomain,
            SnmpSysName = host.SnmpInfo?.SysName,
            SnmpSysDescr = host.SnmpInfo?.SysDescr,
            SnmpSysLocation = host.SnmpInfo?.SysLocation,
            MdnsServicesList = mdnsServicesList,
            SsdpDeviceType = host.SsdpInfo is not null
                ? CartographyEngine.FormatSsdpSummary(host.SsdpInfo) : null,
            SnmpObjectId = host.SnmpInfo?.SysObjectId,
            NtlmDns = host.NtlmInfo?.DnsComputerName,
            NtlmDomain = host.NtlmInfo?.DnsDomainName,
            NtlmBuild = host.NtlmInfo?.OsBuild,
            SshHashFingerprint = host.SshHashFingerprint,
            FaviconHashValue = host.FaviconHash?.ToString(),
            OpenPorts = openPortsList
        };
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_results.Count == 0 || _vm.LastSnapshot is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("FileDialogCsvFilter"),
            FileName = $"cartography_{_vm.Subnet.Trim().Replace('/', '_')}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
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
            Core.Logging.FileLogger.Warn($"NetworkCartography CSV export failed: {ex.Message}");
            _vm.StatusText = string.Format(L("StatusExportFailed"), ex.Message);
        }
    }

    private void OnExportDrawioClick(object sender, RoutedEventArgs e)
    {
        if (_vm.LastSnapshot is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Draw.io (*.drawio)|*.drawio",
            FileName = $"network-map_{_vm.LastSnapshot.Profile.Subnet.Replace('/', '-')}_{_vm.LastSnapshot.Timestamp:yyyyMMdd_HHmmss}.drawio"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var xml = _vm.GetDrawIoXml();
            if (xml is null)
            {
                return;
            }

            File.WriteAllText(dialog.FileName, xml, Encoding.UTF8);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"NetworkCartography Draw.io export failed: {ex.Message}");
            _vm.StatusText = string.Format(L("StatusExportFailed"), ex.Message);
        }
    }

    private void OnEditDiagramClick(object sender, RoutedEventArgs e)
    {
        if (_vm.LastSnapshot is null || _openToolAction is null)
        {
            return;
        }

        try
        {
            var xml = _vm.GetDrawIoXml();
            if (xml is null)
            {
                return;
            }

            var tempFile = Path.Combine(
                Path.GetTempPath(),
                $"heimdall_netmap_{DateTime.Now:yyyyMMdd_HHmmss}.drawio");
            File.WriteAllText(tempFile, xml, Encoding.UTF8);

            _openToolAction("DIAGRAM", L("PaletteToolDiagram"), new ToolContext(Argument: tempFile));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"NetworkCartography edit diagram failed: {ex.Message}");
            _vm.StatusText = string.Format(L("ToolNetMapErrorEditDiagramFailed"), ex.Message);
        }
    }

    private void PopulateHistory()
    {
        _historyList = _vm.GetHistoryList();
        CmbHistory.Items.Clear();
        CmbHistory.Items.Add(new ComboBoxItem { Content = L("ToolNetMapNoComparison") });
        foreach (var (_, timestamp, subnet) in _historyList)
        {
            CmbHistory.Items.Add(new ComboBoxItem
            {
                Content = $"{timestamp:yyyy-MM-dd HH:mm} - {subnet}"
            });
        }

        CmbHistory.SelectedIndex = 0;
    }

    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (CmbHistory.SelectedIndex <= 0 || _vm.LastSnapshot is null)
        {
            DiffPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var index = CmbHistory.SelectedIndex - 1;
        if (index >= _historyList.Count)
        {
            return;
        }

        _vm.CompareWithHistory(_historyList[index].FileName);
    }

    private void PopulateRouteSelector()
    {
        CmbRouteVia.Items.Clear();
        CmbRouteVia.Items.Add(L("ToolTunnelDirect"));
        if (_gateways is not null)
        {
            foreach (var gateway in _gateways)
            {
                CmbRouteVia.Items.Add($"{gateway.Name} ({gateway.Host}:{gateway.Port})");
            }
        }

        CmbRouteVia.SelectedIndex = 0;
    }

    private async void OnRouteViaChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbRouteVia.SelectedIndex <= 0 || _gateways is null)
        {
            _vm.SetGateway(null);
            TxtSubnet.ToolTip = null;
            return;
        }

        var index = CmbRouteVia.SelectedIndex - 1;
        var gateway = index < _gateways.Count ? _gateways[index] : null;
        _vm.SetGateway(gateway);
        if (gateway is null)
        {
            return;
        }

        _vm.StatusText = string.Format(L("ToolNetMapDetectingSubnet"), gateway.Name);

        try
        {
            var subnets = await _vm.DetectRemoteSubnetsAsync();
            if (subnets.Count > 0)
            {
                _vm.Subnet = subnets[0];
                _vm.StatusText = string.Format(L("ToolNetMapSubnetDetected"), subnets.Count, gateway.Name);
                TxtSubnet.ToolTip = subnets.Count > 1 ? string.Join("\n", subnets) : null;
            }
            else
            {
                _vm.StatusText = L("ToolNetMapSubnetDetectFailed");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Subnet detection failed for {gateway.Name}: {ex.Message}");
            _vm.StatusText = L("ToolNetMapSubnetDetectFailed");
        }
    }

    private static string? DetectLocalSubnet() => SubnetDetector.DetectLocalSubnet();

    private void OnResultsContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not CartographyRowViewModel row)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();

        if (_openToolAction is not null)
        {
            var hostItems = ToolContextMenuHelper.BuildHostActions(
                row.IpAddress, row.Hostname, row.OpenPorts, _localizer, _openToolAction);
            foreach (var item in hostItems)
            {
                menu.Items.Add(item);
            }
        }
        else
        {
            var copyIp = new MenuItem { Header = L("ToolCtxCopyIp") };
            copyIp.Click += (_, _) =>
            {
                try { Clipboard.SetText(row.IpAddress); }
                catch (System.Runtime.InteropServices.ExternalException) { }
            };
            menu.Items.Add(copyIp);

            if (!string.IsNullOrWhiteSpace(row.Hostname) && row.Hostname != "\u2014")
            {
                var copyHost = new MenuItem { Header = L("ToolCtxCopyHostname") };
                copyHost.Click += (_, _) =>
                {
                    try { Clipboard.SetText(row.Hostname); }
                    catch (System.Runtime.InteropServices.ExternalException) { }
                };
                menu.Items.Add(copyHost);
            }
        }

        var csvText = $"{row.IpAddress},{row.Hostname},{row.OsGuess},{row.PortSummary},{row.ServiceSummary},{row.PrimaryRoleName},{row.Manufacturer}";
        menu.Items.Add(ToolContextMenuHelper.BuildCopyRowAction(csvText, _localizer));
        menu.Items.Add(ToolContextMenuHelper.BuildCopyAllAction(ResultsGrid, _localizer));
        menu.Items.Add(new Separator());
        menu.Items.Add(ToolContextMenuHelper.BuildExportCsvAction(ResultsGrid, _localizer));

        ResultsGrid.ContextMenu = menu;
    }

    private void OnResultsLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is CartographyRowViewModel row &&
            !string.IsNullOrEmpty(row.DetailTooltip))
        {
            e.Row.ToolTip = row.DetailTooltip;
        }
    }

    private async void OnClearKbClick(object sender, RoutedEventArgs e)
    {
        var dialogService = (Application.Current as App)?.Services
            ?.GetService(typeof(IDialogService)) as IDialogService;
        if (dialogService is null)
        {
            return;
        }

        var confirmed = await dialogService.ShowConfirmAsync(
            L("ToolNetMapTitle"),
            L("ToolNetMapKbClearConfirm"),
            "info");
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _vm.ClearKbCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"KB clear failed: {ex.Message}");
            _vm.StatusText = string.Format(L("ToolNetMapErrorKbClearFailed"), ex.Message);
        }
    }

    private string L(string key) => _localizer?[key] ?? key;

    public bool CanClose() => !_vm.IsScanning;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TxtSubnet.KeyDown -= OnSubnetKeyDown;
        ResultsGrid.PreviewMouseRightButtonDown -= ToolContextMenuHelper.SelectRowOnRightClick;
        ResultsGrid.ContextMenuOpening -= OnResultsContextMenuOpening;
        ResultsGrid.LoadingRow -= OnResultsLoadingRow;
        SizeChanged -= OnViewSizeChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Row view model for DataGrid binding.
    /// </summary>
    internal sealed class CartographyRowViewModel
    {
        public string IpAddress { get; init; } = "";
        public string? Hostname { get; init; }
        public string OsGuess { get; init; } = "";
        public string PortSummary { get; init; } = "";
        public string ServiceSummary { get; init; } = "";
        public string TlsStatus { get; init; } = "";
        public string? CertSubject { get; init; }
        public string? CertExpires { get; init; }
        public string? CertAlgorithm { get; init; }
        public string? CertStatus { get; init; }
        public string PrimaryRoleName { get; init; } = "";
        public string Confidence { get; init; } = "";
        public string DetailsSummary { get; init; } = "";
        public string DetailTooltip { get; init; } = "";
        public string VlanSegment { get; set; } = "";
        public string Manufacturer { get; init; } = "";
        public string MacAddress { get; init; } = "";
        public string Latency { get; init; } = "";
        public string? NetBiosName { get; init; }
        public string? NetBiosDomain { get; init; }
        public string? SnmpSysName { get; init; }
        public string? SnmpSysDescr { get; init; }
        public string? SnmpSysLocation { get; init; }
        public string? MdnsServicesList { get; init; }
        public string? SsdpDeviceType { get; init; }
        public string? SnmpObjectId { get; init; }
        public string? NtlmDns { get; init; }
        public string? NtlmDomain { get; init; }
        public string? NtlmBuild { get; init; }
        public string? SshHashFingerprint { get; init; }
        public string? FaviconHashValue { get; init; }
        public IReadOnlyList<int> OpenPorts { get; init; } = [];
    }
}
