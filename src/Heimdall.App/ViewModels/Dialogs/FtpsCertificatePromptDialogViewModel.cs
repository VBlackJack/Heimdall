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
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels.Dialogs;

public partial class FtpsCertificatePromptDialogViewModel(
    LocalizationManager localizer,
    FtpsCertificatePrompt prompt) : ObservableObject
{
    private readonly LocalizationManager _localizer = localizer;

    public string Host { get; } = prompt.Host;

    public int Port { get; } = prompt.Port;

    public string PresentedFingerprint { get; } = prompt.PresentedFingerprint;

    public string Subject { get; } = prompt.Subject;

    public string Issuer { get; } = prompt.Issuer;

    public string ValidationErrors { get; } = prompt.ValidationErrors;

    public DateTimeOffset NotBefore { get; } = prompt.NotBefore;

    public DateTimeOffset NotAfter { get; } = prompt.NotAfter;

    /// <remarks>
    /// This prompt is a first-use prompt only. A changed certificate is refused by the browser
    /// before any prompt can be asked, so the "certificate changed" wording this view model once
    /// carried described a screen that could not be shown.
    /// </remarks>
    public string HeaderText => _localizer["FtpsCertificateFirstUseTitle"];

    public string WarningText => _localizer.Format(
        "FtpsCertificateFirstUseWarning",
        Host,
        Port);

    public string EndpointText => $"{Host}:{Port}";

    public string ValidityText => string.Format(
        CultureInfo.CurrentCulture,
        "{0:g} - {1:g}",
        NotBefore.ToLocalTime(),
        NotAfter.ToLocalTime());

    public string AcceptButtonText => _localizer["FtpsCertificateAcceptButton"];

    public string TrustOnceButtonText => _localizer["FtpsCertificateTrustOnceButton"];

    public string RejectButtonText => _localizer["FtpsCertificateRejectButton"];

    /// <summary>"Trust this session" never answers Enter; on a first-use prompt Accept does.</summary>
    public bool TrustOnceIsDefault => false;

    public bool AcceptIsDefault => true;

    public FtpsCertificateDecision? Decision { get; private set; }

    public event Action<bool>? CloseRequested;

    [RelayCommand]
    private void Accept()
    {
        Decision = FtpsCertificateDecision.Accept;
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void TrustOnce()
    {
        Decision = FtpsCertificateDecision.TrustOnce;
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Reject()
    {
        Decision = FtpsCertificateDecision.Reject;
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
            FileLogger.Warn($"[FtpsCertificatePrompt] clipboard copy failed: {ex.Message}");
        }
    }
}
