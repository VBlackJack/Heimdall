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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Renci.SshNet;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// Probe abstraction for TLS audits. Tests can inject a fake implementation
/// to exercise the ViewModel without network I/O.
/// </summary>
internal interface ITlsProber
{
    Task<bool> TestProtocolAsync(string host, int port, SslProtocols protocol, CancellationToken ct);

    Task<bool> TestCipherSuiteAsync(string host, int port, TlsCipherSuite suite, CancellationToken ct);

    Task<X509Certificate2?> RetrieveCertificateAsync(string host, int port, CancellationToken ct);
}

/// <summary>
/// Thin ViewModel for the TLS audit tool. It validates input, coordinates direct
/// or tunneled probing, delegates analysis/reporting to <see cref="TlsAuditEngine"/>,
/// and exposes bindable state for the view.
/// </summary>
public sealed partial class TlsAuditViewModel : ObservableObject, IDisposable
{
    private LocalizationManager? _localizer;
    private CancellationTokenSource? _cts;
    private SshGatewayDto? _gateway;
    private ITlsProber? _prober;
    private bool _disposed;

    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _port = TlsAuditEngine.DefaultTlsPort.ToString();
    [ObservableProperty] private bool _isAuditing;
    [ObservableProperty] private TlsGrade _grade = TlsGrade.T;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _showResults;
    [ObservableProperty] private bool _showError;
    [ObservableProperty] private bool _showEmptyState = true;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _showCipherPanel;
    [ObservableProperty] private bool _showCertPanel;
    [ObservableProperty] private bool _showFindingsPanel;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _reportText = string.Empty;
    [ObservableProperty] private IReadOnlyList<TlsProtocolTestResult> _protocols = [];
    [ObservableProperty] private IReadOnlyList<TlsCipherInfo> _ciphers = [];
    [ObservableProperty] private IReadOnlyList<TlsAuditFinding> _findings = [];
    [ObservableProperty] private TlsCertificateSummary? _certificateSummary;

