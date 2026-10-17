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

/// <summary>
/// The words a certificate prompt uses, so one dialog serves FTPS and web consoles alike.
/// </summary>
/// <param name="HeaderKey">Localization key of the title.</param>
/// <param name="WarningKey">Localization key of the warning; formatted with the host and the port.</param>
/// <param name="AcceptIsDefault">
/// Whether Enter trusts the certificate. False when the certificate replaces a trusted one: a
/// reflex keystroke must not accept what may be an interception.
/// </param>
public sealed record CertificatePromptWording(string HeaderKey, string WarningKey, bool AcceptIsDefault)
{
    public static CertificatePromptWording Ftps { get; } =
        new("FtpsCertificateFirstUseTitle", "FtpsCertificateFirstUseWarning", AcceptIsDefault: true);

    public static CertificatePromptWording WebFirstUse { get; } =
        new("WebCertificateFirstUseTitle", "WebCertificateFirstUseWarning", AcceptIsDefault: true);

    public static CertificatePromptWording WebChanged { get; } =
        new("WebCertificateChangedTitle", "WebCertificateChangedWarning", AcceptIsDefault: false);
}

public partial class FtpsCertificatePromptDialogViewModel(
    LocalizationManager localizer,
    FtpsCertificatePrompt prompt,
    CertificatePromptWording? wording = null) : ObservableObject
{
    private readonly LocalizationManager _localizer = localizer;

    private readonly CertificatePromptWording _wording = wording ?? CertificatePromptWording.Ftps;

    public string Host { get; } = prompt.Host;

    public int Port { get; } = prompt.Port;

    public string PresentedFingerprint { get; } = prompt.PresentedFingerprint;

    public string Subject { get; } = prompt.Subject;

    public string Issuer { get; } = prompt.Issuer;

    public string ValidationErrors { get; } = prompt.ValidationErrors;

    public DateTimeOffset NotBefore { get; } = prompt.NotBefore;

    public DateTimeOffset NotAfter { get; } = prompt.NotAfter;

    /// <remarks>
    /// For FTPS this is a first-use prompt only: a changed certificate is refused by the FTP
    /// browser before any prompt can be asked. A web console asks again with the changed wording,
    /// because appliances regenerate their self-signed certificate on firmware updates.
    /// </remarks>
    public string HeaderText => _localizer[_wording.HeaderKey];

    public string WarningText => _localizer.Format(
        _wording.WarningKey,
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

    public bool AcceptIsDefault => _wording.AcceptIsDefault;

    /// <summary>
    /// On a certificate that replaces a trusted one Reject answers Enter and takes the focus,
    /// as the host-key prompt does on a changed key. Before, nothing was the default there but
    /// Accept held the focus, so Enter still trusted what may be an interception.
    /// </summary>
    public bool RejectIsDefault => !_wording.AcceptIsDefault;

    /// <summary>Whether the prompt is drawn as an alert rather than a first-use question.</summary>
    public bool IsWarning => !_wording.AcceptIsDefault;

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
