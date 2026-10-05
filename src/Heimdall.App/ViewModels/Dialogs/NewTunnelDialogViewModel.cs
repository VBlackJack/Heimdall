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
/// ViewModel for creating an ad-hoc SSH local-forward tunnel from the
/// Tunnel Manager.
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
        RefreshValidation();
    }

    public IReadOnlyList<SshGatewayDto> Gateways { get; }

    public bool HasGateways { get; }

    public bool HasNoGateways { get; }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

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

    // The port boxes bind to text. Bound to the numbers directly, a value that did not parse
    // ("abc", "22a") never reached the view model: it kept the last good port, raised no
    // validation message, and the tunnel opened to a port the box no longer showed.
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

    // Anything that is not a plain number reads as port 0, which the range check refuses.
    private static int ParsePort(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            ? port
            : 0;

    // A host name or an address, nothing else: "host:22" or a name with a space in it used to
    // pass and only failed once the tunnel was dialled.
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

        if (string.IsNullOrWhiteSpace(RemoteHost))
        {
            return _localizer["NewTunnelValidationRemoteHost"];
        }

        if (!IsValidRemoteHost(RemoteHost))
        {
            return _localizer["NewTunnelValidationRemoteHostInvalid"];
        }

        if (RemotePort is < 1 or > 65535)
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localizer["NewTunnelValidationRemotePort"],
                1,
                65535);
        }

        if (LocalPort is < 1024 or > 65535)
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localizer["NewTunnelValidationLocalPort"],
                1024,
                65535);
        }

        if (_activeLocalPorts.Contains(LocalPort))
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localizer["NewTunnelValidationLocalPortInUse"],
                LocalPort);
        }

        return null;
    }
}