    public void Initialize(LocalizationManager? localizer)
    {
        _localizer = localizer;
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    internal void SetProber(ITlsProber prober)
    {
        _prober = prober;
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task RunAuditAsync()
    {
        if (_disposed || IsAuditing)
            return;

        var trimmedHost = Host.Trim();
        if (string.IsNullOrWhiteSpace(trimmedHost))
        {
            SetError(L("ToolValidationHostRequired"));
            return;
        }

        if (!InputValidator.Validate(trimmedHost, "Address"))
        {
            SetError(L("ErrorInvalidHost"));
            return;
        }

        if (!int.TryParse(Port.Trim(), out var parsedPort) || !InputValidator.ValidatePortRange(parsedPort))
        {
            SetError(L("ToolCertErrorInvalidPort"));
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ClearError();
        ClearResults();
        ShowEmptyState = false;
        ShowProgress = true;
        ProgressPercent = 0;
        ProgressText = string.Empty;
        IsAuditing = true;

        var prober = _prober ?? new DefaultProber(_gateway);
        var ownsProber = _prober is null;

        try
        {
            var protocolResults = new List<TlsProtocolTestResult>();
            var totalSteps = TlsAuditEngine.ProtocolDefinitions.Count + 2;
            var currentStep = 0;

            foreach (var definition in TlsAuditEngine.ProtocolDefinitions)
            {
                ct.ThrowIfCancellationRequested();
                ProgressText = string.Format(L("ToolTlsAuditProgress"), definition.Name);
                ProgressPercent = ++currentStep * 100.0 / totalSteps;

                var supported = await prober.TestProtocolAsync(trimmedHost, parsedPort, definition.Protocol, ct);
                protocolResults.Add(new TlsProtocolTestResult
                {
                    Name = definition.Name,
                    Supported = supported,
                    Rating = definition.Rating
                });
            }

            var cipherResults = new List<TlsCipherInfo>();
            if (protocolResults.Any(p => p is { Name: "TLS 1.2", Supported: true }))
            {
                ProgressText = L("ToolTlsAuditProgressCiphers");
                ProgressPercent = ++currentStep * 100.0 / totalSteps;

                foreach (var suite in TlsAuditEngine.KnownTls12CipherSuites)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await prober.TestCipherSuiteAsync(trimmedHost, parsedPort, suite, ct))
                        cipherResults.Add(TlsAuditEngine.BuildCipherInfo(suite));
                }
            }
            else
            {
                currentStep++;
            }

            ProgressPercent = ++currentStep * 100.0 / totalSteps;
            TlsCertificateSummary? summary = null;
            var certificate = await prober.RetrieveCertificateAsync(trimmedHost, parsedPort, ct);
            try
            {
                if (certificate is not null)
                    summary = TlsAuditEngine.ExtractCertificateSummary(certificate);
            }
            finally
            {
                certificate?.Dispose();
            }

            var findings = TlsAuditEngine.BuildFindings(protocolResults, cipherResults, Localize);
            var grade = TlsAuditEngine.CalculateGrade(protocolResults, cipherResults);
            var report = TlsAuditEngine.BuildReportText(
                grade,
                protocolResults,
                cipherResults,
                summary,
                findings,
                trimmedHost,
                parsedPort,
                Localize);

            Protocols = protocolResults;
            Ciphers = cipherResults;
            Findings = findings;
            CertificateSummary = summary;
            Grade = grade;
            ReportText = report;
            ShowCipherPanel = Ciphers.Count > 0;
            ShowCertPanel = CertificateSummary is not null;
            ShowFindingsPanel = Findings.Count > 0;
            ShowResults = true;
        }
        catch (OperationCanceledException)
        {
            SetError(L("ToolCertErrorTimeout"));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"TlsAudit failed: {ex.Message}");
            var template = _gateway is null ? L("ToolTlsAuditErrorConnection") : L("ToolTunnelFailed");
            SetError(string.Format(template, ex.Message));
        }
        finally
        {
            if (ownsProber && prober is IDisposable disposable)
                disposable.Dispose();

            ShowProgress = false;
            IsAuditing = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_prober is IDisposable disposable)
            disposable.Dispose();

        GC.SuppressFinalize(this);
    }

    private void SetError(string message)
    {
        ClearResults();
        ErrorMessage = message;
        ShowError = true;
        ShowResults = false;
        ShowEmptyState = false;
        ShowProgress = false;
    }

    private void ClearError()
    {
        ErrorMessage = string.Empty;
        ShowError = false;
    }

    private void ClearResults()
    {
        ShowResults = false;
        ShowCipherPanel = false;
        ShowCertPanel = false;
        ShowFindingsPanel = false;
        Grade = TlsGrade.T;
        Protocols = [];
        Ciphers = [];
        Findings = [];
        CertificateSummary = null;
        ReportText = string.Empty;
    }

    private string L(string key) => _localizer?[key] ?? key;

    private string Localize(string key) => L(key);

    private sealed class DefaultProber : ITlsProber, IDisposable
    {
        private readonly SshGatewayDto? _gateway;
        private SshClient? _sshClient;

        public DefaultProber(SshGatewayDto? gateway)
        {
            _gateway = gateway;
        }

        public Task<bool> TestProtocolAsync(string host, int port, SslProtocols protocol, CancellationToken ct)
        {
            if (_gateway is null)
                return TlsAuditEngine.TestProtocolAsync(host, port, protocol, ct);

            return Task.Run(() => TestProtocolViaTunnel(host, port, protocol, ct), ct);
        }

        public Task<bool> TestCipherSuiteAsync(string host, int port, TlsCipherSuite suite, CancellationToken ct)
        {
            if (_gateway is null)
                return TlsAuditEngine.TestCipherSuiteAsync(host, port, suite, ct);

            return Task.Run(() => TestCipherSuiteViaTunnel(host, port, suite, ct), ct);
        }

        public Task<X509Certificate2?> RetrieveCertificateAsync(string host, int port, CancellationToken ct)
        {
            if (_gateway is null)
                return TlsAuditEngine.RetrieveCertificateAsync(host, port, ct);

            return Task.Run(() => RetrieveCertificateViaTunnel(host, port, ct), ct);
        }

