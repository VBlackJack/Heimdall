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

using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Renci.SshNet;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// Abstraction for certificate retrieval. Allows test injection of
/// fake results without real network I/O.
/// </summary>
public interface ICertProber
{
    /// <summary>
    /// Retrieves a certificate from a host:port. Returns null on failure.
    /// Caller must dispose the returned <see cref="CertProbeResult"/>.
    /// </summary>
    Task<CertProbeResult?> ProbeAsync(string host, int port, CancellationToken ct);

    /// <summary>
    /// Cleans up any shared resources (e.g. SSH tunnel).
    /// </summary>
    void Cleanup();
}

/// <summary>
/// ViewModel for the Certificate Inspector tool. Supports single-port
/// inspection and multi-port scanning with progress and cancellation.
/// </summary>
public sealed partial class CertInspectorViewModel : ObservableObject, IDisposable
{
    private LocalizationManager? _localizer;
    private CancellationTokenSource? _cts;
    private SshGatewayDto? _selectedGateway;
    private ICertProber? _prober;

    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _port = string.Empty;
    [ObservableProperty] private string _customPorts = string.Empty;
    [ObservableProperty] private string _selectedProfile = "quick";

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _showError;
    [ObservableProperty] private bool _showEmptyState = true;

    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private int _progressMax;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private string _progressPercent = "0%";
    [ObservableProperty] private string _progressCount = string.Empty;

    [ObservableProperty] private bool _showSingleResult;
    [ObservableProperty] private CertInspectionSummary? _singleResult;
    [ObservableProperty] private string _singleDetailsText = string.Empty;

    [ObservableProperty] private bool _showScanResults;
    [ObservableProperty] private bool _showScanNoResults;
    [ObservableProperty] private bool _showScanFooter;
    [ObservableProperty] private IReadOnlyList<CertInspectionSummary> _scanResults = [];
    [ObservableProperty] private string _scanSummaryText = string.Empty;

    /// <summary>
    /// True when Port is empty, triggering multi-port scan mode.
    /// </summary>
    public bool IsScanMode => string.IsNullOrWhiteSpace(Port);

    partial void OnPortChanged(string value)
    {
        OnPropertyChanged(nameof(IsScanMode));
    }

    public void Initialize(LocalizationManager? localizer)
    {
        _localizer = localizer;
    }

    internal void SetProber(ICertProber prober)
    {
        _prober = prober;
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _selectedGateway = gateway;
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsChecking)
            return;

