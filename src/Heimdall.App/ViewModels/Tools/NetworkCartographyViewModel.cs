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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Discovery;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// ViewModel for the network cartography tool.
/// </summary>
public sealed partial class NetworkCartographyViewModel : ObservableObject, IDisposable
{
    private const int LargeSubnetThreshold = 512;
    private const int DefaultMaxConcurrency = 50;
    private const int DefaultProbeTimeoutMs = 2000;

    private LocalizationManager? _localizer;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _subnetDetectCts;
    private readonly INetworkKnowledgeBaseStore _store;
    private readonly string _scanHistoryDirectory;
    private ICartographyScanner? _scanner;
    private NetworkKnowledgeBase? _knowledgeBase;
    private SshGatewayDto? _selectedGateway;
    private Task? _initialLoadTask;

    [ObservableProperty] private string _subnet = string.Empty;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _showError;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _statsText = string.Empty;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _progressIsIndeterminate;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMaximum = 100;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private bool _showResults;
    [ObservableProperty] private bool _showEmptyState = true;
    [ObservableProperty] private bool _autoDetect = true;
    [ObservableProperty] private ScanDepth _selectedDepth = ScanDepth.Quick;
    [ObservableProperty] private bool _skipPing;
    [ObservableProperty] private bool _reverseDns = true;
    [ObservableProperty] private bool _useKnowledgeBase;
    [ObservableProperty] private string _kbStatsText = string.Empty;
    [ObservableProperty] private bool _canClearKb;
    [ObservableProperty] private string _diffText = string.Empty;
    [ObservableProperty] private bool _showDiff;
    [ObservableProperty] private IReadOnlyList<HostScanResult> _hostResults = [];
    [ObservableProperty] private NetworkScanSnapshot? _lastSnapshot;
    [ObservableProperty] private int _largeSubnetHostCount;

    partial void OnSubnetChanged(string value)
    {
        UpdateLargeSubnetHostCount(value);
    }

    /// <param name="store">Where the knowledge base is read and written.</param>
    /// <param name="scanHistoryDirectory">
    /// Where scan snapshots are saved and listed. Required, with no default: a test that
    /// relied on one wrote its scans into the developer's own history.
    /// </param>
    public NetworkCartographyViewModel(INetworkKnowledgeBaseStore store, string scanHistoryDirectory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(scanHistoryDirectory);

        _store = store;
        _scanHistoryDirectory = scanHistoryDirectory;
    }

    public void Initialize(LocalizationManager? localizer)
    {
        _localizer = localizer;
        ResetResults();
        _initialLoadTask = LoadKbStatsAsync();
    }

    /// <summary>
    /// Waits for the asynchronous KB statistics load started by
    /// <see cref="Initialize(LocalizationManager?)"/>. Callers can use this
    /// when deterministic post-initialization state is required; destructive
    /// operations use it internally to avoid racing a pending initial load.
    /// </summary>
    public Task WaitForInitialLoadAsync() => _initialLoadTask ?? Task.CompletedTask;

    internal void SetScanner(ICartographyScanner scanner)
    {
        ArgumentNullException.ThrowIfNull(scanner);

        _scanner = scanner;
        _scanner.SetGateway(_selectedGateway);
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _selectedGateway = gateway;
        _scanner?.SetGateway(gateway);
    }

    public List<(string FileName, DateTime Timestamp, string Subnet)> GetHistoryList()
        => ScanHistoryManager.ListSnapshots(_scanHistoryDirectory);

