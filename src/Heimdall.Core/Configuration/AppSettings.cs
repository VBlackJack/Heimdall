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
using Heimdall.Core.Certificates;
using Heimdall.Core.Models;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.Ssh;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Strongly-typed application settings mapped to settings.json.
/// Default values match the legacy settings.default.json for backward compatibility.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } =
        new(StringComparer.Ordinal);

    // Display
    public int DefaultResolutionWidth { get; set; } = 1920;
    public int DefaultResolutionHeight { get; set; } = 1080;
    public bool FullScreen { get; set; } = true;
    public bool AdminMode { get; set; } = true;
    public string DefaultLocale { get; set; } = "en";
    public string DefaultTheme { get; set; } = "Drakul";
    public string AccentTint { get; set; } = "Default";

    // Tools (legacy Plink paths, kept for import compatibility)
    public string PlinkPath { get; set; } = @"C:\Program Files\PuTTY\plink.exe";
    public string? PuttyPath { get; set; }
    public string? PsftpPath { get; set; }

    // Updates
    public bool UpdateCheckEnabled { get; set; } = true;
    public int UpdateCheckIntervalHours { get; set; } = 24;
    public string? UpdateLastCheckUtc { get; set; } = null;   // ISO 8601 round-trip (UTC)
    public string? UpdateSkippedVersion { get; set; } = null;
    public string UpdateRepositoryOwner { get; set; } = "VBlackJack";
    public string UpdateRepositoryName { get; set; } = "Heimdall";

    // Legacy migration offer
    public int LegacyMigrationDeclinedOfferVersion { get; set; }
    public string? LegacyMigrationDeclinedSourceFingerprint { get; set; }

    // Tunnels
    public int TunnelEstablishmentDelayMs { get; set; } = 2500;
    public int TunnelRetryDelayMs { get; set; } = 1500;
    public int ProcessKillTimeoutMs { get; set; } = 2000;
    public int ExternalToolTimeoutMs { get; set; } = 60000;

    // Infrastructure timeouts (centralized from previously hardcoded values)
    public int HostKeyProbeTimeoutMs { get; set; } = 8000;
    public int TelnetConnectTimeoutMs { get; set; } = 15000;
    public int CredentialProviderTimeoutMs { get; set; } = 10000;
    public int RdpCredentialAutofillTimeoutMs { get; set; } = 90000;
    public int RdpArtifactCleanupDelayMs { get; set; } = 10000;
    public int RdpResizeEnableDelayMs { get; set; } = 10000;
    public int RdpConnectWatchdogTimeoutMs { get; set; } = 45000;
    public const int DefaultRdpAutoReconnectMaxAttempts = 20;
    public int RdpAutoReconnectMaxAttempts { get; set; } = DefaultRdpAutoReconnectMaxAttempts;
    public int RdpKeepAliveIntervalMs { get; set; } = 60000;
    public const int DefaultSshKeepAliveIntervalSeconds = 30;
    public int SshKeepAliveIntervalSeconds { get; set; } = DefaultSshKeepAliveIntervalSeconds;
    public const int DefaultPlinkPortCheckIntervalMs = 2000;
    public int PlinkPortCheckIntervalMs { get; set; } = DefaultPlinkPortCheckIntervalMs;
    public const int DefaultPlinkKillGracePeriodMs = 2000;
    public int PlinkKillGracePeriodMs { get; set; } = DefaultPlinkKillGracePeriodMs;
    public int SftpUploadDebounceMs { get; set; } = 2000;
    public int ServerShutdownTimeoutMs { get; set; } = 2000;
    public int SleepPreventionIntervalSeconds { get; set; } = 60;
    public int FileLoggerFlushIntervalMs { get; set; } = 2000;
    public int DefaultRdpTunnelPort { get; set; } = DefaultPorts.RdpTunnel;
    public int DefaultSshTunnelPort { get; set; } = DefaultPorts.SshTunnel;
    public int EphemeralHttpPort { get; set; } = 8080;
    public int EphemeralTftpPort { get; set; } = 69;
    public bool FileShareEnableTftp { get; set; }

    // Logging
    public bool EnableLogging { get; set; } = true;
    public string LogFilePath { get; set; } = @"logs\heimdall.log";

    // Security
    public string? PinHash { get; set; }
    public string? PinSalt { get; set; }

    /// <summary>Persisted count of consecutive failed PIN attempts, restored on startup
    /// so brute-force lockout survives an application restart.</summary>
    public int PinFailureCount { get; set; }

    /// <summary>Persisted absolute UTC instant until which the PIN is locked out, or null
    /// when not locked out. Restored on startup so lockout survives an application restart.</summary>
    public DateTime? PinLockoutUntilUtc { get; set; }

    public string? HmacKey { get; set; }
    public DateTime? HmacKeyCreatedAt { get; set; }
    public string? LastDpapiUser { get; set; }
    public bool RequireCredentialGuard { get; set; }
    public bool EnableEventLog { get; set; }

    // Terminal appearance
    public string TerminalFontFamily { get; set; } = "Consolas";
    public int TerminalFontSize { get; set; } = 14;
    public string TerminalColorScheme { get; set; } = "Dracula";
    public string PowerShellExecutionPolicy { get; set; } = "Default";

    // SSH defaults
    public string SshDefaultMode { get; set; } = "Embedded";
    [JsonConverter(typeof(JsonStringEnumConverter<SshAgentPreference>))]
    public SshAgentPreference SshAgentPreference { get; set; } = SshAgentPreference.AutoOpenSshFirst;
    public bool SyncKnownHostsAtStartup { get; set; }
    public int AntiIdleIntervalSeconds { get; set; } = 60;
    public const int DefaultSshTmoutResetIntervalSeconds = 240;
    public int SshTmoutResetIntervalSeconds { get; set; } = DefaultSshTmoutResetIntervalSeconds;
    public bool SshAutoReconnect { get; set; }
    public int SshAutoReconnectAttempts { get; set; } = 3;
    public int SshAutoReconnectFirstDelaySeconds { get; set; } = 2;
    public int SshAutoReconnectSecondDelaySeconds { get; set; } = 5;
    public int SshAutoReconnectSubsequentDelaySeconds { get; set; } = 15;
    public int SshConnectTimeExitWindowSeconds { get; set; } = 15;

    // RDP defaults
    public string RdpDefaultMode { get; set; } = "Embedded";
    public bool RdpDefaultRedirectClipboard { get; set; } = true;
    public bool RdpDefaultRedirectDrives { get; set; }
    public bool RdpDefaultRedirectPrinters { get; set; }
    public bool RdpDefaultRedirectComPorts { get; set; }
    public bool RdpDefaultRedirectSmartCards { get; set; }
    public bool RdpDefaultRedirectWebcam { get; set; }
    public bool RdpDefaultRedirectUsb { get; set; }
    public int RdpDefaultAudioMode { get; set; }
    public bool RdpDefaultAudioCapture { get; set; }
    public bool RdpDefaultMultiMonitor { get; set; }
    public bool RdpDefaultDynamicResolution { get; set; } = true;
    public bool RdpDefaultNla { get; set; } = true;
    public bool RdpDefaultStrictServerAuthentication { get; set; }
    public int RdpDefaultColorDepth { get; set; } = 32;
    public bool RdpDefaultBitmapCaching { get; set; } = true;
    public bool RdpDefaultCompression { get; set; } = true;

    /// <summary>
    /// Default for letting the RDP control decode and present through the graphics adapter.
    /// </summary>
    /// <remarks>
    /// Off by default: measured at 383 MB and 840 kernel handles saved across three
    /// concurrent 1920x1080 sessions (issue #161).
    /// </remarks>
    public bool RdpDefaultHardwareAcceleration { get; set; }
    public bool RdpDefaultAutoReconnect { get; set; } = true;
    public bool RdpDialogAdvancedDefault { get; set; }
    public bool RdpConfirmReconnectOnResize { get; set; }

    /// <summary>
    /// When true, the embedded RDP Disconnect button asks for confirmation before tearing down the session.
    /// </summary>
    public bool RdpConfirmDisconnect { get; set; } = true;

    /// <summary>
    /// When true, the embedded RDP toolbar displays every redirection indicator
    /// (clipboard, drives, printers, ...) regardless of whether the redirection
    /// is enabled. When false (default), disabled redirections are hidden and
    /// reachable through a discreet "+N" expander to keep the status area
    /// readable on profiles with most redirections turned off.
    /// </summary>
    public bool RdpRedirectionIndicatorsAlwaysExpanded { get; set; }

    /// <summary>
    /// User-configurable resolution presets shown in the embedded RDP session
    /// header's resolution menu. Values are formatted as "WIDTHxHEIGHT". Empty
    /// or null falls back to the built-in 10-preset set.
    /// </summary>
    public string[] RdpResolutionPresets { get; set; } =
    [
        "1920x1080", "1680x1050", "1600x900", "1440x900", "1366x768",
        "1280x1024", "1280x720", "1024x768", "2560x1440", "3840x2160"
    ];

    // Session
    public bool EnableSessionPersistence { get; set; }
    public const int DefaultMaxEmbeddedSessions = 10;
    public int MaxEmbeddedSessions { get; set; } = DefaultMaxEmbeddedSessions;
    public int EmbeddedIdleTimeoutMs { get; set; }
    public bool SftpBrowserEnabled { get; set; } = true;
    public bool SftpAutoOpenOnSsh { get; set; } = true;
    public bool SftpFollowSshDirectory { get; set; }
    public string ExternalEditorPath { get; set; } = @"%windir%\system32\notepad.exe";
    public bool PreventSleepDuringSession { get; set; } = true;
    public bool SessionLoggingEnabled { get; set; }
    public string SessionLogDirectory { get; set; } = @"logs\sessions";

    /// <summary>
    /// Scope applied when broadcast (type-once, send-to-many) mode is active.
    /// Defaults to <see cref="BroadcastScope.CurrentTab"/> so input never reaches
    /// background tabs without an explicit, confirmed opt-in.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<BroadcastScope>))]
    public BroadcastScope BroadcastScope { get; set; } = BroadcastScope.CurrentTab;
    public string NotesDirectory { get; set; } = @"config\notes";
    public int NotesSidebarWidth { get; set; } = 300;

    // UI state
    /// <summary>
    /// Application default for the initial Tunnels panel state when a session has no per-profile override
    /// (<see cref="ServerProfileDto.TunnelsPanelExpanded"/> == null).
    /// true = panel starts collapsed; false = panel starts expanded.
    /// Per-profile manual choice always wins.
    /// </summary>
    public bool CollapseTunnelsPanelByDefault { get; set; } = true;

    public bool SidebarCollapsed { get; set; }
    public int SidebarWidth { get; set; } = 220;
    public bool ShowToolsPanel { get; set; }
    public Dictionary<string, bool> SidebarExpandedCategories { get; set; } = new();
    public List<string> FavoriteToolIds { get; set; } = new();
    public bool OnboardingCompleted { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
    public List<string> TreeExpandedNodes { get; set; } = new();
    public string? TunnelGridColumnWidths { get; set; }
    public bool ServerDialogAdvancedMode { get; set; }
    public string? LastUsedGatewayId { get; set; }

    // Collections
    public List<SshGatewayDto> SshGateways { get; set; } = new();
    public List<ProjectDto> Projects { get; set; } = new();

    /// <summary>
    /// Group-level default settings for connection inheritance.
    /// Key: group path (e.g., "Production/Linux"). Servers in this group
    /// inherit these values when their own fields are null/empty.
    /// Hierarchical: "PROD/Linux" inherits from "PROD".
    /// </summary>
    public Dictionary<string, GroupDefaultsDto> GroupDefaults { get; set; } = new();

    /// <summary>
    /// Empty groups persisted so they remain visible in the TreeView even without servers.
    /// Each entry is a raw group path (the full folder path), e.g. "Infrastructure/Linux".
    /// </summary>
    public List<string> EmptyGroups { get; set; } = new();

    // SSH host key trust store (TOFU — persisted across restarts)
    // Key: "host:port", Value: "SHA256:<base64-no-padding>"
    public Dictionary<string, string> TrustedHostKeys { get; set; } = new();

    // SSH host key trust store v2 with metadata.
    // Key: "host:port" or "[ipv6]:port"; Value: fingerprint + provenance.
    public Dictionary<string, HostKeyEntry> TrustedHostKeysV2 { get; set; } = new();

    // FTPS certificate trust store with metadata.
    // Key: "host:port" or "[ipv6]:port"; Value: certificate fingerprint + provenance.
    public Dictionary<string, FtpsCertificateEntry> TrustedFtpsCertificates { get; set; } = new();

    /// <summary>RDP certificates trusted per profile - a SET each, never one.</summary>
    /// <remarks>
    /// Keyed by profile identifier, and holding a LIST rather than a single value, because
    /// one name can front several machines: a pool of domain controllers each carrying its
    /// own self-signed certificate. Windows keeps one thumbprint per name and therefore
    /// re-asks forever; holding the set is the point of the feature.
    /// <para>
    /// Kept in settings rather than on the profile so that exporting a profile does not
    /// carry trust decisions to another machine.
    /// </para>
    /// </remarks>
    public Dictionary<string, List<RdpCertificateEntry>> TrustedRdpCertificates { get; set; } = new();

    // Scheduled connections
    public List<ScheduledTaskDto> ScheduledTasks { get; set; } = new();

    // External credential provider (KeePassXC, Bitwarden CLI, 1Password CLI, etc.)
    public bool UseExternalCredentialProvider { get; set; }

    // Which provider implementation to use when the external provider is enabled.
    [JsonConverter(typeof(JsonStringEnumConverter<CredentialProviderKind>))]
    public CredentialProviderKind CredentialProviderType { get; set; } = CredentialProviderKind.Command;

    public string? CredentialProviderCommand { get; set; }
    public string? CredentialProviderDatabase { get; set; }

    // A file path (KeePassXC key file), not a secret -> plaintext. Mirrors CredentialProviderDatabase.
    public string? CredentialProviderKeyFile { get; set; }

    // Optional second command that retrieves the username from the vault. Run only when
    // the profile has no username. A command template, not a secret -> plaintext.
    public string? CredentialProviderUsernameCommand { get; set; }

    // When true, take only the first non-empty line of command output (for tools that
    // print the value on line 1 followed by status text, e.g. KeePass2 KPScript, pass).
    public bool CredentialProviderFirstLineOnly { get; set; }

    // DPAPI-encrypted unlock secret (database master password / GPG passphrase)
    // written to the provider command's stdin. Never stored in plaintext.
    public string? CredentialProviderUnlockSecretEncrypted { get; set; }

    // Windows Hello (biometric/PIN) gate evaluated at connect time, before stored
    // credentials are resolved or used. Fail-closed when enabled but unavailable.
    public bool RequireWindowsHelloOnConnect { get; set; }

    /// <summary>Default grace window (minutes) for a successful Windows Hello verification.</summary>
    public const int DefaultWindowsHelloGraceMinutes = 5;

    // Minutes a successful verification is remembered (in-memory, not persisted across
    // restarts) before the user is prompted again. 0 = always re-verify.
    public int WindowsHelloGraceMinutes { get; set; } = DefaultWindowsHelloGraceMinutes;

    // External tools (launched from server context menu)
    public List<ExternalToolDefinition> ExternalTools { get; set; } = new();

    // External tool provider paths (NirSoft / Sysinternals / NanaRun detection)
    public string? SysinternalsPath { get; set; }
    public string? NirSoftPath { get; set; }
    public string? NanaRunPath { get; set; }

    // X11 forwarding
    public string? X11ServerPath { get; set; }
    public bool X11AutoStart { get; set; } = true;

    // Session health monitor (background reachability probe of the inventory)
    public bool SessionHealthMonitorEnabled { get; set; } = true;
    public int SessionHealthCheckIntervalSeconds { get; set; } = 60;
    public int SessionHealthProbeTimeoutMs { get; set; } = 2000;
    public int SessionHealthMaxConcurrent { get; set; } = 10;

    // Command Library Git Sync
    public bool CmdLibGitSyncEnabled { get; set; }
    public string? CmdLibGitSyncUrl { get; set; }
    public string? CmdLibGitSyncToken { get; set; }
    public string CmdLibGitSyncBranch { get; set; } = "main";
    public string CmdLibGitSyncAuthorName { get; set; } = "Heimdall User";
    public string CmdLibGitSyncAuthorEmail { get; set; } = "heimdall@local";
    public bool CmdLibGitSyncOnStartup { get; set; }
    public bool CmdLibGitSyncAutoPush { get; set; } = true;

    // Master-password vault (DEK/KEK + DPAPI). These are NOT bare secrets:
    // VaultWrappedDek is already double-protected (Argon2id KEK wrap + DPAPI).
    /// <summary>Whether the master-password vault is configured (the "vault enabled" flag).</summary>
    public bool VaultEnabled { get; set; }

    /// <summary>DPAPI-wrapped <c>VaultEnvelope</c> holding the DEK (output of
    /// <c>VaultKeyManager.WrapDek</c>). The Argon2id parameters live inside the envelope,
    /// so no separate parameter fields are needed.</summary>
    public string? VaultWrappedDek { get; set; }

    /// <summary>Resumable forward-migration state for the vault.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<VaultMigrationState>))]
    public VaultMigrationState VaultMigrationState { get; set; } = VaultMigrationState.None;

    /// <summary>UTC timestamp (ISO-8601) of when the vault was first enabled. Non-secret.</summary>
    public string? VaultCreatedAt { get; set; }

    /// <summary>Stable non-secret vault identifier bound into Windows Hello AAD.</summary>
    public string? VaultId { get; set; }

    /// <summary>Whether a Windows Hello-wrapped DEK copy is enrolled.</summary>
    public bool VaultHelloEnrolled { get; set; }

    /// <summary>DPAPI-wrapped Hello envelope holding the DEK copy.</summary>
    public string? VaultHelloWrappedDek { get; set; }

    /// <summary>Base64 challenge signed by the Windows Hello KeyCredential.</summary>
    public string? VaultHelloChallenge { get; set; }

    /// <summary>Base64 HKDF salt for the Windows Hello KEK derivation.</summary>
    public string? VaultHelloSalt { get; set; }

    /// <summary>Windows Hello KeyCredential name for this vault.</summary>
    public string? VaultHelloCredentialName { get; set; }

    /// <summary>SHA-256 hash of the enrolled Hello public key, encoded as uppercase hex.</summary>
    public string? VaultHelloPublicKeyHash { get; set; }

    /// <summary>
    /// Maximum days before requiring a master-password unlock instead of Hello.
    /// 0 disables the periodic re-authentication policy.
    /// </summary>
    public int VaultHelloMaxDaysBeforeMasterPassword { get; set; }

    /// <summary>
    /// UTC timestamp of the last successful master-password vault unlock.
    /// Hello unlocks do not update this value; it drives periodic master-password
    /// re-authentication for Windows Hello.
    /// </summary>
    public DateTimeOffset? VaultLastMasterUnlockUtc { get; set; }

    /// <summary>Persisted count of consecutive failed master-password unlock attempts,
    /// restored on startup so the unlock-gate lockout survives an application restart.
    /// Mirrors <see cref="PinFailureCount"/>.</summary>
    public int VaultUnlockFailureCount { get; set; }

    /// <summary>Persisted absolute UTC instant until which the unlock gate is locked out,
    /// or null when not locked out. Mirrors <see cref="PinLockoutUntilUtc"/>.</summary>
    public DateTime? VaultUnlockLockoutUntilUtc { get; set; }

    /// <summary>Idle auto-lock threshold in minutes for the master-password workspace.
    /// 0 disables idle auto-lock. Measured system-wide (GetLastInputInfo). Only active
    /// when the vault is enabled.</summary>
    public int AutoLockIdleMinutes { get; set; }

    /// <summary>When true, locking the workspace also disconnects every active session
    /// (D3 teardown). Default false = survive-and-mask (sessions keep running, hidden).</summary>
    public bool DisconnectOnLock { get; set; }
}
