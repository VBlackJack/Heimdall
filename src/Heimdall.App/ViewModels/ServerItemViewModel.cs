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
public partial class ServerItemViewModel : ObservableObject, IInlineRenameNode, IAccessibleItemViewModel
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

    /// <summary>
    /// Whether the sidebar status dot is currently showing the session state rather than the health
    /// verdict.
    /// </summary>
    /// <remarks>
    /// The one decision, shared. The dot's colour, the tooltip on it and the spoken row name all
    /// follow it, so none of the three can end up describing something the other two are not. What
    /// they render differs on purpose - a tooltip has room for the longer explanation and a spoken
    /// name does not - but the branch they take is the same one.
    /// </remarks>
    public bool StatusShowsConnectionState =>
        ConnectionStateSets.StateOverridesHealth(ConnectionState);

    /// <summary>
    /// Tooltip for the sidebar status dot, describing whatever the dot is coloured for.
    /// </summary>
    /// <remarks>
    /// It used to describe reachability unconditionally, while the dot beside it was already
    /// coloured from the session state. A connected or failed session therefore showed a dot saying
    /// one thing and a tooltip saying another - and the spoken name, which had already been
    /// corrected to follow the dot, agreed with neither.
    /// </remarks>
    public string StatusTooltipText =>
        StatusShowsConnectionState ? ConnectionStateTooltip : HealthTooltipText;

    partial void OnHealthStateChanged(HealthState value)
    {
        OnPropertyChanged(nameof(HealthTooltipText));
        OnPropertyChanged(nameof(StatusTooltipText));
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

    /// <summary>
    /// Spoken description of the row. Its status half follows the SAME priority as the sidebar
    /// dot - connection state first, health only where the dot itself falls back to health.
    /// </summary>
    /// <remarks>
    /// It used to key off <see cref="IsActiveSession"/>, which is a strictly narrower set: an
    /// <c>Error</c> or any transitional state left the dot coloured from the state while this
    /// name read out the reachability verdict, so the two contradicted each other precisely where
    /// a user most needs the status. <c>ServerStatusToColorConverter</c> is deliberately left
    /// untouched; its own priority is the correct behaviour, and a coherence test pins the two
    /// together so neither can drift alone.
    /// </remarks>
    public string AccessibleName => Format(
        "SessionTreeServerAccessibleName",
        DisplayName,
        ConnectionType.ToUpperInvariant(),
        StatusShowsConnectionState
            ? ConnectionStateDisplayName
            : HealthTooltipText);

    /// <summary>
    /// No keyboard guidance to announce for a server row. Folders carry one because they
    /// expand and collapse; a server row does nothing a screen reader needs told in advance.
    /// </summary>
    public string? AccessibleHelpText => null;

    /// <summary>
    /// Hover text for the row, or <see langword="null"/> when the row already shows everything
    /// there is to say.
    /// </summary>
    /// <remarks>
    /// It used to be bound straight to <see cref="DisplayName"/>, which is the one thing the row
    /// is already printing, so hovering answered a question nobody had. What the row does not
    /// print is where the session actually goes: the host and port, who it signs in as, and which
    /// protocol the coloured icon stands for.
    ///
    /// <para>
    /// The health verdict is deliberately left out. The status dot carries its own tooltip and is
    /// the only place that verdict is spelled out; repeating it here would make the dot's tooltip
    /// redundant, and the row is the larger hover target, so the version people would actually see
    /// is the one attached to the wrong control. The verdict already reaches assistive technology
    /// through <see cref="AccessibleName"/>.
    /// </para>
    /// </remarks>
    public string? RowTooltipText
    {
        get
        {
            List<string> lines = [];

            if (!string.IsNullOrWhiteSpace(Endpoint))
            {
                lines.Add(Format("SessionTreeRowTooltipHost", Endpoint));
            }

            if (!string.IsNullOrWhiteSpace(Username))
            {
                lines.Add(Format("SessionTreeRowTooltipUser", Username));
            }

            // A tool row has no protocol to name - its "connection type" is the tool id.
            if (!ConnectionTypeCatalog.IsToolConnectionType(ConnectionType))
            {
                lines.Add(Format(
                    "SessionTreeRowTooltipProtocol",
                    ConnectionType.ToUpperInvariant()));
            }

            return lines.Count == 0
                ? null
                : string.Join(System.Environment.NewLine, lines);
        }
    }

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
        _ when ConnectionTypeCatalog.IsToolConnectionType(ConnectionType) => "TOOL",
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
        OnPropertyChanged(nameof(RowTooltipText));
        InvalidateSearchTextCache();
    }

    partial void OnEndpointChanged(string value) => OnPropertyChanged(nameof(RowTooltipText));

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

    partial void OnUsernameChanged(string value)
    {
        OnPropertyChanged(nameof(RowTooltipText));
        InvalidateSearchTextCache();
    }

    partial void OnTagsChanged(string value) => InvalidateSearchTextCache();

    partial void OnConnectionStateChanged(string value)
    {
        OnPropertyChanged(nameof(IsActiveSession));
        OnPropertyChanged(nameof(ConnectionStateDisplayName));
        OnPropertyChanged(nameof(ConnectionStateTooltip));
        OnPropertyChanged(nameof(StatusTooltipText));
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
        OnPropertyChanged(nameof(StatusTooltipText));
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(RowTooltipText));
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
        "SessionAuthUsername" => "Username",
        "SessionAuthSshKey" => "SSH key",
        "SessionAuthPassword" => "Password",
        "SessionAuthNoneSaved" => "No saved credentials",
        "SessionAuthCurrentUser" => "Current user",
        "SessionTreeServerAccessibleName" => "{0}, protocol {1}, state {2}",
        "SessionTreeRowTooltipHost" => "Host: {0}",
        "SessionTreeRowTooltipUser" => "User: {0}",
        "SessionTreeRowTooltipProtocol" => "Protocol: {0}",
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
        var parts = new List<string>();

        switch (type)
        {
            case "SSH" or "SFTP":
                AddIfConfigured(parts, dto.SshUsername, "SessionAuthUsername");
                AddIfConfigured(parts, dto.SshKeyPath, "SessionAuthSshKey");
                AddIfConfigured(parts, dto.SshPasswordEncrypted, "SessionAuthPassword");
                break;

            case "RDP":
                AddIfConfigured(parts, dto.RdpUsername, "SessionAuthUsername");
                AddIfConfigured(parts, dto.RdpPasswordEncrypted, "SessionAuthPassword");
                break;

            case "WINRM":
                if (dto.WinRmIdentityMode == Core.Configuration.WinRmIdentityMode.CurrentUser)
                {
                    return T("SessionAuthCurrentUser");
                }

                AddIfConfigured(parts, dto.WinRmUsername, "SessionAuthUsername");
                AddIfConfigured(parts, dto.WinRmPasswordEncrypted, "SessionAuthPassword");
                break;

            case "FTP":
                AddIfConfigured(parts, dto.FtpUsername, "SessionAuthUsername");
                AddIfConfigured(parts, dto.FtpPasswordEncrypted, "SessionAuthPassword");
                break;

            case "VNC":
                AddIfConfigured(parts, dto.VncPassword, "SessionAuthPassword");
                break;

            default:
                return "";
        }

        return parts.Count > 0
            ? string.Join(" + ", parts)
            : T("SessionAuthNoneSaved");
    }

    private void AddIfConfigured(List<string> parts, string? value, string localizationKey)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(T(localizationKey));
        }
    }
}
