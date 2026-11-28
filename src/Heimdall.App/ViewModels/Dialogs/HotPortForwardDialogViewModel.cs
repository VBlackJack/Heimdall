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
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Ssh;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// View-model for adding a hot port forward (<c>-L</c>, <c>-D</c>, or <c>-R</c>) to a live SSH session.
/// </summary>
public sealed partial class HotPortForwardDialogViewModel : ObservableObject
{
    public sealed record ModeOption(SshSessionForwardKind Kind, string DisplayName);

    private readonly LocalizationManager _localizer;

    public HotPortForwardDialogViewModel(LocalizationManager localizer)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        ForwardModes =
        [
            new ModeOption(SshSessionForwardKind.Local, _localizer["NewTunnelModeLocal"]),
            new ModeOption(SshSessionForwardKind.DynamicSocks, _localizer["NewTunnelModeDynamicSocks5"]),
            new ModeOption(SshSessionForwardKind.Remote, _localizer["NewTunnelModeRemote"])
        ];
        _selectedModeOption = ForwardModes[0];
    }

    public IReadOnlyList<ModeOption> ForwardModes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemoteHostField))]
    [NotifyPropertyChangedFor(nameof(ShowRemotePortField))]
    [NotifyPropertyChangedFor(nameof(LocalPortLabelText))]
    [NotifyPropertyChangedFor(nameof(RemotePortLabelText))]
    private ModeOption _selectedModeOption;

    [ObservableProperty]
    private string _remoteHost = "127.0.0.1";

    [ObservableProperty]
    private string _remotePortText = string.Empty;

    [ObservableProperty]
    private string _localPortText = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string _validationError = string.Empty;

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationError);

    public bool ShowRemoteHostField => SelectedModeOption.Kind == SshSessionForwardKind.Local;

    public bool ShowRemotePortField => SelectedModeOption.Kind is SshSessionForwardKind.Local or SshSessionForwardKind.Remote;

    public string LocalPortLabelText => SelectedModeOption.Kind switch
    {
        SshSessionForwardKind.DynamicSocks => _localizer["NewTunnelSocksPortLabel"],
        SshSessionForwardKind.Remote => _localizer["NewTunnelRemoteLocalPortLabel"],
        _ => _localizer["NewTunnelLocalPortLabel"]
    };

    public string RemotePortLabelText => SelectedModeOption.Kind == SshSessionForwardKind.Remote
        ? _localizer["NewTunnelRemoteBindPortLabel"]
        : _localizer["NewTunnelRemotePortLabel"];

    public SshSessionForwardRequest? Result { get; private set; }

    public event Action<bool>? CloseRequested;

    [RelayCommand]
    private void Confirm()
    {
        ValidationError = string.Empty;
        var kind = SelectedModeOption.Kind;

        if (!TryParsePort(LocalPortText, out int localPort))
        {
            ValidationError = _localizer["HotForwardErrInvalidPort"];
            return;
        }

        if (kind == SshSessionForwardKind.DynamicSocks)
        {
            Result = new SshSessionForwardRequest(
                SshSessionForwardKind.DynamicSocks,
                LocalPort: localPort,
                Label: string.IsNullOrWhiteSpace(Label) ? null : Label.Trim());
            CloseRequested?.Invoke(true);
            return;
        }

        if (!TryParsePort(RemotePortText, out int remotePort))
        {
            ValidationError = _localizer["HotForwardErrInvalidPort"];
            return;
        }

        if (kind == SshSessionForwardKind.Local)
        {
            var host = RemoteHost?.Trim() ?? string.Empty;
            if (!InputValidator.IsValidSshHost(host))
            {
                ValidationError = _localizer["HotForwardErrInvalidHost"];
                return;
            }

            Result = new SshSessionForwardRequest(
                SshSessionForwardKind.Local,
                LocalPort: localPort,
                RemoteHost: host,
                RemotePort: remotePort,
                Label: string.IsNullOrWhiteSpace(Label) ? null : Label.Trim());
            CloseRequested?.Invoke(true);
            return;
        }

        Result = new SshSessionForwardRequest(
            SshSessionForwardKind.Remote,
            LocalPort: localPort,
            RemotePort: remotePort,
            Label: string.IsNullOrWhiteSpace(Label) ? null : Label.Trim());
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(false);
    }

    private static bool TryParsePort(string? raw, out int port)
    {
        port = 0;
        return int.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && InputValidator.ValidatePortRange(port);
    }
}
