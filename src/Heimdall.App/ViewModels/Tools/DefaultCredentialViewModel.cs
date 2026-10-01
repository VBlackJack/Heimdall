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
using Heimdall.Core.Localization;
using Heimdall.Core.Security;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// ViewModel for the default credential scanner tool.
/// </summary>
public sealed partial class DefaultCredentialViewModel : ObservableObject, IDisposable
{
    private sealed class ScanProgressUpdate
    {
        public required string Status { get; init; }

        public required bool IsIndeterminate { get; init; }

        public required int Completed { get; init; }

        public required int Total { get; init; }

        public IReadOnlyList<CredTestResultDto>? ResultsSnapshot { get; init; }
    }

    private LocalizationManager? _localizer;
    private CancellationTokenSource? _cts;
    private SshGatewayDto? _selectedGateway;
    private ICredentialScanner? _scanner;
    private Func<CancellationToken, Task> _delayProvider =
        ct => Task.Delay(DefaultCredentialEngine.RateLimitDelayMs, ct);

    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _showError;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _progressIsIndeterminate;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private string _progressStatus = string.Empty;
    [ObservableProperty] private bool _showEmptyState = true;
    [ObservableProperty] private bool _showResults;
    [ObservableProperty] private bool _autoDetect = true;
    [ObservableProperty] private bool _showPasswords;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private IReadOnlyList<CredTestResultDto> _scanResults = [];

    public void Initialize(LocalizationManager? localizer)
    {
        _localizer = localizer;
        AutoDetect = true;
        ShowPasswords = false;
        ResetState();
        ShowEmptyState = true;
    }

    internal void SetScanner(ICredentialScanner scanner)
    {
        ArgumentNullException.ThrowIfNull(scanner);

        if (!ReferenceEquals(_scanner, scanner))
        {
            _scanner?.Cleanup();
        }

        _scanner = scanner;
    }

    internal void SetDelayProvider(Func<CancellationToken, Task> delayProvider)
    {
        _delayProvider = delayProvider ?? throw new ArgumentNullException(nameof(delayProvider));
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _selectedGateway = gateway;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        var host = Host.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            SetError(Lk("ToolValidationHostRequired"));
            return;
        }

