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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Judges the five basic fields of a session by the rules of the server dialog, without opening it.
/// </summary>
/// <remarks>
/// It holds a <see cref="ServerDialogViewModel"/> built from a copy of a stored profile, puts the
/// typed values on it and runs the dialog's own <c>Validate</c>, then reads back only the errors of
/// the five fields. The rules, their messages and their dependence on the protocol (a web address
/// is judged with its port and path, a login is required by SSH and judged by WinRM) are therefore
/// the dialog's, not a second copy of them. Errors of fields the panel does not edit are ignored:
/// they would block a rename over a setting the user cannot see here.
/// </remarks>
internal sealed class SessionBasicFieldsValidator
{
    private readonly ServerDialogViewModel _dialog;

    /// <summary>Builds the validator from a copy of <paramref name="stored"/>; the profile itself is never touched.</summary>
    /// <param name="stored">The profile whose protocol and other settings frame the rules.</param>
    /// <param name="localizer">Language of the messages.</param>
    /// <param name="settings">Application settings the dialog's rules depend on, when known.</param>
    public SessionBasicFieldsValidator(
        ServerProfileDto stored,
        LocalizationManager localizer,
        AppSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(localizer);

        _dialog = ServerDialogViewModel.FromDto(stored.CloneFaithfully());
        _dialog.Localizer = localizer;
        if (settings is not null)
        {
            _dialog.Settings = settings;
        }

        Capabilities = SessionBasicFieldCapabilities.For(_dialog.ConnectionType);
        Current = Read();
    }

    /// <summary>Which of the fields the profile's protocol has.</summary>
    public SessionBasicFieldCapabilities Capabilities { get; }

    /// <summary>The connection type of the profile, as the dialog reads it (an empty type reads as RDP).</summary>
    public string ConnectionType => _dialog.ConnectionType.ToUpperInvariant();

    /// <summary>The values of the profile the validator was built from.</summary>
    public SessionBasicFieldValues Current { get; }

    /// <summary>The caption of the port field, named for the protocol as the dialog names it.</summary>
    public string PortLabel => _dialog.EndpointPortLabel;

    /// <summary>Validates <paramref name="values"/> and returns the error of each field.</summary>
    public SessionBasicFieldErrors Validate(SessionBasicFieldValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        _dialog.DisplayName = values.DisplayName;
        if (Capabilities.HasHost)
        {
            _dialog.RemoteServer = values.Host;
        }

        if (Capabilities.HasPort)
        {
            _dialog.EndpointPort = values.Port;
        }

        if (Capabilities.HasUsername)
        {
            WriteUsername(values.Username);
        }

        _dialog.Group = values.Folder;
        _dialog.ValidateCommand.Execute(null);

        return new SessionBasicFieldErrors(
            _dialog.DisplayNameError,
            Capabilities.HasHost ? _dialog.RemoteServerError : null,
            Capabilities.HasPort ? _dialog.EndpointPortError : null,
            Capabilities.HasUsername ? UsernameError() : null);
    }

    private SessionBasicFieldValues Read() => new(
        _dialog.DisplayName,
        Capabilities.HasHost ? _dialog.RemoteServer : "",
        Capabilities.HasPort ? _dialog.EndpointPort : 0,
        Capabilities.HasUsername && !string.IsNullOrWhiteSpace(ReadUsername()) ? ReadUsername() : "",
        string.IsNullOrWhiteSpace(_dialog.Group) ? "" : _dialog.Group);

    private string ReadUsername() => NormalizedType() switch
    {
        "RDP" => _dialog.RdpUsername,
        "WINRM" => _dialog.WinRmUsername,
        "FTP" => _dialog.FtpUsername,
        "WEB" => _dialog.WebUsername,
        _ => _dialog.SshUsername,
    };

    private void WriteUsername(string username)
    {
        switch (NormalizedType())
        {
            case "RDP":
                _dialog.RdpUsername = username;
                break;
            case "WINRM":
                _dialog.WinRmUsername = username;
                break;
            case "FTP":
                _dialog.FtpUsername = username;
                break;
            case "WEB":
                _dialog.WebUsername = username;
                break;
            default:
                _dialog.SshUsername = username;
                break;
        }
    }

    private string? UsernameError() => NormalizedType() switch
    {
        "SSH" or "SFTP" => _dialog.SshUsernameError,
        "WINRM" => _dialog.WinRmUsernameError,
        _ => null,
    };

    private string NormalizedType() => _dialog.ConnectionType.ToUpperInvariant();
}
