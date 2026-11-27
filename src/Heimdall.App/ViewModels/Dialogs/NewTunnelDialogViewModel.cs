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
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// Port forwarding mode for an ad-hoc SSH tunnel.
/// </summary>
public enum TunnelForwardMode
{
    Local,
    DynamicSocks5,
    Remote
}

/// <summary>
/// Display option pairing a <see cref="TunnelForwardMode"/> with its localized label.
/// </summary>
public sealed record TunnelForwardModeOption(TunnelForwardMode Mode, string DisplayName);

/// <summary>
/// ViewModel for creating an ad-hoc SSH tunnel (Local -L, Dynamic SOCKS5 -D, or Remote -R)
/// from the Tunnel Manager.
/// </summary>
public sealed partial class NewTunnelDialogViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private readonly HashSet<int> _activeLocalPorts;

    public NewTunnelDialogViewModel(
        IReadOnlyList<SshGatewayDto> gateways,
        LocalizationManager localizer,
        IReadOnlySet<int>? activeLocalPorts = null)
    {
        ArgumentNullException.ThrowIfNull(gateways);
        ArgumentNullException.ThrowIfNull(localizer);

        _localizer = localizer;
        _activeLocalPorts = activeLocalPorts is null
            ? []
            : new HashSet<int>(activeLocalPorts);

        Gateways = gateways;
        SelectedGateway = gateways.FirstOrDefault();
        HasGateways = gateways.Count > 0;
        HasNoGateways = !HasGateways;

        ForwardModes =
        [
            new TunnelForwardModeOption(TunnelForwardMode.Local, _localizer["NewTunnelModeLocal"]),
            new TunnelForwardModeOption(TunnelForwardMode.DynamicSocks5, _localizer["NewTunnelModeDynamicSocks5"]),
            new TunnelForwardModeOption(TunnelForwardMode.Remote, _localizer["NewTunnelModeRemote"])
        ];
        _selectedModeOption = ForwardModes[0];
        RefreshValidation();
    }

    public IReadOnlyList<SshGatewayDto> Gateways { get; }

    public IReadOnlyList<TunnelForwardModeOption> ForwardModes { get; }

    public bool HasGateways { get; }

    public bool HasNoGateways { get; }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public TunnelForwardMode ForwardMode
    {
        get => SelectedModeOption.Mode;
        set
        {
            var match = ForwardModes.FirstOrDefault(o => o.Mode == value);
            if (match is not null)
            {
                SelectedModeOption = match;
            }
        }
    }

    public bool ShowRemoteHostField => ForwardMode == TunnelForwardMode.Local;

    public bool ShowRemotePortField => ForwardMode is TunnelForwardMode.Local or TunnelForwardMode.Remote;

    public string RemotePortLabelText => ForwardMode == TunnelForwardMode.Remote
        ? _localizer["NewTunnelRemoteBindPortLabel"]
        : _localizer["NewTunnelRemotePortLabel"];

    public string LocalPortLabelText => ForwardMode switch
    {
        TunnelForwardMode.DynamicSocks5 => _localizer["NewTunnelSocksPortLabel"],
        TunnelForwardMode.Remote => _localizer["NewTunnelRemoteLocalPortLabel"],
        _ => _localizer["NewTunnelLocalPortLabel"]
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(ForwardMode))]
    [NotifyPropertyChangedFor(nameof(ShowRemoteHostField))]
    [NotifyPropertyChangedFor(nameof(ShowRemotePortField))]
    [NotifyPropertyChangedFor(nameof(RemotePortLabelText))]
    [NotifyPropertyChangedFor(nameof(LocalPortLabelText))]
    private TunnelForwardModeOption _selectedModeOption;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private SshGatewayDto? _selectedGateway;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _remoteHost = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private int _remotePort = 22;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private int _localPort = 9090;

    [ObservableProperty]
    private string _remotePortText = "22";

    [ObservableProperty]
    private string _localPortText = "9090";

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string? _validationMessage;

    public bool? Decision { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnSelectedModeOptionChanged(TunnelForwardModeOption value) => RefreshValidation();

    partial void OnSelectedGatewayChanged(SshGatewayDto? value) => RefreshValidation();

    partial void OnRemoteHostChanged(string value) => RefreshValidation();

    partial void OnRemotePortChanged(int value)
    {
        if (ParsePort(RemotePortText) != value)
        {
            RemotePortText = value.ToString(CultureInfo.InvariantCulture);
        }

        RefreshValidation();
    }

    partial void OnLocalPortChanged(int value)
    {
        if (ParsePort(LocalPortText) != value)
        {
            LocalPortText = value.ToString(CultureInfo.InvariantCulture);
        }

        RefreshValidation();
    }

    partial void OnRemotePortTextChanged(string value) => RemotePort = ParsePort(value);

    partial void OnLocalPortTextChanged(string value) => LocalPort = ParsePort(value);

    private static int ParsePort(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            ? port
            : 0;

    private static bool IsValidRemoteHost(string host)
    {
        string trimmed = host.Trim();
        return IPAddress.TryParse(trimmed, out _)
            || (InputValidator.TryCanonicalizeDomain(trimmed, out string canonical)
                && string.Equals(canonical, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnValidationMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasValidationMessage));
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!ValidateInputs())
        {
            return;
        }

        Decision = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Decision = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanConfirm()
    {
        return HasGateways
            && SelectedGateway is not null
            && GetValidationMessage() is null;
    }

    private bool ValidateInputs()
    {
        var message = GetValidationMessage();
        ValidationMessage = message;
        return message is null;
    }

    private void RefreshValidation()
    {
        ValidationMessage = HasGateways ? GetValidationMessage() : null;
    }

    private string? GetValidationMessage()
    {
        if (!HasGateways)
        {
            return null;
        }

        if (SelectedGateway is null)
        {
            return _localizer["NewTunnelValidationGateway"];
        }

        if (ForwardMode == TunnelForwardMode.Local)
        {
            if (string.IsNullOrWhiteSpace(RemoteHost))
            {
                return _localizer["NewTunnelValidationRemoteHost"];
            }

            if (!IsValidRemoteHost(RemoteHost))
            {
                return _localizer["NewTunnelValidationRemoteHostInvalid"];
            }
        }

        if (ForwardMode is TunnelForwardMode.Local or TunnelForwardMode.Remote
            && RemotePort is < 1 or > 65535)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                _localizer["NewTunnelValidationRemotePort"],
                1,
                65535);
        }

        int minLocalPort = ForwardMode == TunnelForwardMode.Remote ? 1 : 1024;
        if (LocalPort < minLocalPort || LocalPort > 65535)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                _localizer["NewTunnelValidationLocalPort"],
                minLocalPort,
                65535);
        }

        int trackedPort = ForwardMode == TunnelForwardMode.Remote ? RemotePort : LocalPort;
        if (_activeLocalPorts.Contains(trackedPort))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                _localizer["NewTunnelValidationLocalPortInUse"],
                trackedPort);
        }

        return null;
    }
}
