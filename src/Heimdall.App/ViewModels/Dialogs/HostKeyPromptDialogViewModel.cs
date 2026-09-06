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
/// ViewModel for the SSH host key verification dialog.
/// </summary>
public partial class HostKeyPromptDialogViewModel(
    LocalizationManager localizer,
    string host,
    int port,
    string algorithm,
    string presentedFingerprint,
    string? storedFingerprint) : ObservableObject
{
    private readonly LocalizationManager _localizer = localizer;

    public string Host { get; } = host;

    public int Port { get; } = port;

    public string Algorithm { get; } = string.IsNullOrWhiteSpace(algorithm)
        ? "ssh-unknown"
        : algorithm;

    public string PresentedFingerprint { get; } = presentedFingerprint;

    public string? StoredFingerprint { get; } = storedFingerprint;

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

    /// <summary>
    /// "Trust this session" never answers Enter: on a first-use prompt Accept does,
    /// and on a changed key nothing that connects may be one keystroke away.
    /// </summary>
    public bool TrustOnceIsDefault => false;

    public bool AcceptIsDefault => !IsMismatch;

    /// <summary>
    /// On a changed key the default button is Reject, as OpenSSH refuses outright:
    /// the Enter reflex trained by the first-use prompt must not connect to a host
    /// whose key no longer matches the stored one.
    /// </summary>
    public bool RejectIsDefault => IsMismatch;

    public HostKeyDecision? Decision { get; private set; }

    public event Action<bool>? CloseRequested;

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