    public async Task<List<string>> DetectRemoteSubnetsAsync()
    {
        if (_selectedGateway is null)
        {
            return [];
        }

        _subnetDetectCts?.Cancel();
        _subnetDetectCts?.Dispose();
        _subnetDetectCts = new CancellationTokenSource();

        try
        {
            var scanner = EnsureScanner();
            scanner.SetGateway(_selectedGateway);
            return await scanner.DetectRemoteSubnetsAsync(_subnetDetectCts.Token);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    public async Task LoadKbStatsAsync()
    {
        try
        {
            _knowledgeBase = await _store.LoadAsync();
        }
        catch
        {
            _knowledgeBase = KnowledgeBaseManager.CreateEmpty();
        }

        UpdateKbStats();
    }

    public string BuildCsvExport()
    {
        return LastSnapshot is null
            ? string.Empty
            : CartographyEngine.BuildCsvExport(LastSnapshot, CreateLocalize());
    }

    public string? GetDrawIoXml()
    {
        return LastSnapshot is null
            ? null
            : DrawIoExporter.Generate(LastSnapshot, CreateLocalize());
    }

    public void CompareWithHistory(string fileName)
    {
        if (LastSnapshot is null)
        {
            DiffText = string.Empty;
            ShowDiff = false;
            return;
        }

        var oldSnapshot = ScanHistoryManager.LoadSnapshot(_scanHistoryDirectory, fileName);
        if (oldSnapshot is null)
        {
            DiffText = string.Empty;
            ShowDiff = false;
            return;
        }

        var diff = ScanHistoryManager.ComputeDiff(oldSnapshot, LastSnapshot);
        var parts = new List<string>();

        if (diff.NewHosts.Count > 0)
        {
            parts.Add(string.Format(Lk("ToolNetMapDiffNew"), diff.NewHosts.Count));
        }

        if (diff.RemovedHosts.Count > 0)
        {
            parts.Add(string.Format(Lk("ToolNetMapDiffRemoved"), diff.RemovedHosts.Count));
        }

        if (diff.ModifiedHosts.Count > 0)
        {
            parts.Add(string.Format(Lk("ToolNetMapDiffChanged"), diff.ModifiedHosts.Count));
        }

        if (parts.Count == 0)
        {
            DiffText = string.Empty;
            ShowDiff = false;
            return;
        }

        DiffText = string.Join(" | ", parts);
        ShowDiff = true;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        var normalizedSubnet = NormalizeSubnet(Subnet);
        if (string.IsNullOrWhiteSpace(normalizedSubnet))
        {
            SetError(Lk("ToolNetMapErrorEmptySubnet"));
            return;
        }

        if (!string.Equals(normalizedSubnet, Subnet, StringComparison.Ordinal))
        {
            Subnet = normalizedSubnet;
        }

        var ipList = CartographyEngine.ParseCidr(normalizedSubnet);
        LargeSubnetHostCount = ipList.Count;
        if (ipList.Count == 0)
        {
            SetError(Lk("ToolNetMapErrorInvalidSubnet"));
            return;
        }

        ResetResults();
        IsScanning = true;
        ShowProgress = true;
        ProgressIsIndeterminate = true;
        ProgressValue = 0;
        ProgressMaximum = Math.Max(1, ipList.Count);
        ProgressText = string.Format(Lk("ToolNetMapScanProgress"), 0, ipList.Count);
        StatusText = string.Format(Lk("ToolNetMapStatusDiscovery"), 0, ipList.Count);

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            try
            {
                _knowledgeBase = await _store.LoadAsync();
            }
            catch
            {
                _knowledgeBase = KnowledgeBaseManager.CreateEmpty();
            }

            var scanner = EnsureScanner();
            scanner.SetGateway(_selectedGateway);

            var liveHosts = new List<HostScanResult>();
            IProgress<CartographyScanProgress> progress = new Progress<CartographyScanProgress>(update =>
                ApplyProgress(update, liveHosts));

            var profile = new ScanProfile(
                Subnet: normalizedSubnet,
                Depth: SelectedDepth,
                CustomPorts: null,
                MaxConcurrency: DefaultMaxConcurrency,
                TimeoutMs: DefaultProbeTimeoutMs,
                SkipPing: SkipPing,
                ReverseDns: ReverseDns);

            var snapshot = await scanner.ScanAsync(
                profile,
                UseKnowledgeBase ? _knowledgeBase : null,
                progress.Report,
                ct);

            try
            {
                await ScanHistoryManager.SaveSnapshotAsync(_scanHistoryDirectory, snapshot);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"NetworkCartography history save failed: {ex.Message}");
            }

            try
            {
                _knowledgeBase ??= await _store.LoadAsync();
                _knowledgeBase = KnowledgeBaseManager.MergeSnapshot(_knowledgeBase, snapshot);
                await _store.SaveAsync(_knowledgeBase);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"NetworkCartography KB merge failed: {ex.Message}");
            }

            LastSnapshot = snapshot;
            HostResults = snapshot.Hosts;
            ShowResults = HostResults.Count > 0;
            ShowEmptyState = HostResults.Count == 0;

            var totalServices = snapshot.Hosts.Sum(host => host.Services.Count);
            var totalRoles = snapshot.Hosts.Count(host => host.PrimaryRole is not null);
            var durationText = snapshot.Duration.TotalSeconds < 60
                ? $"{snapshot.Duration.TotalSeconds:F1}s"
                : $"{snapshot.Duration.TotalMinutes:F1}m";

            StatusText = snapshot.Hosts.Count == 0
                ? Lk("ToolNetMapNoHostsFound")
                : string.Format(
                    Lk("ToolNetMapStatusComplete"),
                    snapshot.Hosts.Count,
                    totalServices,
                    totalRoles,
                    durationText);

            var serviceNames = snapshot.Hosts
                .SelectMany(host => host.Services)
                .Where(service => service.ServiceName is not null)
                .Select(service => service.ServiceName!)
                .Distinct()
                .Count();
            StatsText = string.Format(
                Lk("ToolNetMapStats"),
                snapshot.Hosts.Count,
                totalServices,
                serviceNames);

            await LoadKbStatsAsync();
        }
        catch (OperationCanceledException)
        {
            // User cancelled. Keep partial results and current status.
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"NetworkCartography scan failed: {ex.Message}");
            SetError(string.Format(Lk("ToolNetMapErrorScanFailed"), ex.Message));
        }
        finally
        {
            IsScanning = false;
            ShowProgress = false;
            ShowEmptyState = HostResults.Count == 0 && !IsScanning;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task ClearKbAsync()
    {
        try
        {
            await WaitForInitialLoadAsync();
        }
        catch
        {
            // The initial stats load is best-effort; clear should still proceed.
        }

        _knowledgeBase = KnowledgeBaseManager.CreateEmpty();
        await _store.SaveAsync(_knowledgeBase);
        UpdateKbStats();
        StatusText = Lk("ToolNetMapKbCleared");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _subnetDetectCts?.Cancel();
        _subnetDetectCts?.Dispose();
        _subnetDetectCts = null;

        _scanner?.Cleanup();
    }

    private void ApplyProgress(CartographyScanProgress update, List<HostScanResult> liveHosts)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(liveHosts);

        ProgressIsIndeterminate = update.IsIndeterminate;
        if (update.Total > 0)
        {
            ProgressMaximum = update.Total;
            ProgressValue = Math.Min(update.Completed, update.Total);
        }

        if (!string.IsNullOrEmpty(update.StatusKey))
        {
            StatusText = FormatStatus(update.StatusKey, update.StatusArgs);
        }

        if (update.Phase is "Discovery" or "TunnelScan")
        {
            ProgressText = string.Format(
                Lk("ToolNetMapScanProgress"),
                update.Completed,
                update.Total);
        }

        if (update.CompletedHost is not null)
        {
            liveHosts.Add(update.CompletedHost);
            HostResults = liveHosts.ToList();
            ShowResults = HostResults.Count > 0;
            ShowEmptyState = HostResults.Count == 0 && !IsScanning;
        }
    }

    private ICartographyScanner EnsureScanner()
    {
        _scanner ??= new CartographyScanner();
        _scanner.SetGateway(_selectedGateway);
        return _scanner;
    }

    private void UpdateKbStats()
    {
        var isEmpty = _knowledgeBase is null || KnowledgeBaseManager.HostCount(_knowledgeBase) == 0;
        CanClearKb = !IsScanning && !isEmpty;
        KbStatsText = isEmpty
            ? Lk("ToolNetMapKbStatsNever")
            : string.Format(
                Lk("ToolNetMapKbStats"),
                KnowledgeBaseManager.HostCount(_knowledgeBase!),
                FormatTimeAgo(_knowledgeBase!.LastUpdated));
    }

    private string FormatTimeAgo(DateTime utcTime)
    {
        var elapsed = DateTime.UtcNow - utcTime;
        if (elapsed.TotalMinutes < 5)
        {
            return Lk("ToolNetMapKbTimeAgoJustNow");
        }

        if (elapsed.TotalHours < 1)
        {
            return string.Format(Lk("ToolNetMapKbTimeAgo1m"), (int)elapsed.TotalMinutes);
        }

        if (elapsed.TotalHours < 24)
        {
            return string.Format(Lk("ToolNetMapKbTimeAgo1h"), (int)elapsed.TotalHours);
        }

        return string.Format(Lk("ToolNetMapKbTimeAgo1d"), (int)elapsed.TotalDays);
    }

    private string NormalizeSubnet(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return trimmed.Contains('/', StringComparison.Ordinal)
            ? trimmed
            : $"{trimmed}/24";
    }

    private void UpdateLargeSubnetHostCount(string subnetValue)
    {
        var normalized = NormalizeSubnet(subnetValue);
        LargeSubnetHostCount = string.IsNullOrWhiteSpace(normalized)
            ? 0
            : CartographyEngine.ParseCidr(normalized).Count;
    }

    private void ResetResults()
    {
        ShowError = false;
        ShowDiff = false;
        DiffText = string.Empty;
        ShowResults = false;
        ShowEmptyState = false;
        HostResults = [];
        LastSnapshot = null;
        StatsText = string.Empty;
    }

    private void SetError(string message)
    {
        ResetResults();
        ShowError = true;
        StatusText = message;
        ShowEmptyState = true;
        ShowProgress = false;
        ProgressText = string.Empty;
    }

    private string FormatStatus(string statusKey, string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return Lk(statusKey);
        }

        return string.Format(Lk(statusKey), args.Cast<object>().ToArray());
    }

    private string Lk(string key) => _localizer?[key] ?? key;

    private Func<string, string> CreateLocalize() => key => _localizer?[key] ?? key;
}