        if (IsScanMode)
            await RunScanAsync();
        else
            await RunSingleCheckAsync();
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    private async Task RunSingleCheckAsync()
    {
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

        if (!int.TryParse(Port.Trim(), out var port) || port is <= 0 or > 65535)
        {
            SetError(Lk("ToolCertErrorInvalidPort"));
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource(CertInspectorEngine.ConnectionTimeout);

        ResetState();
        IsChecking = true;

        var prober = _prober ?? new DefaultProber(_selectedGateway);
        var ownsProber = _prober is null;

        try
        {
            var probeResult = await prober.ProbeAsync(host, port, _cts.Token);
            if (probeResult is null)
            {
                SetError(string.Format(Lk("ToolCertErrorConnection"), "No certificate received"));
                return;
            }

            using (probeResult)
            {
                var summary = CertInspectorEngine.BuildSummary(probeResult, host, port);
                SingleResult = summary;
                SingleDetailsText = CertInspectorEngine.BuildDetailsText(summary, host, CreateLocalize());
                ShowSingleResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            SetError(Lk("ToolCertErrorTimeout"));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"CertInspector certificate retrieval failed: {ex.Message}");
            var errorMessage = _selectedGateway is not null
                ? string.Format(Lk("ToolTunnelFailed"), ex.Message)
                : string.Format(Lk("ToolCertErrorConnection"), ex.Message);
            SetError(errorMessage);
        }
        finally
        {
            if (ownsProber)
                prober.Cleanup();

            IsChecking = false;
        }
    }

    private async Task RunScanAsync()
    {
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

        var ports = GetScanPorts();
        if (ports.Count == 0)
        {
            SetError(Lk("ToolCertScanErrorCustomPorts"));
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ResetState();
        ShowProgress = true;
        ProgressMax = ports.Count;
        ProgressValue = 0;
        ProgressPercent = "0%";
        ProgressCount = string.Format(Lk("ToolCertScanProgress"), 0, ports.Count);
        IsChecking = true;

        var prober = _prober ?? new DefaultProber(_selectedGateway);
        var ownsProber = _prober is null;
        var collectedResults = new List<CertInspectionSummary>();
        var fatalError = false;
        IProgress<(int Completed, int Total)> progress = new Progress<(int Completed, int Total)>((state) =>
        {
            ProgressValue = state.Completed;
            ProgressPercent = $"{(int)(state.Completed * 100.0 / state.Total)}%";
            ProgressCount = string.Format(Lk("ToolCertScanProgress"), state.Completed, state.Total);
        });

        var completed = 0;

        try
        {
            using var semaphore = new SemaphoreSlim(CertInspectorEngine.MaxConcurrentProbes);

            var tasks = ports.Select(async port =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    CertProbeResult? probeResult = null;

                    try
                    {
                        using var portTimeout = new CancellationTokenSource(CertInspectorEngine.PerPortTimeout);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, portTimeout.Token);
                        probeResult = await prober.ProbeAsync(host, port, linked.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Per-port timeout, skip this port.
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Non-TLS or unreachable port, skip silently.
                    }

                    try
                    {
                        if (probeResult is not null)
                        {
                            using (probeResult)
                            {
                                var summary = CertInspectorEngine.BuildSummary(probeResult, host, port);
                                lock (collectedResults)
                                {
                                    collectedResults.Add(summary);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Core.Logging.FileLogger.Warn(
                            $"CertInspector failed to process certificate on port {port}: {ex.Message}");
                    }

                    var finished = Interlocked.Increment(ref completed);
                    progress.Report((finished, ports.Count));
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // User stop; partial results will be published below.
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"CertInspector scan failed: {ex.Message}");
            SetError(string.Format(Lk("ToolCertErrorConnection"), ex.Message));
            fatalError = true;
        }
        finally
        {
            if (ownsProber)
                prober.Cleanup();

            IsChecking = false;
            ShowProgress = false;
        }

        if (fatalError)
            return;

        ShowScanResults = true;

        List<CertInspectionSummary> sorted;
        lock (collectedResults)
        {
            sorted = collectedResults.OrderBy(result => result.Port).ToList();
        }

        if (sorted.Count == 0)
        {
            ShowScanNoResults = true;
        }
        else
        {
            ScanResults = sorted;
            ScanSummaryText = string.Format(Lk("ToolCertScanFound"), sorted.Count, ports.Count);
            ShowScanFooter = true;
        }
    }

    /// <summary>
    /// Returns the CSV content for the current scan results.
    /// The view handles the save dialog and file write.
    /// </summary>
    public string BuildCsvExport()
    {
        return CertInspectorEngine.BuildScanCsv(
            ScanResults,
            CreateLocalize(),
            port => NetworkToolPresets.GetTlsServiceLabel(port));
    }

    /// <summary>
    /// Returns the combined report text for all scan results.
    /// </summary>
    public string BuildAllDetailsText()
    {
        var localize = CreateLocalize();
        var builder = new StringBuilder();
        foreach (var item in ScanResults)
        {
            var service = NetworkToolPresets.GetTlsServiceLabel(item.Port);
            builder.AppendLine($"=== {string.Format(Lk("ToolCertScanPortHeader"), item.Port, service)} ===");
            builder.AppendLine(CertInspectorEngine.BuildDetailsText(item, Host.Trim(), localize));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _prober?.Cleanup();
    }

    private List<int> GetScanPorts() => SelectedProfile switch
    {
        "extended" => [.. NetworkToolPresets.TlsExtendedScanPorts],
        "custom" => CertInspectorEngine.ParsePorts(CustomPorts),
        _ => [.. NetworkToolPresets.TlsQuickScanPorts]
    };

    private void ResetState()
    {
        ShowError = false;
        ErrorMessage = string.Empty;
        ShowSingleResult = false;
        ShowScanResults = false;
        ShowScanNoResults = false;
        ShowScanFooter = false;
        ShowProgress = false;
        ShowEmptyState = false;
        SingleResult = null;
        SingleDetailsText = string.Empty;
        ScanResults = [];
        ScanSummaryText = string.Empty;
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

    /// <summary>
    /// Default prober: direct via <see cref="CertInspectorEngine"/>, tunnel via SSH.NET.
    /// </summary>
    private sealed class DefaultProber : ICertProber
    {
        private readonly SshGatewayDto? _gateway;
        private SshClient? _tunnelClient;
        private SemaphoreSlim? _commandLock;

        public DefaultProber(SshGatewayDto? gateway)
        {
            _gateway = gateway;
            _commandLock = gateway is not null ? new SemaphoreSlim(1, 1) : null;
        }

        public async Task<CertProbeResult?> ProbeAsync(string host, int port, CancellationToken ct)
        {
            if (_gateway is null)
            {
                try
                {
                    return await CertInspectorEngine.RetrieveCertificateAsync(host, port, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return null;
                }
            }

            await _commandLock!.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureTunnelAsync(ct).ConfigureAwait(false);
                return await Task.Run(() => RetrieveCertificateViaTunnel(_tunnelClient!, host, port, ct), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public void Cleanup()
        {
            if (_tunnelClient is not null)
            {
                try
                {
                    _tunnelClient.Disconnect();
                }
                catch
                {
                    // Best effort.
                }

                _tunnelClient.Dispose();
                _tunnelClient = null;
            }

            _commandLock?.Dispose();
            _commandLock = null;
        }

        private async Task EnsureTunnelAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _tunnelClient ??= await ToolGatewayConnector.ConnectAsync(_gateway!, ct).ConfigureAwait(false);
        }

        private static CertProbeResult RetrieveCertificateViaTunnel(
            SshClient sshClient,
            string host,
            int port,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var escapedHost = InputValidator.EscapeShellArg(host);
            using var command = sshClient.CreateCommand(
                $"echo | openssl s_client -connect {escapedHost}:{port} -servername {escapedHost} 2>/dev/null");
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            command.Execute();
            var pemOutput = command.Result ?? string.Empty;

            const string beginMarker = "-----BEGIN CERTIFICATE-----";
            const string endMarker = "-----END CERTIFICATE-----";
            var beginIndex = pemOutput.IndexOf(beginMarker, StringComparison.Ordinal);
            var endIndex = pemOutput.IndexOf(endMarker, StringComparison.Ordinal);
            if (beginIndex < 0 || endIndex < 0)
                throw new InvalidOperationException("No certificate received via tunnel.");

            var pemBlock = pemOutput[beginIndex..(endIndex + endMarker.Length)];
            var base64 = pemBlock
                .Replace(beginMarker, string.Empty, StringComparison.Ordinal)
                .Replace(endMarker, string.Empty, StringComparison.Ordinal)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal);

            var certBytes = Convert.FromBase64String(base64);
            var cert = X509CertificateLoader.LoadCertificate(certBytes);

            var tlsProtocol = SslProtocols.None;
            if (pemOutput.Contains("TLSv1.3", StringComparison.OrdinalIgnoreCase))
                tlsProtocol = SslProtocols.Tls13;
            else if (pemOutput.Contains("TLSv1.2", StringComparison.OrdinalIgnoreCase))
                tlsProtocol = SslProtocols.Tls12;

            return new CertProbeResult
            {
                Certificate = cert,
                Protocol = tlsProtocol,
                Chain = CertInspectorEngine.BuildChain(cert)
            };
        }
    }
}
