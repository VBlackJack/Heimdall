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

using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// WinRM-specific hints and guards of the session editor: what the dialog tells the user about
/// transport, certificate checks, TrustedHosts and the stored credential.
/// </summary>
public partial class ServerDialogViewModel
{
    private const char HostLabelSeparator = '.';

    private bool _winRmSslSuppressedByGateway;
    private bool _winRmSkipCertificateSuppressedByGateway;

    /// <summary>Inline error on the WinRM password box, raised by Save only.</summary>
    [ObservableProperty]
    private string? _winRmPasswordError;

    /// <summary>
    /// True while HTTPS is switched off only because an SSH gateway is selected. The dialog says
    /// so, and restores the choice if the gateway is removed before the profile is saved.
    /// </summary>
    public bool WinRmSslDisabledByGateway => _winRmSslSuppressedByGateway;

    /// <summary>
    /// True when the certificate check is skipped on this profile: the one setting that removes
    /// the server identity check, so its hint is styled as a warning.
    /// </summary>
    public bool IsWinRmCertificateCheckSkipped => IsWinRmConnection && WinRmUseSsl && WinRmSkipCertificateCheck;

    /// <summary>
    /// Hint of the "skip certificate validation" box. A greyed box with no reason reads as a
    /// defect, so while it cannot be used the hint says what enables it.
    /// </summary>
    public string WinRmSkipCertificateHintText => CanSkipWinRmCertificate
        ? L("ServerDialogWinRmSkipCertCheckHint")
        : L("ServerDialogWinRmSkipCertCheckHttpsOnly");

    /// <summary>
    /// Reminder that an off-domain NTLM sign-in over HTTP needs the host in TrustedHosts, or null
    /// when it does not apply. Heimdall never edits that system setting.
    /// </summary>
    public string? WinRmTrustedHostsHint => ShouldShowWinRmTrustedHostsHint(
        RemoteServer,
        WinRmUseSsl,
        IsWinRmCredentialIdentity,
        IsWinRmConnection && !UsesGateway)
        ? L("ServerDialogWinRmTrustedHostsHint")
        : null;

    /// <summary>
    /// Whether the TrustedHosts reminder applies: a plain-HTTP session to a host Kerberos cannot
    /// serve, that is an IP address, or a stored credential aimed at a single-label name.
    /// </summary>
    internal static bool ShouldShowWinRmTrustedHostsHint(
        string? host,
        bool useSsl,
        bool storedCredential,
        bool applicable)
    {
        if (!applicable || useSsl || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string trimmed = host.Trim();
        return IPAddress.TryParse(trimmed, out _)
            || (storedCredential && trimmed.IndexOf(HostLabelSeparator) < 0);
    }

    /// <summary>
    /// The error a stored-credential profile saved without any password would raise at connect
    /// time, judged at save time instead. An empty box keeps an already stored password.
    /// </summary>
    private string? GetWinRmPasswordError()
        => IsWinRmConnection
            && IsWinRmCredentialIdentity
            && string.IsNullOrEmpty(WinRmPassword)
            && !HasStoredWinRmPassword
            ? L("ValidationInlineWinRmPasswordMissing")
            : null;

    partial void OnWinRmPasswordChanged(string value)
    {
        if (WinRmPasswordError is not null && !string.IsNullOrEmpty(value))
        {
            WinRmPasswordError = null;
            RefreshValidationSummary();
        }
    }

    /// <summary>
    /// Switches HTTPS off while an SSH gateway is selected, remembering what was chosen, and puts
    /// it back when the gateway goes away. The previous code turned the box off with no word and
    /// the profile was then saved as plain HTTP.
    /// </summary>
    private void CoerceWinRmSslForGateway()
    {
        if (!IsWinRmConnection)
        {
            return;
        }

        if (UsesGateway)
        {
            if (WinRmUseSsl)
            {
                _winRmSslSuppressedByGateway = true;
                _winRmSkipCertificateSuppressedByGateway = WinRmSkipCertificateCheck;
                WinRmUseSsl = false;
                RaiseWinRmHintsChanged();
            }
        }
        else if (_winRmSslSuppressedByGateway)
        {
            _winRmSslSuppressedByGateway = false;
            bool restoreSkip = _winRmSkipCertificateSuppressedByGateway;
            _winRmSkipCertificateSuppressedByGateway = false;
            WinRmUseSsl = true;
            WinRmSkipCertificateCheck = restoreSkip;
            RaiseWinRmHintsChanged();
        }
    }

    private void RaiseWinRmHintsChanged()
    {
        OnPropertyChanged(nameof(WinRmSslDisabledByGateway));
        OnPropertyChanged(nameof(WinRmUseSslHelpText));
        OnPropertyChanged(nameof(IsWinRmCertificateCheckSkipped));
        OnPropertyChanged(nameof(WinRmSkipCertificateHintText));
        OnPropertyChanged(nameof(WinRmTrustedHostsHint));
    }
}
