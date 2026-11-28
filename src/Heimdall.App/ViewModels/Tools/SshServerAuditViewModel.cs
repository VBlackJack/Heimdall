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

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;
using Heimdall.Ssh;
using Renci.SshNet;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// View-model backing the SSH Server Cryptographic Audit tool (<c>SSHSERVERAUDIT</c>).
/// Probes a target's RFC 4253 banner and <c>SSH_MSG_KEXINIT</c> algorithms directly or
/// through an SSH gateway chain, grades compliance, and generates exportable reports.
/// </summary>
public sealed partial class SshServerAuditViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, int, SshGatewayDto?, CancellationToken, Task<SshKexInitSnapshot>> _probeRunner;
    private LocalizationManager? _localizer;
    private SshGatewayDto? _gateway;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public SshServerAuditViewModel()
        : this(ProbeWithOptionalGatewayAsync)
    {
    }

    internal SshServerAuditViewModel(
        Func<string, int, SshGatewayDto?, CancellationToken, Task<SshKexInitSnapshot>> probeRunner)
    {
        _probeRunner = probeRunner ?? throw new ArgumentNullException(nameof(probeRunner));
    }

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private string _port = "22";

    [ObservableProperty]
    private bool _isAuditing;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _showError;

    [ObservableProperty]
    private bool _showEmptyState = true;

    [ObservableProperty]
    private bool _showResults;

    [ObservableProperty]
    private SshServerAuditReport? _report;

    public void Initialize(LocalizationManager? localizer)
    {
        _localizer = localizer;
    }

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    [RelayCommand]
    private async Task RunAuditAsync()
    {
        if (IsAuditing)
        {
            return;
        }

        string trimmedHost = Host?.Trim() ?? string.Empty;
        if (!InputValidator.IsValidSshHost(trimmedHost))
        {
            ShowErrorMessage(L("ToolSshServerAuditErrInvalidHost"));
            return;
        }

        if (!int.TryParse(Port?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int portNum)
            || !InputValidator.ValidatePortRange(portNum))
        {
            ShowErrorMessage(L("ToolSshServerAuditErrInvalidPort"));
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsAuditing = true;
        ShowError = false;
        ErrorMessage = string.Empty;

        try
        {
            SshKexInitSnapshot snapshot = await _probeRunner(trimmedHost, portNum, _gateway, ct)
                .ConfigureAwait(true);

            Report = SshServerAuditEngine.Evaluate(snapshot);
            ShowEmptyState = false;
            ShowResults = true;
        }
        catch (OperationCanceledException)
        {
            ShowErrorMessage(L("ToolSshServerAuditCancelled"));
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshServerAudit] probe failed for {trimmedHost}:{portNum}: {ex.Message}");
            ShowErrorMessage(string.Format(
                CultureInfo.CurrentCulture,
                L("ToolSshServerAuditErrProbeFailed"),
                ex.Message));
        }
        finally
        {
            IsAuditing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    private void ShowErrorMessage(string message)
    {
        ErrorMessage = message;
        ShowError = !string.IsNullOrWhiteSpace(message);
    }

    private string L(string key) => _localizer?[key] ?? key;

    private static async Task<SshKexInitSnapshot> ProbeWithOptionalGatewayAsync(
        string host,
        int port,
        SshGatewayDto? gateway,
        CancellationToken ct)
    {
        if (gateway is null)
        {
            return await SshKexInitProbe.ProbeAsync(host, port, host, port, cancellationToken: ct)
                .ConfigureAwait(false);
        }

        using var gatewayClient = await ToolGatewayConnector.ConnectAsync(gateway, ct)
            .ConfigureAwait(false);
        using var forward = new ForwardedPortLocal(LoopbackBinding.DefaultHost, 0, host, (uint)port);
        gatewayClient.AddForwardedPort(forward);
        try
        {
            forward.Start();
            return await SshKexInitProbe.ProbeAsync(
                LoopbackBinding.DefaultHost,
                (int)forward.BoundPort,
                host,
                port,
                cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (forward.IsStarted)
                {
                    forward.Stop();
                }

                gatewayClient.RemoveForwardedPort(forward);
            }
            catch (Exception)
            {
                // Best-effort cleanup on probe close.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
