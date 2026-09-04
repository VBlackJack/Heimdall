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

    /// <summary>
    /// The inventory profile this copy belongs to, once <see cref="Id"/> has been replaced by a
    /// key minted for one session; null until that happens.
    /// <para>Not "null whenever the identifier is the profile's own": the ordinary connect path
    /// puts the key on for the length of the connect and then restores the original
    /// <see cref="Id"/> without clearing this, so a profile can carry an origin equal to its own
    /// identifier. <see cref="InventoryProfileId"/> answers the same either way, which is why
    /// the restore leaves it alone.</para>
    /// </summary>
    /// <remarks>
    /// <para>Transient, never serialized, and null by default - like
    /// <see cref="AllowCredentialPrompt"/> below, it carries a decision rather than a derived
    /// value, which is why it says so here.</para>
    /// <para><b>Carried, because it cannot be recovered.</b> A pane-scoped copy is minted a key
    /// of the form <c>&lt;profileId&gt;_&lt;8 hex&gt;</c> so tunnel lifetime and error recovery
    /// stay independent. Two earlier attempts tried to invert that afterwards and both handed one
    /// profile's certificate approval to another. Reading the shape decoded the imported profile
    /// <c>prod_deadbeef</c> to <c>prod</c>. Asking the inventory fixed that and not the profile
    /// deleted while its own connection was still being established, which is absent for the same
    /// reason a minted key is. Keeping a process-wide record of every mint fixed that in turn and
    /// not an import that arrives carrying a string some earlier session was minted - the session
    /// identifier is written to the log, and an import preserves whatever identifier its file
    /// held. Each of those asked what a STRING was; the question is what this OBJECT is, and only
    /// the code that replaced the identifier knows.</para>
    /// <para><b>Forgetting to set it is the safe direction.</b> A mint that leaves this null files
    /// an approval under a key that dies with the pane, so the certificate is asked about again
    /// next time. There is no arrangement of this field that sends an approval to a profile that
    /// did not earn it without a call site explicitly naming that profile.</para>
    /// </remarks>
    [JsonIgnore]
    public string? SessionOriginProfileId { get; private set; }

    /// <summary>
    /// The inventory profile this copy stands for: <see cref="SessionOriginProfileId"/> when a
    /// session key has replaced <see cref="Id"/>, and <see cref="Id"/> otherwise.
    /// </summary>
    /// <remarks>
    /// <para>The single spelling of the rule. Three surfaces ask it - the RDP certificate
    /// question, the execution-trust prompt, and the disconnect overlay's "Edit profile" - and
    /// surfaces agreeing by resemblance is how one of them once shipped inverting the mint
    /// unconditionally while another silently answered "server not found".</para>
    /// <para>Named for what it answers rather than for who asks. It was briefly
    /// <c>TrustProfileId</c>, which read as though trust were its meaning; trust is one reader of
    /// it, and the reader that had been getting a pane key and finding no profile at all was the
    /// edit button.</para>
    /// </remarks>
    [JsonIgnore]
    public string InventoryProfileId =>
        string.IsNullOrWhiteSpace(SessionOriginProfileId) ? Id : SessionOriginProfileId;

    /// <summary>
    /// Replaces <see cref="Id"/> with a key minted for one session, recording the profile being
    /// left behind.
    /// </summary>
    /// <remarks>
    /// The only supported way to put a minted key on a profile, so the origin is recorded at the
    /// one instant it is known rather than reconstructed later from the key's text. A copy that
    /// already carries an origin keeps it: minting again over a pane-scoped profile - a split of
    /// a split - must still name the inventory profile at the bottom, not the pane above it.
    /// </remarks>
    public void AdoptSessionIdentity(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (string.IsNullOrWhiteSpace(SessionOriginProfileId) && !string.IsNullOrWhiteSpace(Id))
        {
            SessionOriginProfileId = Id;
        }

        Id = sessionId;
    }

    /// <summary>
    /// Whether this profile stands for a destination the user typed by hand rather than for a
    /// profile in the inventory.
    /// </summary>
    /// <remarks>
    /// <para>Transient and never serialized, like <see cref="SessionOriginProfileId"/>, and for
    /// the same reason: it records what this OBJECT is, which only the code that built it knows.
    /// A typed destination's identifier is minted in a namespace a saved profile can also hold -
    /// through an old import, an old installation or a hand edit - so the identifier's text
    /// cannot say which of the two a profile is. The RDP certificate check reads this mark to
    /// file an approval under the typed destination's host instead of under a profile identifier
    /// that a saved profile may share.</para>
    /// <para><b>Forgetting to set it is the safe direction.</b> An unmarked typed destination is
    /// treated as a profile named by its minted identifier, which is what every typed destination
    /// was before this mark existed: no approval reaches an owner that did not earn it, and the
    /// only cost is the collision this mark was added to end.</para>
    /// <para>Survives <see cref="CloneFaithfully"/>, which is how a reconnect and a duplicate
    /// reach the pane; does not survive a JSON round trip or the save-as-profile dialog, which
    /// builds a new profile under a new identifier - a saved profile is not a typed destination,
    /// whatever it was saved from.</para>
    /// </remarks>
    [JsonIgnore]
    public bool IsTypedDestination { get; private set; }

    /// <summary>Records that this profile was built for a destination typed by hand.</summary>
    /// <remarks>
    /// The only way to set <see cref="IsTypedDestination"/>, so the mark is placed where the
    /// profile is minted and nowhere else - never by anything that reads a profile from disk.
    /// </remarks>
    public void MarkAsTypedDestination() => IsTypedDestination = true;
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

    /// <summary>
    /// Whether this attempt may put a credential question to the user.
    /// </summary>
    /// <remarks>
    /// Transient caller intent for one connect, never serialized, and false by default.
    /// The other <c>JsonIgnore</c> members on this type are derived values or
    /// presence flags; this one carries a decision, which is why it says so here.
    /// <para>
    /// It exists so a prompt can only appear on a connection the user asked for. Panes
    /// opened as a side effect - a companion browser alongside a shell, a restored
    /// session, a split - build their own profiles and leave this false, so they fail
    /// quietly rather than raising a modal nobody was expecting.
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public bool AllowCredentialPrompt { get; set; }

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
    [SettingRange(0, 2)]
    public int RdpAudioMode { get; set; }
    public bool RdpAudioCapture { get; set; }
    public bool RdpMultiMonitor { get; set; }
    public int[] RdpSelectedMonitorIndices { get; set; } = [];
    public bool RdpDynamicResolution { get; set; } = true;
    public bool RdpNla { get; set; } = true;
    public bool RdpStrictServerAuthentication { get; set; }
    [SettingRange(8, 32)]
    public int RdpColorDepth { get; set; } = 32;
    public bool RdpBitmapCaching { get; set; } = true;
    public bool RdpCompression { get; set; } = true;

    /// <summary>
    /// Lets the RDP control decode and present through the graphics adapter for this server.
    /// </summary>
    public bool RdpHardwareAcceleration { get; set; }
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

    /// <summary>
    /// Returns an independent copy of this profile, identical in every observable respect.
    /// </summary>
    /// <remarks>
    /// <para>Built on <see cref="object.MemberwiseClone"/> because this type is sealed: the shallow
    /// copy carries every backing field, the four private presence flags and any scalar member added
    /// later, without a hand-written assignment list that drifts. Two such lists existed and had
    /// drifted in opposite directions before this method.</para>
    /// <para>No property setter is used while cloning. <see cref="WinRmPort"/>,
    /// <see cref="SshPort"/>, <see cref="SshKeyPassphraseEncrypted"/> and
    /// <see cref="RdpResolutionMode"/> each raise their presence flag on assignment, including for a
    /// null or default value, so copying through them would fabricate presence on a clone whose
    /// source had none - which silently flips <see cref="UsesLegacySshCredentialMapping"/> and
    /// changes how the profile authenticates, and makes
    /// <c>RdpResolutionProfileMigration</c> skip the legacy migration on the copy.</para>
    /// <para>Every mutable reference is then replaced by a deep copy, so writing to the clone cannot
    /// reach the original: the monitor array, the post-connect steps with their own parameter
    /// dictionaries, and the JSON extension data. Each copied dictionary keeps the comparer of the
    /// dictionary it came from: imposing one would change how the copy is searched, which is a
    /// silent behaviour change even though every key is still present.</para>
    /// </remarks>
    public ServerProfileDto CloneFaithfully()
    {
        ServerProfileDto clone = (ServerProfileDto)MemberwiseClone();

        clone.RdpSelectedMonitorIndices = [.. RdpSelectedMonitorIndices];
        clone.PostConnectSteps = [.. PostConnectSteps.Select(CloneStep)];
        clone.ExtensionData = new Dictionary<string, JsonElement>(
            ExtensionData.Count,
            ExtensionData.Comparer);

        foreach (KeyValuePair<string, JsonElement> entry in ExtensionData)
        {
            // Clone(): the element otherwise stays bound to the document it was parsed from, which
            // the source profile owns.
            clone.ExtensionData[entry.Key] = entry.Value.Clone();
        }

        return clone;
    }

    private static PostConnectStep CloneStep(PostConnectStep step)
    {
        return new PostConnectStep
        {
            Id = step.Id,
            Input = step.Input,
            CommandLibraryId = step.CommandLibraryId,
            CommandLibraryParams = step.CommandLibraryParams is null
                ? null
                : new Dictionary<string, string>(
                    step.CommandLibraryParams,
                    step.CommandLibraryParams.Comparer),
            DelayMs = step.DelayMs,
            Enabled = step.Enabled,
            OnFailure = step.OnFailure,
        };
    }
}

internal sealed class WinRmIdentityModeJsonConverter
    : JsonStringEnumConverter<WinRmIdentityMode>
{
    public WinRmIdentityModeJsonConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}
