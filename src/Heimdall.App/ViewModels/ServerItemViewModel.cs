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
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.SessionHealth;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.ViewModels;

/// <summary>
/// ViewModel representing a single server item in the server list.
/// Maps from <see cref="ServerProfileDto"/> for UI binding.
/// </summary>
public partial class ServerItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string _remoteServer = "";

    [ObservableProperty]
    private int _remotePort;

    [ObservableProperty]
    private string _group = "";

    [ObservableProperty]
    private string _connectionType = "RDP";

    [ObservableProperty]
    private string _connectionState = "Disconnected";

    [ObservableProperty]
    private string _environment = "";

    [ObservableProperty]
    private string _projectId = "";

    [ObservableProperty]
    private string _projectName = "";

    [ObservableProperty]
    private string _projectColor = "";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private int _sortOrder;

    [ObservableProperty]
    private string _macAddress = "";

    [ObservableProperty]
    private string _tags = "";

    [ObservableProperty]
    private string _endpoint = "";

    [ObservableProperty]
    private string _gatewayName = "";

    [ObservableProperty]
    private bool _isGatewayBadgeVisible;

    [ObservableProperty]
    private bool _isGatewayMissing;

    [ObservableProperty]
    private string _gatewayBadgeText = "";

    [ObservableProperty]
    private string _gatewayBadgeTooltip = "";

    [ObservableProperty]
    private string _gatewayDetailText = "";

    [ObservableProperty]
    private string _authSummary = "";

    /// <summary>
    /// Last known reachability state, fed externally by
    /// <see cref="SessionHealthMonitor"/>. The sidebar dot reads from this
    /// when the server is in a non-active connection state.
    /// </summary>
    [ObservableProperty]
    private HealthState _healthState = HealthState.Initial;

    /// <summary>Tooltip text for the sidebar health dot, recomputed on every <see cref="HealthState"/> change.</summary>
    public string HealthTooltipText => HealthReasonLocalizer.FormatTooltip(HealthState, _localizer);

    partial void OnHealthStateChanged(HealthState value) => OnPropertyChanged(nameof(HealthTooltipText));

    /// <summary>
    /// Retained DTO reference for accessing protocol-specific properties
    /// (e.g. SshPort, FtpPort, SshKeyPath) that are not exposed as ViewModel fields.
    /// </summary>
    private ServerProfileDto? _sourceDto;

    /// <summary>
    /// Returns the protocol-appropriate port for this server (SSH→SshPort, FTP→FtpPort, etc.)
    /// instead of the generic <see cref="RemotePort"/> which defaults to the RDP port.
    /// </summary>
    public int EffectivePort => _sourceDto is null ? RemotePort
        : ConnectionType?.ToUpperInvariant() switch
        {
            "SSH" or "SFTP" => _sourceDto.SshPort,
            "WINRM" => _sourceDto.WinRmPort,
            "FTP" => _sourceDto.FtpPort,
            "VNC" => _sourceDto.VncPort,
            "TELNET" => _sourceDto.TelnetPort,
            _ => RemotePort
        };

    /// <summary>
    /// Path to the SSH private key file, if configured.
    /// </summary>
    public string SshKeyPath => _sourceDto?.SshKeyPath ?? "";

    public bool IsActiveSession => ConnectionStateSets.IsConnected(ConnectionState);

    public string ConnectionStateDisplayName =>
        ConnectionState switch
        {
            { } state when string.Equals(state, "LaunchedExternalClient", StringComparison.OrdinalIgnoreCase)
                => L("StatusLaunchedExternalClient"),
            { } state when string.Equals(state, "RemoteSessionHandedOff", StringComparison.OrdinalIgnoreCase)
                => L("StatusRemoteSessionHandedOff"),
            _ => ConnectionState
        };

    public string ConnectionStateTooltip =>
        ConnectionState switch
        {
            { } state when string.Equals(state, "LaunchedExternalClient", StringComparison.OrdinalIgnoreCase)
                => L("StatusLaunchedExternalClientTooltip"),
            { } state when string.Equals(state, "RemoteSessionHandedOff", StringComparison.OrdinalIgnoreCase)
                => L("StatusRemoteSessionHandedOffTooltip"),
            _ => ConnectionStateDisplayName
        };

    public string SidebarDisplayName => SidebarDisplayNameFormatter.Format(DisplayName) ?? "";

    public string ConnectionTypeBadge => ConnectionType.ToUpperInvariant() switch
    {
        "RDP" => "RDP",
        "SSH" => "SSH",
        "WINRM" => "WINRM",
        "SFTP" => "SFTP",
        "FTP" => "FTP",
        "VNC" => "VNC",
        "TELNET" => "TEL",
        "CITRIX" => "CTX",
        "LOCAL" => "SH",
        _ when ConnectionType.StartsWith("TOOL:", StringComparison.OrdinalIgnoreCase) => "TOOL",
        _ => ConnectionType.ToUpperInvariant()
    };

    /// <summary>
    /// Creates a <see cref="ServerItemViewModel"/> from a <see cref="ServerProfileDto"/>.
    /// </summary>
    public static ServerItemViewModel FromDto(
        ServerProfileDto dto,
        ProjectDto? project = null,
        string connectionState = "Disconnected",
        IReadOnlyDictionary<string, SshGatewayDto>? gatewayMap = null,
        LocalizationManager? localizer = null)
    {
        var viewModel = new ServerItemViewModel
        {
            _sourceDto = dto,
            _localizer = localizer,
            Id = dto.Id,
            DisplayName = dto.DisplayName,
            Origin = dto.Origin,
            RemoteServer = dto.RemoteServer,
            RemotePort = dto.RemotePort,
            Group = dto.Group ?? "",
            ConnectionType = dto.ConnectionType,
            ConnectionState = connectionState,
            Environment = dto.Environment ?? "",
            ProjectId = dto.ProjectId ?? "",
            ProjectName = project?.Name ?? "",
            ProjectColor = project?.Color ?? "",
            Username = GetUsername(dto),
            Tags = dto.Tags ?? "",
            Endpoint = FormatEndpoint(dto),
            IsFavorite = dto.IsFavorite,
            SortOrder = dto.SortOrder,
            MacAddress = dto.MacAddress ?? "",
            AuthSummary = BuildAuthSummary(dto),
        };
        viewModel.ApplyGatewayState(dto.SshGatewayId, gatewayMap);
        return viewModel;
    }

    /// <summary>
    /// Applies updated values from a <see cref="ServerProfileDto"/> to this ViewModel.
    /// </summary>
    public void UpdateFromDto(
        ServerProfileDto dto,
        ProjectDto? project = null,
        IReadOnlyDictionary<string, SshGatewayDto>? gatewayMap = null,
        LocalizationManager? localizer = null)
    {
        _sourceDto = dto;
        _localizer = localizer ?? _localizer;
        DisplayName = dto.DisplayName;
        Origin = dto.Origin;
        RemoteServer = dto.RemoteServer;
        RemotePort = dto.RemotePort;
        Group = dto.Group ?? "";
        ConnectionType = dto.ConnectionType;
        Environment = dto.Environment ?? "";
        ProjectId = dto.ProjectId ?? "";
        ProjectName = project?.Name ?? "";
        ProjectColor = project?.Color ?? "";
        Username = GetUsername(dto);
        Tags = dto.Tags ?? "";
        Endpoint = FormatEndpoint(dto);
        IsFavorite = dto.IsFavorite;
        SortOrder = dto.SortOrder;
        MacAddress = dto.MacAddress ?? "";
        ApplyGatewayState(dto.SshGatewayId, gatewayMap);
        AuthSummary = BuildAuthSummary(dto);
        OnPropertyChanged(nameof(OriginDisplayName));
    }

    partial void OnConnectionTypeChanged(string value)
    {
        OnPropertyChanged(nameof(ConnectionTypeBadge));
    }

    partial void OnDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(SidebarDisplayName));
    }

    partial void OnRemoteServerChanged(string value)
        => Endpoint = string.IsNullOrEmpty(value) ? "" : (RemotePort > 0 ? $"{value}:{RemotePort}" : value);

    partial void OnConnectionStateChanged(string value)
    {
        OnPropertyChanged(nameof(IsActiveSession));
        OnPropertyChanged(nameof(ConnectionStateDisplayName));
        OnPropertyChanged(nameof(ConnectionStateTooltip));
    }

    private string L(string key) => _localizer?[key] ?? key;

    private static string FormatEndpoint(ServerProfileDto dto)
    {
        var type = dto.ConnectionType?.ToUpperInvariant();
        if (type is "LOCAL" or "CITRIX")
        {
            return "";
        }

        var host = dto.RemoteServer;
        if (string.IsNullOrEmpty(host)) return "";

        var port = type switch
        {
            "SSH" or "SFTP" => dto.SshPort,
            "WINRM" => dto.WinRmPort,
            "FTP" => dto.FtpPort,
            "VNC" => dto.VncPort,
            "TELNET" => dto.TelnetPort,
            _ => dto.RemotePort
        };

        return port > 0 ? $"{host}:{port}" : host;
    }

    private static string GetUsername(ServerProfileDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.SshUsername)) return dto.SshUsername;
        if (!string.IsNullOrWhiteSpace(dto.RdpUsername)) return dto.RdpUsername;
        if (!string.IsNullOrWhiteSpace(dto.FtpUsername)) return dto.FtpUsername;
        if (!string.IsNullOrWhiteSpace(dto.TelnetUsername)) return dto.TelnetUsername;
        return "";
    }

    private void ApplyGatewayState(
        string? gatewayId,
        IReadOnlyDictionary<string, SshGatewayDto>? gatewayMap)
    {
        if (!ProtocolCapabilities.SupportsSshGateway(ConnectionType)
            || string.IsNullOrWhiteSpace(gatewayId)
            || gatewayMap is null)
        {
            GatewayName = "";
            IsGatewayBadgeVisible = false;
            IsGatewayMissing = false;
            GatewayBadgeText = "";
            GatewayBadgeTooltip = "";
            GatewayDetailText = "";
            return;
        }

        IsGatewayBadgeVisible = true;
        if (gatewayMap.TryGetValue(gatewayId, out var gateway))
        {
            var gatewayName = gateway.Name ?? "";
            GatewayName = gatewayName;
            IsGatewayMissing = false;
            GatewayBadgeText = Format("SessionGatewayBadgeVia", gatewayName);
            GatewayBadgeTooltip = Format("SessionGatewayBadgeTooltipVia", gatewayName);
            GatewayDetailText = gatewayName;
            return;
        }

        GatewayName = "";
        IsGatewayMissing = true;
        GatewayBadgeText = T("SessionGatewayBadgeMissing");
        GatewayBadgeTooltip = Format("SessionGatewayBadgeTooltipMissing", gatewayId);
        GatewayDetailText = Format("SessionGatewayMissingDetail", gatewayId);
    }

    private string T(string key) => _localizer?.HasKey(key) == true ? _localizer[key] : Fallback(key);

    private string Format(string key, params object[] args)
    {
        if (_localizer?.HasKey(key) == true)
        {
            return _localizer.Format(key, args);
        }

        var template = Fallback(key);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, args);
    }

    private static string Fallback(string key) => key switch
    {
        "SessionGatewayBadgeVia" => "via {0}",
        "SessionGatewayBadgeMissing" => "gateway missing",
        "SessionGatewayBadgeTooltipVia" => "Routes through SSH gateway {0}.",
        "SessionGatewayBadgeTooltipMissing" => "This session references missing SSH gateway id {0}.",
        "SessionGatewayMissingDetail" => "Missing gateway ({0})",
        _ => key
    };

    private static string BuildAuthSummary(ServerProfileDto dto)
    {
        var type = dto.ConnectionType?.ToUpperInvariant();

        switch (type)
        {
            case "SSH" or "SFTP":
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(dto.SshKeyPath))
                    {
                        parts.Add("SSH Key");
                    }

                    if (!string.IsNullOrWhiteSpace(dto.SshPasswordEncrypted))
                    {
                        parts.Add("Password");
                    }

                    if (dto.SshAgentForwarding)
                    {
                        parts.Add("Agent");
                    }

                    return parts.Count > 0 ? string.Join(" + ", parts) : "Password";
                }

            case "RDP":
                {
                    return !string.IsNullOrWhiteSpace(dto.RdpPasswordEncrypted)
                        ? "Password"
                        : "Prompt";
                }

            case "WINRM":
                {
                    return dto.WinRmIdentityMode == Core.Configuration.WinRmIdentityMode.CurrentUser
                        ? "Current user"
                        : !string.IsNullOrWhiteSpace(dto.WinRmPasswordEncrypted)
                            ? "Password"
                            : "Prompt";
                }

            case "FTP":
                {
                    return !string.IsNullOrWhiteSpace(dto.FtpPasswordEncrypted)
                        ? "Password"
                        : "Prompt";
                }

            case "TELNET":
                {
                    return !string.IsNullOrWhiteSpace(dto.TelnetPasswordEncrypted)
                        ? "Password"
                        : "Prompt";
                }

            case "VNC":
                {
                    return !string.IsNullOrWhiteSpace(dto.VncPassword)
                        ? "Password"
                        : "Prompt";
                }

            default:
                return "";
        }
    }
}
