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
public partial class ServerItemViewModel : ObservableObject, IInlineRenameNode
{
    [ObservableProperty]
    private string _id = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editName = "";

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

    partial void OnHealthStateChanged(HealthState value)
    {
        OnPropertyChanged(nameof(HealthTooltipText));
        OnPropertyChanged(nameof(AccessibleName));
    }

    /// <summary>
    /// Retained DTO reference for accessing protocol-specific properties
    /// (e.g. SshPort, FtpPort, SshKeyPath) that are not exposed as ViewModel fields.
    /// </summary>
    private ServerProfileDto? _sourceDto;
    private IReadOnlyDictionary<string, SshGatewayDto>? _gatewayMap;

    private string _normalizedSearchText = "";
    private bool _searchTextCacheInvalid = true;

    /// <summary>
    /// Cached normalized projection of every searchable server field. Property
    /// callbacks invalidate it, and the next filter pass recomputes it once.
    /// </summary>
    public string NormalizedSearchText
    {
        get
        {
            if (_searchTextCacheInvalid)
            {
                _normalizedSearchText = NormalizeSearchTerm(string.Join(
                    "\u001F",
                    DisplayName,
                    RemoteServer,
                    Group,
                    Username,
                    ConnectionType,
                    Environment,
                    Tags,
                    ProjectName));
                _searchTextCacheInvalid = false;
            }

            return _normalizedSearchText;
        }
    }

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
            { } state when string.Equals(state, "Connected", StringComparison.OrdinalIgnoreCase)
                => T("SessionStatusConnected"),
            { } state when string.Equals(state, "LaunchedExternalClient", StringComparison.OrdinalIgnoreCase)
                => T("StatusLaunchedExternalClient"),
            { } state when string.Equals(state, "RemoteSessionHandedOff", StringComparison.OrdinalIgnoreCase)
                => T("StatusRemoteSessionHandedOff"),
            _ => ConnectionState
        };

    public string ConnectionStateTooltip =>
        ConnectionState switch
        {
            { } state when string.Equals(state, "LaunchedExternalClient", StringComparison.OrdinalIgnoreCase)
                => T("StatusLaunchedExternalClientTooltip"),
            { } state when string.Equals(state, "RemoteSessionHandedOff", StringComparison.OrdinalIgnoreCase)
                => T("StatusRemoteSessionHandedOffTooltip"),
            _ => ConnectionStateDisplayName
        };

    public string SidebarDisplayName => SidebarDisplayNameFormatter.Format(DisplayName) ?? "";

    public string AccessibleName => Format(
        "SessionTreeServerAccessibleName",
        DisplayName,
        ConnectionType.ToUpperInvariant(),
        IsActiveSession ? ConnectionStateDisplayName : HealthTooltipText);

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
            _gatewayMap = gatewayMap,
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
        };
        viewModel.RefreshLocalizedState();
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
        _gatewayMap = gatewayMap;
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
        RefreshLocalizedState();
    }

    partial void OnConnectionTypeChanged(string value)
    {
        OnPropertyChanged(nameof(ConnectionTypeBadge));
        OnPropertyChanged(nameof(AccessibleName));
        InvalidateSearchTextCache();
    }

    partial void OnDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(SidebarDisplayName));
        OnPropertyChanged(nameof(AccessibleName));
        InvalidateSearchTextCache();
    }

    /// <inheritdoc />
    public void BeginInlineEdit()
    {
        EditName = DisplayName;
        IsEditing = true;
    }

    /// <inheritdoc />
    public void CancelInlineEdit()
    {
        EditName = DisplayName;
        IsEditing = false;
    }

    /// <inheritdoc />
    public void CompleteInlineEdit()
    {
        EditName = DisplayName;
        IsEditing = false;
    }

    partial void OnRemoteServerChanged(string value)
    {
        Endpoint = string.IsNullOrEmpty(value) ? "" : (RemotePort > 0 ? $"{value}:{RemotePort}" : value);
        InvalidateSearchTextCache();
    }

    partial void OnGroupChanged(string value) => InvalidateSearchTextCache();

    partial void OnEnvironmentChanged(string value) => InvalidateSearchTextCache();

    partial void OnProjectNameChanged(string value) => InvalidateSearchTextCache();

    partial void OnUsernameChanged(string value) => InvalidateSearchTextCache();

    partial void OnTagsChanged(string value) => InvalidateSearchTextCache();

    partial void OnConnectionStateChanged(string value)
    {
        OnPropertyChanged(nameof(IsActiveSession));
        OnPropertyChanged(nameof(ConnectionStateDisplayName));
        OnPropertyChanged(nameof(ConnectionStateTooltip));
        OnPropertyChanged(nameof(AccessibleName));
    }

    internal void RefreshLocalizedState()
    {
        if (_sourceDto is not null)
        {
            AuthSummary = BuildAuthSummary(_sourceDto);
            ApplyGatewayState(_sourceDto.SshGatewayId, _gatewayMap);
        }

        OnPropertyChanged(nameof(OriginDisplayName));
        OnPropertyChanged(nameof(HealthTooltipText));
        OnPropertyChanged(nameof(ConnectionStateDisplayName));
        OnPropertyChanged(nameof(ConnectionStateTooltip));
        OnPropertyChanged(nameof(AccessibleName));
    }

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

    internal static string NormalizeSearchTerm(string? value) =>
        value?.Trim().ToUpperInvariant() ?? "";

    private void InvalidateSearchTextCache() => _searchTextCacheInvalid = true;

    private static string GetUsername(ServerProfileDto dto)
    {
        return dto.ConnectionType?.ToUpperInvariant() switch
        {
            "SSH" or "SFTP" => dto.SshUsername ?? "",
            "RDP" => dto.RdpUsername ?? "",
            "WINRM" => dto.WinRmUsername ?? "",
            "FTP" => dto.FtpUsername ?? "",
            "TELNET" => dto.TelnetUsername ?? "",
            _ => ""
        };
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
        "SessionAuthSshKey" => "SSH key",
        "SessionAuthPassword" => "Password",
        "SessionAuthAgent" => "Agent",
        "SessionAuthPrompt" => "Prompt",
        "SessionAuthCurrentUser" => "Current user",
        "SessionTreeServerAccessibleName" => "{0}, protocol {1}, state {2}",
        "SessionStatusConnected" => "Connected",
        "StatusLaunchedExternalClient" => "External client launched",
        "StatusLaunchedExternalClientTooltip" => "The external client was launched.",
        "StatusRemoteSessionHandedOff" => "Session started",
        "StatusRemoteSessionHandedOffTooltip" => "The remote session was handed off to its client.",
        _ => key
    };

    private string BuildAuthSummary(ServerProfileDto dto)
    {
        var type = dto.ConnectionType?.ToUpperInvariant();

        switch (type)
        {
            case "SSH" or "SFTP":
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(dto.SshKeyPath))
                    {
                        parts.Add(T("SessionAuthSshKey"));
                    }

                    if (!string.IsNullOrWhiteSpace(dto.SshPasswordEncrypted))
                    {
                        parts.Add(T("SessionAuthPassword"));
                    }

                    if (dto.SshAgentForwarding)
                    {
                        parts.Add(T("SessionAuthAgent"));
                    }

                    return parts.Count > 0
                        ? string.Join(" + ", parts)
                        : T("SessionAuthPassword");
                }

            case "RDP":
                {
                    return !string.IsNullOrWhiteSpace(dto.RdpPasswordEncrypted)
                        ? T("SessionAuthPassword")
                        : T("SessionAuthPrompt");
                }

            case "WINRM":
                {
                    return dto.WinRmIdentityMode == Core.Configuration.WinRmIdentityMode.CurrentUser
                        ? T("SessionAuthCurrentUser")
                        : !string.IsNullOrWhiteSpace(dto.WinRmPasswordEncrypted)
                            ? T("SessionAuthPassword")
                            : T("SessionAuthPrompt");
                }

            case "FTP":
                {
                    return !string.IsNullOrWhiteSpace(dto.FtpPasswordEncrypted)
                        ? T("SessionAuthPassword")
                        : T("SessionAuthPrompt");
                }

            case "TELNET":
                {
                    return !string.IsNullOrWhiteSpace(dto.TelnetPasswordEncrypted)
                        ? T("SessionAuthPassword")
                        : T("SessionAuthPrompt");
                }

            case "VNC":
                {
                    return !string.IsNullOrWhiteSpace(dto.VncPassword)
                        ? T("SessionAuthPassword")
                        : T("SessionAuthPrompt");
                }

            default:
                return "";
        }
    }
}