        if (!InputValidator.Validate(host, "Address"))
        {
            SetError(Lk("ErrorInvalidHost"));
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ResetState();
        IsScanning = true;
        ShowResults = true;

        var scanner = _scanner ?? new DefaultCredentialScanner(_selectedGateway);
        var ownsScanner = _scanner is null;

        try
        {
            var detectedServices = await DetectServicesAsync(scanner, host, ct);
            if (detectedServices.Count == 0)
            {
                ScanResults = [];
                SummaryText = DefaultCredentialEngine.BuildSummaryText(ScanResults, CreateLocalize(), _localizer);
                return;
            }

            await ExecuteCredentialScanAsync(scanner, host, detectedServices, ct);
            SummaryText = DefaultCredentialEngine.BuildSummaryText(ScanResults, CreateLocalize(), _localizer);
        }
        catch (OperationCanceledException)
        {
            SummaryText = DefaultCredentialEngine.BuildSummaryText(ScanResults, CreateLocalize(), _localizer);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"DefaultCredential scanner failed: {ex.Message}");
            var message = _selectedGateway is not null
                ? string.Format(Lk("ToolTunnelFailed"), ex.Message)
                : ex.Message;
            SetError(message);
        }
        finally
        {
            if (ownsScanner)
            {
                scanner.Cleanup();
            }

            IsScanning = false;
            ShowProgress = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Returns CSV content for export.
    /// </summary>
    public string BuildCsvExport()
    {
        return DefaultCredentialEngine.BuildCsvExport(ScanResults, CreateLocalize());
    }

    /// <summary>
    /// Returns plain-text report content for clipboard copy.
    /// </summary>
    public string BuildReportText()
    {
        return DefaultCredentialEngine.BuildReportText(ScanResults, CreateLocalize());
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _scanner?.Cleanup();
    }

    private async Task<List<(int Port, string Service)>> DetectServicesAsync(
        ICredentialScanner scanner,
        string host,
        CancellationToken ct)
    {
        if (!AutoDetect)
        {
            return [.. DefaultCredentialPresets.ServicePorts.Select(kv => (kv.Key, kv.Value))];
        }

        ShowProgress = true;
        ProgressIsIndeterminate = true;
        ProgressValue = 0;
        ProgressStatus = Lk("ToolDefCredDetecting");

        var detectedServices = new List<(int Port, string Service)>();
        var portsToScan = DefaultCredentialPresets.ServicePorts.Keys.ToList();
        using var semaphore = new SemaphoreSlim(_selectedGateway is not null ? 5 : 20);

        var probeTasks = portsToScan.Select(async port =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var isOpen = await scanner.ProbePortAsync(host, port, ct).ConfigureAwait(false);
                return isOpen ? (Port: port, Service: DefaultCredentialPresets.ServicePorts[port]) : ((int Port, string Service)?)null;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var probeResults = await Task.WhenAll(probeTasks).ConfigureAwait(false);
        detectedServices.AddRange(probeResults.Where(result => result is not null).Select(result => result!.Value));
        return detectedServices;
    }

    private async Task ExecuteCredentialScanAsync(
        ICredentialScanner scanner,
        string host,
        List<(int Port, string Service)> detectedServices,
        CancellationToken ct)
    {
        var serviceGroups = detectedServices
            .GroupBy(service => service.Service, StringComparer.Ordinal)
            .ToList();
        var totalTests = serviceGroups.Sum(group =>
            group.Sum(svc => DefaultCredentialPresets.CredentialsByService.GetValueOrDefault(svc.Service)?.Count ?? 0));
        var completedTests = 0;
        var collectedResults = new List<CredTestResultDto>();
        var sync = new object();

        IProgress<ScanProgressUpdate> progress = new Progress<ScanProgressUpdate>(update =>
        {
            ShowProgress = true;
            ProgressIsIndeterminate = update.IsIndeterminate;
            ProgressStatus = update.Status;
            ProgressValue = update.Total > 0
                ? (int)(update.Completed * 100.0 / update.Total)
                : 0;

            if (update.ResultsSnapshot is not null)
            {
                ScanResults = update.ResultsSnapshot;
                SummaryText = DefaultCredentialEngine.BuildSummaryText(ScanResults, CreateLocalize(), _localizer);
            }
        });

        using var serviceSemaphore = new SemaphoreSlim(DefaultCredentialEngine.MaxConcurrentServices);
        var serviceTasks = serviceGroups.Select(async group =>
        {
            await serviceSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var (port, service) in group)
                {
                    ct.ThrowIfCancellationRequested();
                    var credentials = DefaultCredentialPresets.CredentialsByService.GetValueOrDefault(service);
                    if (credentials is null)
                    {
                        continue;
                    }

                    foreach (var (user, pass) in credentials)
                    {
                        ct.ThrowIfCancellationRequested();

                        var statusText = string.Format(Lk("ToolDefCredTesting"), service, user, pass);
                        progress.Report(new ScanProgressUpdate
                        {
                            Status = statusText,
                            IsIndeterminate = false,
                            Completed = completedTests,
                            Total = totalTests,
                        });

                        var result = await scanner.TestCredentialAsync(host, port, service, user, pass, ct)
                            .ConfigureAwait(false);

                        IReadOnlyList<CredTestResultDto> snapshot;
                        lock (sync)
                        {
                            collectedResults.Add(result);
                            snapshot = [.. collectedResults
                                .OrderBy(item => item.Port)
                                .ThenBy(item => item.Service, StringComparer.Ordinal)
                                .ThenBy(item => item.Username, StringComparer.Ordinal)];
                        }

                        var finished = Interlocked.Increment(ref completedTests);
                        progress.Report(new ScanProgressUpdate
                        {
                            Status = statusText,
                            IsIndeterminate = false,
                            Completed = finished,
                            Total = totalTests,
                            ResultsSnapshot = snapshot,
                        });

                        await _delayProvider(ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                serviceSemaphore.Release();
            }
        });

        await Task.WhenAll(serviceTasks).ConfigureAwait(false);
    }

    private void ResetState()
    {
        ShowError = false;
        ErrorMessage = string.Empty;
        ShowProgress = false;
        ProgressIsIndeterminate = false;
        ProgressValue = 0;
        ProgressStatus = string.Empty;
        ShowResults = false;
        SummaryText = string.Empty;
        ScanResults = [];
        ShowEmptyState = false;
    }

    private void SetError(string message)
    {
        ResetState();
        ErrorMessage = message;
        ShowError = true;
    }

    private string Lk(string key) => _localizer?[key] ?? key;

    private Func<string, string> CreateLocalize()
        => key => _localizer?[key] ?? key;
}
