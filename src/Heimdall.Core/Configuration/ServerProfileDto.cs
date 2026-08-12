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

using System.Text.Json;
using System.Text.Json.Serialization;
using Heimdall.Core.Models;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Flat DTO for server JSON deserialization.
/// Compatible with legacy servers.json format.
/// The ViewModel layer converts these to ObservableObject models.
/// </summary>
public sealed class ServerProfileDto : IJsonOnDeserialized
{
    private int _winRmPort = DefaultPorts.WinRmHttp;
    private int _sshPort = DefaultPorts.Ssh;
    private string? _sshKeyPassphraseEncrypted;
    private RdpResolutionMode _rdpResolutionMode = RdpResolutionMode.FitWindow;
    private int? _rdpFixedWidth;
    private int? _rdpFixedHeight;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } =
        new(StringComparer.Ordinal);

    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// Provenance tag. Profiles serialized before b63 omit this field and therefore
    /// deserialize to <see cref="ProfileOrigin.Manual"/> (value 0).
    /// </summary>
    public ProfileOrigin Origin { get; set; } = ProfileOrigin.Manual;
    public string RemoteServer { get; set; } = string.Empty;
    public int RemotePort { get; set; } = DefaultPorts.Rdp;
    public int LocalPort { get; set; } = DefaultPorts.RdpTunnel;
    public string? Group { get; set; }
    public string? SshGatewayId { get; set; }
    public string? RdpUsername { get; set; }
    public string? RdpPasswordEncrypted { get; set; }
    public string? RdpDomain { get; set; }
    public bool UseDirectConnection { get; set; }
    public string? ProjectId { get; set; }
    public string ConnectionType { get; set; } = "RDP";
    public bool? SessionLoggingOverride { get; set; }

    /// <summary>
    /// Optional name or reference of this profile's entry in the external password
    /// manager, substituted for the <c>{Title}</c> placeholder in the credential
    /// provider command. When null or empty, <see cref="DisplayName"/> is used.
    /// </summary>
    public string? VaultEntryName { get; set; }

    // WinRM settings
    public int WinRmPort
    {
        get => _winRmPort;
        set
        {
            _winRmPort = value;
            HasWinRmPortField = true;
        }
    }

    [JsonIgnore]
    public bool HasWinRmPortField { get; private set; }

    public string? WinRmUsername { get; set; }
    public string? WinRmPasswordEncrypted { get; set; }
    public bool WinRmUseSsl { get; set; }
    public bool WinRmSkipCertificateCheck { get; set; }

    [JsonConverter(typeof(WinRmIdentityModeJsonConverter))]
    public WinRmIdentityMode WinRmIdentityMode { get; set; } = WinRmIdentityMode.CurrentUser;

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (!HasWinRmPortField)
        {
            _winRmPort = WinRmUseSsl
                ? DefaultPorts.WinRmHttps
                : DefaultPorts.WinRmHttp;
        }
    }

    // SSH settings
    public string? SshUsername { get; set; }

    [JsonIgnore]
    public int SshPort
    {
        get => _sshPort;
        set
        {
            _sshPort = value;
            HasSshPortField = true;
        }
    }

    [JsonIgnore]
    public bool HasSshPortField { get; private set; }

    [JsonInclude]
    [JsonPropertyName("sshPort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal int? SerializedSshPort
    {
        get => HasSshPortField ? _sshPort : null;
        set
        {
            if (value.HasValue)
            {
                _sshPort = value.Value;
                HasSshPortField = true;
            }
        }
    }

    internal void ApplyInheritedSshPort(int port)
    {
        _sshPort = port;
    }

    public string SshMode { get; set; } = "Embedded";
    public bool SshAgentForwarding { get; set; }
    public string? SshKeyPath { get; set; }
    public string? SshPasswordEncrypted { get; set; }
    public string? SshKeyPassphraseEncrypted
    {
        get => _sshKeyPassphraseEncrypted;
        set
        {
            _sshKeyPassphraseEncrypted = value;
            HasSshKeyPassphraseEncryptedField = true;
        }
    }

    [JsonIgnore]
    public bool HasSshKeyPassphraseEncryptedField { get; private set; }

    [JsonIgnore]
    public bool UsesLegacySshCredentialMapping =>
        !HasSshKeyPassphraseEncryptedField
        && !string.IsNullOrWhiteSpace(SshKeyPath)
        && !string.IsNullOrWhiteSpace(SshPasswordEncrypted);

    public bool SshCompression { get; set; }
    public bool SshX11Forwarding { get; set; }
    public int SocksProxyPort { get; set; }
    public int RemoteBindPort { get; set; }
    public int RemoteLocalPort { get; set; }
    public List<PostConnectStep> PostConnectSteps { get; set; } = [];
    public string PostConnectCommand { get; set; } = "";
    public int PostConnectDelayMs { get; set; } = 800;

    // RDP display settings
    public bool RdpAntiIdle { get; set; }
    public string RdpAspectRatio { get; set; } = "Stretch";

    /// <summary>
    /// One-way migration shim for legacy JSON only. Scheduled for full removal in a later phase.
    /// </summary>
    [Obsolete("Use RdpFixedWidth. This setter-only shim exists only for legacy JSON migration.")]
    [JsonPropertyName("rdpDefaultResolutionWidth")]
    public int? RdpDefaultResolutionWidth
    {
        set
        {
            if (value.HasValue && !_rdpFixedWidth.HasValue)
            {
                RdpFixedWidth = value.Value;
            }
        }
    }

    /// <summary>
    /// One-way migration shim for legacy JSON only. Scheduled for full removal in a later phase.
    /// </summary>
    [Obsolete("Use RdpFixedHeight. This setter-only shim exists only for legacy JSON migration.")]
    [JsonPropertyName("rdpDefaultResolutionHeight")]
    public int? RdpDefaultResolutionHeight
    {
        set
        {
            if (value.HasValue && !_rdpFixedHeight.HasValue)
            {
                RdpFixedHeight = value.Value;
            }
        }
    }

    [JsonPropertyName("rdpResolutionMode")]
    [JsonConverter(typeof(JsonStringEnumConverter<RdpResolutionMode>))]
    public RdpResolutionMode RdpResolutionMode
    {
        get => _rdpResolutionMode;
        set
        {
            _rdpResolutionMode = value;
            HasRdpResolutionModeField = true;
        }
    }

    [JsonIgnore]
    public bool HasRdpResolutionModeField { get; private set; }

    [JsonPropertyName("rdpFixedResolutionWidth")]
    public int RdpFixedWidth
    {
        get => _rdpFixedWidth.GetValueOrDefault();
        set => _rdpFixedWidth = value;
    }

    [JsonPropertyName("rdpFixedResolutionHeight")]
    public int RdpFixedHeight
    {
        get => _rdpFixedHeight.GetValueOrDefault();
        set => _rdpFixedHeight = value;
    }

    [JsonPropertyName("rdpInitialSmartSizing")]
    public bool RdpInitialSmartSizing { get; set; } = true;

    [JsonPropertyName("rdpResizeEnableDelayMs")]
    public int? RdpResizeEnableDelayMs { get; set; }

    /// <summary>
    /// Per-profile override for the Tunnels panel expanded state.
    /// null = use application default (<see cref="AppSettings.CollapseTunnelsPanelByDefault"/>).
    /// true / false = remembered manual choice for this profile.
    /// </summary>
    public bool? TunnelsPanelExpanded { get; set; }

    public bool IsFavorite { get; set; }
    public int SortOrder { get; set; }
    public string? Tags { get; set; }

    // RDP mode and device redirection
    public string RdpMode { get; set; } = "Embedded";
    public bool RdpUseGlobalDefaults { get; set; } = true;
    public bool RdpRedirectClipboard { get; set; } = true;
    public bool RdpRedirectDrives { get; set; }
    public bool RdpRedirectPrinters { get; set; }
    public bool RdpRedirectComPorts { get; set; }
    public bool RdpRedirectSmartCards { get; set; }
    public bool RdpRedirectWebcam { get; set; }
    public bool RdpRedirectUsb { get; set; }
    public int RdpAudioMode { get; set; }
    public bool RdpAudioCapture { get; set; }
    public bool RdpMultiMonitor { get; set; }
    public int[] RdpSelectedMonitorIndices { get; set; } = [];
    public bool RdpDynamicResolution { get; set; } = true;
    public bool RdpNla { get; set; } = true;
    public bool RdpStrictServerAuthentication { get; set; }
    public int RdpColorDepth { get; set; } = 32;
    public bool RdpBitmapCaching { get; set; } = true;
    public bool RdpCompression { get; set; } = true;
    public bool RdpAutoReconnect { get; set; } = true;
    public bool RdpAdminMode { get; set; }
    public bool RdpFullScreen { get; set; }
    public int RdpPerformanceFlags { get; set; }
    public bool RdpDisableUdp { get; set; }
    public string? RdpGateway { get; set; }
    public string? Environment { get; set; }

    /// <summary>MAC address for Wake-on-LAN (format: AA:BB:CC:DD:EE:FF).</summary>
    public string? MacAddress { get; set; }

    // Local shell settings
    public string? LocalShellExecutable { get; set; }
    public string? LocalShellArguments { get; set; }
    public string? LocalShellWorkingDirectory { get; set; }
    public bool LocalShellElevated { get; set; }
    public Models.ElevationMode ElevationMode { get; set; } = Models.ElevationMode.None;

    /// <summary>
    /// True when the user has authored or explicitly vetted this profile's local-execution
    /// payload (e.g. via the server dialog). Profiles serialized before this field, and all
    /// imported profiles, deserialize to false. A later connect-time guard uses this flag.
    /// </summary>
    public bool ExecutionConfirmed { get; set; }

    /// <summary>
    /// Returns the effective elevation mode: if <see cref="ElevationMode"/> is
    /// <see cref="Models.ElevationMode.None"/> but legacy <see cref="LocalShellElevated"/>
    /// is true, returns <see cref="Models.ElevationMode.Auto"/> for backward compatibility.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Models.ElevationMode EffectiveElevationMode =>
        ElevationMode != Models.ElevationMode.None ? ElevationMode
        : LocalShellElevated ? Models.ElevationMode.Auto
        : Models.ElevationMode.None;

    // Citrix settings
    public string? CitrixStoreFrontUrl { get; set; }
    public string? CitrixAppName { get; set; }
    public string? CitrixIcaFilePath { get; set; }
    public bool CitrixSeamlessMode { get; set; } = true;
    public bool CitrixUseSso { get; set; } = true;

    /// <summary>Pre-authenticated SelfService.exe launch arguments from cache XML.</summary>
    public string? CitrixLaunchCommandLine { get; set; }

    // FTP settings
    public int FtpPort { get; set; } = DefaultPorts.Ftp;
    public string? FtpUsername { get; set; }
    public string? FtpPasswordEncrypted { get; set; }

    // VNC settings
    public int VncPort { get; set; } = DefaultPorts.Vnc;
    public string? VncPassword { get; set; }

    // FTP options
    public bool FtpPassiveMode { get; set; } = true;
    public bool FtpUseSsl { get; set; }

    // VNC options
    public bool VncViewOnly { get; set; }

    // Telnet settings
    public int TelnetPort { get; set; } = DefaultPorts.Telnet;
    public string? TelnetUsername { get; set; }
    public string? TelnetPasswordEncrypted { get; set; }
}

internal sealed class WinRmIdentityModeJsonConverter
    : JsonStringEnumConverter<WinRmIdentityMode>
{
    public WinRmIdentityModeJsonConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}
