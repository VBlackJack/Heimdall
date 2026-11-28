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
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the SSH host key verification dialog, including optional non-blocking
/// DNS SSHFP verification (advisory only; never auto-accepts TOFU).
/// </summary>
public partial class HostKeyPromptDialogViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private readonly ReadOnlyMemory<byte> _hostKeyBlob;
    private readonly bool _isViaGateway;
    private readonly ISshfpResolver? _sshfpResolver;

    public HostKeyPromptDialogViewModel(
        LocalizationManager localizer,
        string host,
        int port,
        string algorithm,
        string presentedFingerprint,
        string? storedFingerprint)
        : this(localizer, host, port, algorithm, presentedFingerprint, storedFingerprint, ReadOnlyMemory<byte>.Empty, false, null)
    {
    }

    public HostKeyPromptDialogViewModel(
        LocalizationManager localizer,
        string host,
        int port,
        string algorithm,
        string presentedFingerprint,
        string? storedFingerprint,
        ReadOnlyMemory<byte> hostKeyBlob,
        bool isViaGateway,
        ISshfpResolver? sshfpResolver)
    {
        _localizer = localizer;
        Host = host;
        Port = port;
        Algorithm = string.IsNullOrWhiteSpace(algorithm) ? "ssh-unknown" : algorithm;
        PresentedFingerprint = presentedFingerprint;
        StoredFingerprint = storedFingerprint;
        _hostKeyBlob = hostKeyBlob;
        _isViaGateway = isViaGateway;
        _sshfpResolver = sshfpResolver;

        if (_sshfpResolver is not null)
        {
            if (_isViaGateway)
            {
                SshfpStatusText = _localizer["HostKeySshfpSkippedGateway"];
                ShowSshfpStatus = true;
            }
            else
            {
                SshfpStatusText = _localizer["HostKeySshfpChecking"];
                ShowSshfpStatus = true;
            }
        }
    }

    public string Host { get; }

    public int Port { get; }

    public string Algorithm { get; }

    public string PresentedFingerprint { get; }

    public string? StoredFingerprint { get; }

    public bool IsMismatch => !string.IsNullOrWhiteSpace(StoredFingerprint);

    public string HeaderTextKey => IsMismatch
        ? "HostKeyMismatchTitle"
        : "HostKeyFirstUseTitle";

    public string WarningTextKey => IsMismatch
        ? "HostKeyMismatchWarning"
        : "HostKeyFirstUseWarning";

    public string HeaderText => _localizer[HeaderTextKey];

    public string WarningText => _localizer.Format(WarningTextKey, Host, Port);

    public string EndpointText => $"{Host}:{Port}";

    public string AcceptButtonText => _localizer[
        IsMismatch ? "HostKeyAcceptDestructiveButton" : "HostKeyAcceptButton"];

    public string TrustOnceButtonText => _localizer["HostKeyTrustOnceButton"];

    public string RejectButtonText => _localizer["HostKeyRejectButton"];

    public bool TrustOnceIsDefault => false;

    public bool AcceptIsDefault => !IsMismatch;

    public bool RejectIsDefault => IsMismatch;

    [ObservableProperty]
    private bool _showSshfpStatus;

    [ObservableProperty]
    private string _sshfpStatusText = string.Empty;

    [ObservableProperty]
    private bool _isSshfpMatch;

    [ObservableProperty]
    private bool _isSshfpMismatch;

    public HostKeyDecision? Decision { get; private set; }

    public event Action<bool>? CloseRequested;

    /// <summary>
    /// Runs the non-blocking DNS SSHFP lookup when a resolver is configured and the connection
    /// is direct. Always advisory; never modifies the default button or auto-accepts.
    /// </summary>
    public async Task RunSshfpLookupAsync(CancellationToken ct)
    {
        if (_sshfpResolver is null || _isViaGateway)
        {
            return;
        }

        try
        {
            var result = await _sshfpResolver
                .VerifyHostKeyAsync(Host, Algorithm, PresentedFingerprint, _hostKeyBlob, ct)
                .ConfigureAwait(true);

            ApplySshfpResult(result);
        }
        catch (OperationCanceledException)
        {
            // Dialog closed or lookup cancelled.
        }
        catch (Exception ex)
        {
            FileLogger.Debug($"[HostKeyPrompt] SSHFP lookup failed: {ex.Message}");
            ShowSshfpStatus = false;
        }
    }

    internal void ApplySshfpResult(SshfpVerificationResult result)
    {
        IsSshfpMatch = false;
        IsSshfpMismatch = false;

        switch (result.Status)
        {
            case SshfpVerificationStatus.Matched:
                IsSshfpMatch = true;
                ShowSshfpStatus = true;
                SshfpStatusText = result.AuthenticatedData
                    ? _localizer[result.UsedLoopbackResolver ? "HostKeySshfpMatchDnssecLoopback" : "HostKeySshfpMatchDnssecResolver"]
                    : _localizer["HostKeySshfpMatchUnsigned"];
                break;

            case SshfpVerificationStatus.Mismatch:
                IsSshfpMismatch = true;
                ShowSshfpStatus = true;
                SshfpStatusText = _localizer["HostKeySshfpMismatchWarning"];
                break;

            case SshfpVerificationStatus.NotFound:
                ShowSshfpStatus = true;
                SshfpStatusText = _localizer["HostKeySshfpNotFound"];
                break;

            case SshfpVerificationStatus.Skipped:
            case SshfpVerificationStatus.Error:
            default:
                ShowSshfpStatus = false;
                SshfpStatusText = string.Empty;
                break;
        }
    }

    [RelayCommand]
    private void Accept()
    {
        Decision = HostKeyDecision.Accept;
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void TrustOnce()
    {
        Decision = HostKeyDecision.TrustOnce;
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Reject()
    {
        Decision = HostKeyDecision.Reject;
        CloseRequested?.Invoke(false);
    }

    [RelayCommand]
    private void CopyFingerprint()
    {
        try
        {
            System.Windows.Clipboard.SetText(PresentedFingerprint);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[HostKeyPrompt] clipboard copy failed: {ex.Message}");
        }
    }
}