        public void Dispose()
        {
            if (_sshClient is null)
                return;

            try
            {
                if (_sshClient.IsConnected)
                    _sshClient.Disconnect();
            }
            catch
            {
                // Best effort disconnect.
            }

            _sshClient.Dispose();
            _sshClient = null;
        }

        private async Task<bool> TestProtocolViaTunnel(string host, int port, SslProtocols protocol, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var flag = protocol switch
            {
#pragma warning disable CA5397, CS0618, SYSLIB0039
                SslProtocols.Ssl3 => "-ssl3",
                SslProtocols.Tls => "-tls1",
                SslProtocols.Tls11 => "-tls1_1",
#pragma warning restore CA5397, CS0618, SYSLIB0039
                SslProtocols.Tls12 => "-tls1_2",
                SslProtocols.Tls13 => "-tls1_3",
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(flag))
                return false;

            try
            {
                var escapedHost = InputValidator.EscapeShellArg(host);
                using var command = (await EnsureTunnelClientAsync(ct).ConfigureAwait(false)).CreateCommand(
                    $"echo | openssl s_client -connect {escapedHost}:{port} {flag} 2>&1 | head -5");
                command.CommandTimeout = TlsAuditEngine.ConnectionTimeout;
                command.Execute();
                var output = command.Result ?? string.Empty;

                return output.Contains("CONNECTED", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("error", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("wrong version", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("no protocols available", StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TestCipherSuiteViaTunnel(string host, int port, TlsCipherSuite suite, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (!TlsAuditEngine.OpenSslCipherNames.TryGetValue(suite, out var opensslName))
                return false;

            try
            {
                var escapedHost = InputValidator.EscapeShellArg(host);
                using var command = (await EnsureTunnelClientAsync(ct).ConfigureAwait(false)).CreateCommand(
                    $"echo | openssl s_client -connect {escapedHost}:{port} -tls1_2 -cipher {opensslName} 2>&1 | head -5");
                command.CommandTimeout = TlsAuditEngine.ConnectionTimeout;
                command.Execute();
                var output = command.Result ?? string.Empty;

                return output.Contains("CONNECTED", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("error", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("handshake failure", StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private async Task<X509Certificate2?> RetrieveCertificateViaTunnel(string host, int port, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var escapedHost = InputValidator.EscapeShellArg(host);
            using var command = (await EnsureTunnelClientAsync(ct).ConfigureAwait(false)).CreateCommand(
                $"echo | openssl s_client -connect {escapedHost}:{port} -servername {escapedHost} 2>/dev/null");
            command.CommandTimeout = TlsAuditEngine.ConnectionTimeout;
            command.Execute();
            var pemOutput = command.Result ?? string.Empty;

            const string beginMarker = "-----BEGIN CERTIFICATE-----";
            const string endMarker = "-----END CERTIFICATE-----";

            var beginIndex = pemOutput.IndexOf(beginMarker, StringComparison.Ordinal);
            var endIndex = pemOutput.IndexOf(endMarker, StringComparison.Ordinal);
            if (beginIndex < 0 || endIndex < 0)
                return null;

            var pemBlock = pemOutput[beginIndex..(endIndex + endMarker.Length)];
            var base64 = pemBlock
                .Replace(beginMarker, string.Empty, StringComparison.Ordinal)
                .Replace(endMarker, string.Empty, StringComparison.Ordinal)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal);

            var certBytes = Convert.FromBase64String(base64);
            return X509CertificateLoader.LoadCertificate(certBytes);
        }

        private async Task<SshClient> EnsureTunnelClientAsync(CancellationToken ct)
        {
            if (_sshClient is { IsConnected: true })
                return _sshClient;

            if (_gateway is null)
                throw new InvalidOperationException("No gateway is configured.");

            _sshClient?.Dispose();
            _sshClient = await ToolGatewayConnector.ConnectAsync(_gateway, ct).ConfigureAwait(false);
            return _sshClient;
        }
    }
}
