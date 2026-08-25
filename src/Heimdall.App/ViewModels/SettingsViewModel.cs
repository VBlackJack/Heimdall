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

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Extensions;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.Updates;
using SshAgentPreferenceEnum = Heimdall.Core.Ssh.SshAgentPreference;

namespace Heimdall.App.ViewModels;

/// <summary>
/// ViewModel for the application settings tab.
/// Tracks dirty state and delegates persistence to <see cref="IConfigManager"/>.
/// </summary>
public partial class SettingsViewModel : ObservableValidator, IDisposable
{
    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static typeInfo =>
                {
                    if (typeInfo.Type == typeof(ServerProfileDto))
                    {
                        RemoveJsonProperties(
                            typeInfo,
                            [
                                JsonNamingPolicy.CamelCase.ConvertName(nameof(ServerProfileDto.RdpPasswordEncrypted)),
                                JsonNamingPolicy.CamelCase.ConvertName(nameof(ServerProfileDto.SshPasswordEncrypted)),
                                JsonNamingPolicy.CamelCase.ConvertName(nameof(ServerProfileDto.WinRmPasswordEncrypted)),
                                JsonNamingPolicy.CamelCase.ConvertName(nameof(ServerProfileDto.FtpPasswordEncrypted)),
                                JsonNamingPolicy.CamelCase.ConvertName(nameof(ServerProfileDto.TelnetPasswordEncrypted)),
                                JsonNamingPolicy.CamelCase.ConvertName(nameof(ServerProfileDto.SshKeyPassphraseEncrypted)),
                                JsonNamingPolicy.CamelCase.ConvertName(nameof(ServerProfileDto.VncPassword)),
                                JsonNamingPolicy.CamelCase.ConvertName(nameof(ServerProfileDto.CitrixLaunchCommandLine))
                            ]);
                        return;
                    }

                    if (typeInfo.Type == typeof(SshGatewayDto))
                    {
                        RemoveJsonProperties(
                            typeInfo,
                            [
                                JsonNamingPolicy.CamelCase.ConvertName(nameof(SshGatewayDto.SshPasswordEncrypted)),
                                JsonNamingPolicy.CamelCase.ConvertName(nameof(SshGatewayDto.SshKeyPassphraseEncrypted))
                            ]);
                    }
                }
            }
        }
    };

    private static readonly JsonSerializerOptions ImportJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IConfigManager _configManager;
    private readonly LocalizationManager _localizer;
    private readonly IDialogService _dialogService;
    private readonly PinManager _pinManager;
    private readonly VaultLifecycleService _vaultLifecycle;
    private readonly IUpdateService _updateService;
    private readonly IAppVersionProvider _appVersionProvider;
    private readonly IUpdateInstallFlow _installFlow;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly IProfileImportService? _profileImportService;
    private readonly ICredentialGuardService _credentialGuardService;

    // The update found by the last successful check; drives the download-and-install action.
    private UpdateInfo? _availableUpdate;
    private string _updateReleaseUrl = string.Empty;
    private bool _disposed;

    // Repository coordinates for the update check, captured from settings on load.
    private string _updateOwner = "";
    private string _updateRepo = "";
    private int _mobaStoredCredentialCount;

    private string _originalTheme = "";
    private string _originalAccentTint = "Default";

    // Working buffers (mutated by CRUD, flushed to disk on Save)
    private List<SshGatewayDto> _pendingGateways = new();

    /// <summary>
    /// Shared with the network tab of the server dialog: both create a gateway from outside
    /// this panel, and one copy of that sequence is the point.
    /// </summary>
    private IGatewayCreationService? _gatewayCreation;
    private List<ProjectDto> _pendingProjects = new();

    // Projects removed before Save — servers are unassigned on flush
    private readonly List<string> _deletedProjectIds = new();

    // Gateways removed before Save — all reverse references are cleared on flush
    private readonly HashSet<string> _deletedGatewayIds = new(StringComparer.OrdinalIgnoreCase);

    // --- General ---

    [ObservableProperty]
    private string _defaultLocale = "en";

    [ObservableProperty]
    private string _defaultTheme = "Drakul";

    [ObservableProperty]
    private string _accentTint = "Default";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 20, ErrorMessage = "Max embedded sessions must be between 1 and 20.")]
    private int _maxEmbeddedSessions = 10;

    [ObservableProperty]
    private bool _preventSleepDuringSession = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReofferLegacyMigrationNextStartupCommand))]
    private bool _legacyMigrationReofferAvailable;

    [ObservableProperty]
    private bool _collapseTunnelsPanelByDefault = true;

    [ObservableProperty]
    private string _externalEditorPath = "";

    // --- Updates ---

    [ObservableProperty]
    private bool _updateCheckEnabled = true;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 8760, ErrorMessage = "Update check interval must be between 1 and 8760 hours.")]
    private int _updateCheckIntervalHours = 24;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAndInstallCommand))]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAndInstallCommand))]
    private bool _isInstallingUpdate;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadAndInstallCommand))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenUpdateReleaseCommand))]
    private bool _isUpdateReleaseAvailable;

    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    /// <summary>The raw informational version for display (never raises PropertyChanged, so it stays out of dirty).</summary>
    public string CurrentVersionText => _appVersionProvider.InformationalVersion;

    // --- Terminal ---

    [ObservableProperty]
    private string _terminalFontFamily = "Consolas";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(8, 72, ErrorMessage = "Terminal font size must be between 8 and 72.")]
    private int _terminalFontSize = 14;

    [ObservableProperty]
    private string _terminalColorScheme = "Dracula";

    [ObservableProperty]
    private string _powerShellExecutionPolicy = "Default";

    // --- SSH & SFTP ---

    [ObservableProperty]
    private string _plinkPath = "";

    [ObservableProperty]
    private string _puttyPath = "";

    [ObservableProperty]
    private string _sshDefaultMode = "Embedded";

    [ObservableProperty]
    private string _sshAgentPreference = "AutoOpenSshFirst";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(0, 3600, ErrorMessage = "Anti-idle interval must be between 0 and 3600 seconds.")]
    private int _antiIdleInterval = 60;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(0, 3600, ErrorMessage = "SSH TMOUT reset interval must be between 0 and 3600 seconds.")]
    private int _sshTmoutResetInterval = 240;

    [ObservableProperty]
    private bool _sshAutoReconnect;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 10, ErrorMessage = "SSH auto-reconnect attempts must be between 1 and 10.")]
    private int _sshAutoReconnectAttempts = 3;

    [ObservableProperty]
    private bool _sftpBrowserEnabled = true;

    [ObservableProperty]
    private bool _sftpAutoOpenOnSsh = true;

    [ObservableProperty]
    private bool _sftpFollowSshDirectory;

    [ObservableProperty]
    private string _x11ServerPath = "";

    [ObservableProperty]
    private bool _x11AutoStart = true;

    // --- External tool provider paths ---

    [ObservableProperty]
    private string _sysinternalsPath = "";

    [ObservableProperty]
    private string _nirSoftPath = "";

    [ObservableProperty]
    private string _nanaRunPath = "";

    // --- Command Library Git Sync ---

    [ObservableProperty]
    private bool _cmdLibGitSyncEnabled;

    [ObservableProperty]
    private string _cmdLibGitSyncUrl = "";

    [ObservableProperty]
    private string _cmdLibGitSyncBranch = "main";

    [ObservableProperty]
    private string _cmdLibGitSyncAuthorName = "Heimdall User";

    [ObservableProperty]
    private string _cmdLibGitSyncAuthorEmail = "heimdall@local";

    [ObservableProperty]
    private bool _cmdLibGitSyncOnStartup;

    [ObservableProperty]
    private bool _cmdLibGitSyncAutoPush = true;

    // --- RDP defaults ---

    [ObservableProperty]
    private int _defaultResolutionWidth = 1920;

    [ObservableProperty]
    private int _defaultResolutionHeight = 1080;

    [ObservableProperty]
    private string _rdpDefaultMode = "Embedded";

    [ObservableProperty]
    private bool _rdpDefaultNla = true;

    [ObservableProperty]
    private bool _rdpDefaultStrictServerAuthentication;

    [ObservableProperty]
    private int _rdpDefaultColorDepth = 32;

    [ObservableProperty]
    private bool _rdpDefaultDynamicResolution = true;

    [ObservableProperty]
    private bool _rdpDefaultMultiMonitor;

    [ObservableProperty]
    private bool _rdpDefaultRedirectClipboard = true;

    [ObservableProperty]
    private bool _rdpDefaultRedirectDrives;

    [ObservableProperty]
    private bool _rdpDefaultRedirectPrinters;

    [ObservableProperty]
    private bool _rdpDefaultRedirectComPorts;

    [ObservableProperty]
    private bool _rdpDefaultRedirectSmartCards;

    [ObservableProperty]
    private bool _rdpDefaultRedirectWebcam;

    [ObservableProperty]
    private bool _rdpDefaultRedirectUsb;

    [ObservableProperty]
    private bool _rdpDefaultAudioCapture;

    [ObservableProperty]
    private bool _rdpDefaultAutoReconnect = true;

    [ObservableProperty]
    private bool _rdpDefaultBitmapCaching = true;

    [ObservableProperty]
    private bool _rdpDefaultCompression = true;

    [ObservableProperty]
    private bool _rdpDefaultHardwareAcceleration;

    [ObservableProperty]
    private int _rdpDefaultAudioMode;

    [ObservableProperty]
    private string[] _rdpResolutionPresets = [];

    [ObservableProperty]
    private bool _rdpDialogAdvancedDefault;

    /// <summary>
    /// Multi-line text representation of <see cref="RdpResolutionPresets"/>
    /// for the Settings UI: one preset per line, format <c>WIDTHxHEIGHT</c>.
    /// Setter parses, trims, validates and rebuilds the array. Invalid lines
    /// are silently dropped — the user keeps editing what's left in the box.
    /// </summary>
    public string RdpResolutionPresetsText
    {
        get => string.Join(Environment.NewLine, RdpResolutionPresets);
        set
        {
            var parsed = (value ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line =>
                {
                    var parts = line.Split(['x', 'X', '×'], 2);
                    return parts.Length == 2
                        && int.TryParse(parts[0].Trim(), out var w) && w > 0
                        && int.TryParse(parts[1].Trim(), out var h) && h > 0;
                })
                .ToArray();

            if (!parsed.SequenceEqual(RdpResolutionPresets))
            {
                RdpResolutionPresets = parsed;
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void ResetRdpResolutionPresets()
    {
        RdpResolutionPresets =
        [
            "1920x1080", "1680x1050", "1600x900", "1440x900", "1366x768",
            "1280x1024", "1280x720", "1024x768", "2560x1440", "3840x2160"
        ];
        OnPropertyChanged(nameof(RdpResolutionPresetsText));
    }

    partial void OnRdpResolutionPresetsChanged(string[] value)
    {
        OnPropertyChanged(nameof(RdpResolutionPresetsText));
    }

    // --- Security ---

    [ObservableProperty]
    private bool _useExternalCredentialProvider;

    public string CredProvHelpText => UseExternalCredentialProvider
        ? ""
        : _localizer["SettingsCredProvDisabledHint"];

    partial void OnUseExternalCredentialProviderChanged(bool value)
    {
        OnPropertyChanged(nameof(CredProvHelpText));
    }

    [ObservableProperty]
    private CredentialProviderKind _credentialProviderType = CredentialProviderKind.Command;

    /// <summary>True when the command-based provider is selected (controls field visibility).</summary>
    public bool IsCommandProvider
    {
        get => CredentialProviderType == CredentialProviderKind.Command;
        set { if (value) CredentialProviderType = CredentialProviderKind.Command; }
    }

    /// <summary>True when the Windows Credential Manager provider is selected.</summary>
    public bool IsWindowsCredentialManagerProvider
    {
        get => CredentialProviderType == CredentialProviderKind.WindowsCredentialManager;
        set { if (value) CredentialProviderType = CredentialProviderKind.WindowsCredentialManager; }
    }

    partial void OnCredentialProviderTypeChanged(CredentialProviderKind value)
    {
        OnPropertyChanged(nameof(IsCommandProvider));
        OnPropertyChanged(nameof(IsWindowsCredentialManagerProvider));
    }

    [ObservableProperty]
    private string _credentialProviderCommand = "";

    [ObservableProperty]
    private string _credentialProviderDatabase = "";

    // Path to the KeePassXC key file (replaces {KeyFile}). A file path, not a secret.
    [ObservableProperty]
    private string _credentialProviderKeyFile = "";

    // Optional command that retrieves the username from the vault (plaintext template).
    [ObservableProperty]
    private string _credentialProviderUsernameCommand = "";

    // Take only the first non-empty line of command output (KeePass2 KPScript, pass, etc.).
    [ObservableProperty]
    private bool _credentialProviderFirstLineOnly;

    // Plaintext unlock secret held only in the view-model; persisted DPAPI-encrypted.
    [ObservableProperty]
    private string _credentialProviderUnlockSecret = "";

    [ObservableProperty]
    private int _credentialProviderTimeoutMs = 10000;

    [ObservableProperty]
    private bool _requireCredentialGuard;

    [ObservableProperty]
    private string _credentialGuardStatusText = string.Empty;

    partial void OnRequireCredentialGuardChanged(bool value)
    {
        if (!value)
        {
            CredentialGuardStatusText = string.Empty;
            return;
        }

        RefreshCredentialGuardStatusAsync().SafeFireAndForget();
    }

    private async Task RefreshCredentialGuardStatusAsync()
    {
        CredentialGuardStatus status = await _credentialGuardService.GetStatusAsync();
        CredentialGuardStatusText = status.State is CredentialGuardState.Active
            ? _localizer["CredentialGuardEnabled"]
            : _localizer["CredentialGuardDisabled"];

        if (status.State is CredentialGuardState.Indeterminate)
        {
            FileLogger.Warn(
                _localizer.Format(
                    "LogCredentialGuardCheckFailed",
                    status.FailureReason ?? "unknown error"));
        }
    }

    [ObservableProperty]
    private bool _requireWindowsHelloOnConnect;

    [ObservableProperty]
    private int _windowsHelloGraceMinutes = AppSettings.DefaultWindowsHelloGraceMinutes;

    /// <summary>Idle auto-lock threshold (minutes) for the vault workspace; 0 disables it.</summary>
    [ObservableProperty]
    private int _autoLockIdleMinutes;

    /// <summary>Whether locking the workspace also disconnects active sessions (D3).</summary>
    [ObservableProperty]
    private bool _disconnectOnLock;

    [ObservableProperty]
    private bool _isPinConfigured;

    partial void OnIsPinConfiguredChanged(bool value) => OnPropertyChanged(nameof(PinStatusText));

    public string PinStatusText => IsPinConfigured
        ? _localizer["SettingsPinStatusEnabled"]
        : _localizer["SettingsPinStatusDisabled"];

    [ObservableProperty]
    private bool _isVaultEnabled;

    partial void OnIsVaultEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(VaultStatusText));
        OnPropertyChanged(nameof(VaultDisabledActionsVisible));
        OnPropertyChanged(nameof(VaultEnabledActionsVisible));
        RefreshVaultHelloUiState();
    }

    public string VaultStatusText => IsVaultEnabled
        ? _localizer["SettingsVaultStatusEnabled"]
        : _localizer["SettingsVaultStatusDisabled"];

    /// <summary>Visibility flag for the "Enable" action (vault not yet configured).</summary>
    public bool VaultDisabledActionsVisible => !IsVaultEnabled;

    /// <summary>Visibility flag for the "Change" / "Disable" actions (vault configured).</summary>
    public bool VaultEnabledActionsVisible => IsVaultEnabled;

    [ObservableProperty]
    private bool _isVaultHelloAvailable;

    partial void OnIsVaultHelloAvailableChanged(bool value) => RefreshVaultHelloUiState();

    [ObservableProperty]
    private bool _isVaultHelloEnrolled;

    partial void OnIsVaultHelloEnrolledChanged(bool value) => RefreshVaultHelloUiState();

    [ObservableProperty]
    private bool _isVaultHelloBusy;

    partial void OnIsVaultHelloBusyChanged(bool value) => RefreshVaultHelloUiState();

    [ObservableProperty]
    private string _vaultHelloStatusText = "";

    public bool VaultHelloSectionVisible => IsVaultEnabled;

    public bool VaultHelloEnrollVisible => IsVaultEnabled && IsVaultHelloAvailable && !IsVaultHelloEnrolled;

    public bool VaultHelloDisableVisible => IsVaultEnabled && IsVaultHelloAvailable && IsVaultHelloEnrolled;

    public bool VaultHelloUnavailableVisible => IsVaultEnabled && !IsVaultHelloAvailable;

    public bool CanEnableVaultHello => VaultHelloEnrollVisible && !IsVaultHelloBusy;

    public bool CanDisableVaultHello => VaultHelloDisableVisible && !IsVaultHelloBusy;

    // --- UI state (persisted but not exposed in Settings tab) ---

    [ObservableProperty]
    private bool _showToolsPanel;

    // --- Advanced / File sharing ---

    [ObservableProperty]
    private bool _fileShareEnableTftp;

    // --- Advanced / Logging ---

    [ObservableProperty]
    private bool _enableLogging = true;

    [ObservableProperty]
    private bool _sessionLoggingEnabled;

    [ObservableProperty]
    private string _sessionLogDirectory = @"logs\sessions";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(0, 30000, ErrorMessage = "Tunnel establishment delay must be between 0 and 30000 ms.")]
    private int _tunnelEstablishmentDelayMs = 2500;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(SettingsViewModel), nameof(ValidateRdpConnectWatchdogTimeoutMsValue))]
    private int _rdpConnectWatchdogTimeoutMs = 45000;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(5000, 600000, ErrorMessage = "External tool timeout must be between 5000 and 600000 ms.")]
    private int _externalToolTimeoutMs = 60000;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(SettingsViewModel), nameof(ValidateRdpResizeEnableDelayMsValue))]
    private int _rdpResizeEnableDelayMs = 10000;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1000, 60000, ErrorMessage = "RDP artifact cleanup delay must be between 1000 and 60000 ms.")]
    private int _rdpArtifactCleanupDelayMs = 10000;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(5000, 300000, ErrorMessage = "RDP credential autofill timeout must be between 5000 and 300000 ms.")]
    private int _rdpCredentialAutofillTimeoutMs = 90000;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, AppSettings.DefaultRdpAutoReconnectMaxAttempts, ErrorMessage = "RDP auto-reconnect maximum attempts must be between 1 and 20.")]
    private int _rdpAutoReconnectMaxAttempts = AppSettings.DefaultRdpAutoReconnectMaxAttempts;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(5000, 300000, ErrorMessage = "RDP keep-alive interval must be between 5000 and 300000 ms.")]
    private int _rdpKeepAliveIntervalMs = 60000;

    // --- Session Health Monitor ---

    [ObservableProperty]
    private bool _sessionHealthMonitorEnabled = true;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(15, 3600, ErrorMessage = "Health check interval must be between 15 and 3600 seconds.")]
    private int _sessionHealthCheckIntervalSeconds = 60;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(250, 30000, ErrorMessage = "Probe timeout must be between 250 and 30000 ms.")]
    private int _sessionHealthProbeTimeoutMs = 2000;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 50, ErrorMessage = "Max concurrent probes must be between 1 and 50.")]
    private int _sessionHealthMaxConcurrent = 10;

    // --- Collections ---

    [ObservableProperty]
    private ObservableCollection<ExternalToolItemViewModel> _externalTools = new();

    [ObservableProperty]
    private ExternalToolItemViewModel? _selectedExternalTool;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private ObservableCollection<GatewayItemViewModel> _gateways = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditGatewayCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteGatewayCommand))]
    private GatewayItemViewModel? _selectedGateway;

    [ObservableProperty]
    private ObservableCollection<ProjectItemViewModel> _projects = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProjectCommand))]
    private ProjectItemViewModel? _selectedProject;

    /// <summary>
    /// Raised after a server import completes so the main shell can reload
    /// the server list and related UI state.
    /// </summary>
    public event Action? ConfigurationChanged;

    /// <summary>
    /// Raised when the user changes the theme selection so the shell can
    /// swap the active <see cref="System.Windows.ResourceDictionary"/> at runtime.
    /// </summary>
    public event Action<string>? ThemeChanged;

    public event Action<string>? AccentTintChanged;

    public SettingsViewModel(
        IConfigManager configManager,
        LocalizationManager localizer,
        IDialogService dialogService,
        TrustedHostKeysSettingsViewModel trustedHostKeys,
        PinManager pinManager,
        VaultLifecycleService vaultLifecycle,
        IUpdateService updateService,
        IAppVersionProvider appVersionProvider,
        IUpdateInstallFlow installFlow,
        IBrowserLauncher browserLauncher,
        IProfileImportService? profileImportService = null,
        ICredentialGuardService? credentialGuardService = null)
    {
        _configManager = configManager;
        _localizer = localizer;
        _dialogService = dialogService;
        _pinManager = pinManager;
        _vaultLifecycle = vaultLifecycle;
        _updateService = updateService;
        _appVersionProvider = appVersionProvider;
        _installFlow = installFlow;
        _browserLauncher = browserLauncher;
        _profileImportService = profileImportService;
        _credentialGuardService = credentialGuardService ?? new CredentialGuardService();
        TrustedHostKeys = trustedHostKeys;
    }

    private bool CanCheckNow() => !IsCheckingUpdate && !IsInstallingUpdate;

    /// <summary>
    /// Runs a one-shot update check against the configured repository and reports
    /// the outcome in <see cref="UpdateStatusText"/>. Never marks settings dirty.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync(CancellationToken cancellationToken)
    {
        IsCheckingUpdate = true;
        UpdateStatusText = _localizer.Format("SettingsUpdateStatusChecking");
        ClearUpdateActions();
        try
        {
            var current = _appVersionProvider.Current;
            if (current is null)
            {
                UpdateStatusText = _localizer.Format("SettingsUpdateStatusUnknownVersion");
                return;
            }

            var result = await _updateService.CheckForUpdatesAsync(current.Value, _updateOwner, _updateRepo, cancellationToken);
            if (result.Status == UpdateCheckStatus.UpdateAvailable)
            {
                _availableUpdate = result.Update;
                _updateReleaseUrl = result.Update!.ReleaseUrl;
                IsUpdateAvailable = true;
                IsUpdateReleaseAvailable = true;
            }
            else if (result.Status == UpdateCheckStatus.UpdateNotInstallable)
            {
                _updateReleaseUrl = result.Release!.HtmlUrl;
                IsUpdateReleaseAvailable = true;
            }

            UpdateStatusText = result.Status switch
            {
                UpdateCheckStatus.UpToDate => _localizer.Format("SettingsUpdateStatusUpToDate"),
                UpdateCheckStatus.UpdateAvailable => _localizer.Format("SettingsUpdateStatusAvailable", result.Update!.Version.ToString()),
                UpdateCheckStatus.UpdateNotInstallable => _localizer.Format("SettingsUpdateStatusNotInstallable", result.Release!.Version.ToString()),
                _ => _localizer.Format("SettingsUpdateStatusFailed"),
            };
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private void ClearUpdateActions()
    {
        _availableUpdate = null;
        _updateReleaseUrl = string.Empty;
        IsUpdateAvailable = false;
        IsUpdateReleaseAvailable = false;
    }

    private bool CanOpenUpdateRelease() =>
        IsUpdateReleaseAvailable && !string.IsNullOrWhiteSpace(_updateReleaseUrl);

    [RelayCommand(CanExecute = nameof(CanOpenUpdateRelease))]
    private void OpenUpdateRelease() => _browserLauncher.Open(_updateReleaseUrl);

    private bool CanDownloadAndInstall() =>
        IsUpdateAvailable && !IsCheckingUpdate && !IsInstallingUpdate;

    /// <summary>
    /// Downloads the verified installer for the available update, launches the detached
    /// relauncher, and shuts the app down so the installer can replace it. The app stays
    /// running on any failure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadAndInstall), IncludeCancelCommand = true)]
    private async Task DownloadAndInstallAsync(CancellationToken cancellationToken)
    {
        var update = _availableUpdate;
        if (update is null)
        {
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["SettingsUpdateConfirmTitle"],
            _localizer.Format("SettingsUpdateConfirmMessage", update.Version.ToString()));
        if (!confirmed)
        {
            return;
        }

        IsInstallingUpdate = true;
        DownloadProgress = 0;
        try
        {
            UpdateStatusText = _localizer.Format("SettingsUpdateStatusDownloading");
            var progress = new Progress<double>(p => DownloadProgress = p);
            var outcome = await _installFlow.RunAsync(update, progress, cancellationToken);
            var key = UpdateInstallOutcomeText.StatusKey(outcome);
            if (key is not null)
            {
                UpdateStatusText = _localizer.Format(key);
            }
        }
        finally
        {
            IsInstallingUpdate = false;
        }
    }

    public TrustedHostKeysSettingsViewModel TrustedHostKeys { get; }

    internal Func<string?>? ImportFilePathProvider { get; set; }

    internal Func<CitrixScanResult>? CitrixScanProvider { get; set; }

    internal Func<GatewayOverviewMutationRequest, CancellationToken, Task<int>>? GatewayReferenceMutationHandler { get; set; }

    /// <summary>
    /// Applies the current <see cref="SshDefaultMode"/> to every server in the inventory.
    /// </summary>
    [RelayCommand]
    private async Task ApplySshModeToAllAsync()
    {
        var servers = await _configManager.LoadServersAsync();
        var mode = SshDefaultMode;
        var changeCount = servers.Count(s => !string.Equals(s.SshMode, mode, StringComparison.Ordinal));

        if (changeCount == 0)
        {
            FileLogger.Info("ApplySshModeToAll: no changes needed.");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["ConfirmApplyAllTitle"],
            _localizer.Format("ConfirmApplySshModeMessage", mode, changeCount, servers.Count),
            "danger");

        if (!confirmed) return;

        var update = await _configManager.MutateServersAsync(currentServers =>
        {
            int updatedCount = 0;
            foreach (ServerProfileDto server in currentServers)
            {
                if (!string.Equals(server.SshMode, mode, StringComparison.Ordinal))
                {
                    server.SshMode = mode;
                    updatedCount++;
                }
            }

            return (UpdatedCount: updatedCount, TotalCount: currentServers.Count);
        });
        ConfigurationChanged?.Invoke();
        FileLogger.Info(
            $"Applied SSH mode '{mode}' to {update.UpdatedCount}/{update.TotalCount} servers.");
    }

    /// <summary>
    /// Applies the current <see cref="RdpDefaultMode"/> to every server in the inventory.
    /// </summary>
    [RelayCommand]
    private async Task ApplyRdpModeToAllAsync()
    {
        var servers = await _configManager.LoadServersAsync();
        var rdpServers = servers
            .Where(s => string.Equals(s.ConnectionType, "RDP", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var mode = RdpDefaultMode;
        var changeCount = rdpServers.Count(s => !string.Equals(s.RdpMode, mode, StringComparison.Ordinal));

        if (changeCount == 0)
        {
            FileLogger.Info("ApplyRdpModeToAll: no changes needed.");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["SettingsApplyModeToAllConfirmTitle"],
            _localizer.Format("SettingsApplyModeToAllConfirmBody", rdpServers.Count),
            "danger");

        if (!confirmed) return;

        var update = await _configManager.MutateServersAsync(currentServers =>
        {
            List<ServerProfileDto> currentRdpServers = currentServers
                .Where(server => string.Equals(
                    server.ConnectionType,
                    "RDP",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            int updatedCount = 0;
            foreach (ServerProfileDto server in currentRdpServers)
            {
                if (!string.Equals(server.RdpMode, mode, StringComparison.Ordinal))
                {
                    server.RdpMode = mode;
                    updatedCount++;
                }
            }

            return (UpdatedCount: updatedCount, TotalCount: currentRdpServers.Count);
        });
        ConfigurationChanged?.Invoke();
        FileLogger.Info(
            $"Applied RDP mode '{mode}' to {update.UpdatedCount}/{update.TotalCount} RDP servers.");
    }

    /// <summary>
    /// Populates ViewModel properties from the loaded <see cref="AppSettings"/>.
    /// Does not mark the ViewModel as dirty.
    /// </summary>
    public void LoadFromSettings(AppSettings settings)
    {
        // General
        DefaultLocale = settings.DefaultLocale;
        DefaultTheme = settings.DefaultTheme;
        AccentTint = settings.AccentTint;
        _originalTheme = settings.DefaultTheme;
        _originalAccentTint = settings.AccentTint;
        MaxEmbeddedSessions = settings.MaxEmbeddedSessions;
        PreventSleepDuringSession = settings.PreventSleepDuringSession;
        CollapseTunnelsPanelByDefault = settings.CollapseTunnelsPanelByDefault;
        ExternalEditorPath = settings.ExternalEditorPath;

        // Updates
        UpdateCheckEnabled = settings.UpdateCheckEnabled;
        UpdateCheckIntervalHours = settings.UpdateCheckIntervalHours;
        _updateOwner = settings.UpdateRepositoryOwner;
        _updateRepo = settings.UpdateRepositoryName;
        LegacyMigrationReofferAvailable =
            LegacyMigrationDecisionPolicy.HasDeclineMarker(settings);

        // UI state
        ShowToolsPanel = settings.ShowToolsPanel;

        // Advanced / File sharing
        FileShareEnableTftp = settings.FileShareEnableTftp;

        // Terminal
        TerminalFontFamily = settings.TerminalFontFamily;
        TerminalFontSize = settings.TerminalFontSize;
        TerminalColorScheme = settings.TerminalColorScheme;
        PowerShellExecutionPolicy = settings.PowerShellExecutionPolicy;

        // SSH & SFTP
        PlinkPath = settings.PlinkPath;
        PuttyPath = settings.PuttyPath ?? "";
        SshDefaultMode = settings.SshDefaultMode;
        SshAgentPreference = settings.SshAgentPreference.ToString();
        AntiIdleInterval = settings.AntiIdleIntervalSeconds;
        SshTmoutResetInterval = settings.SshTmoutResetIntervalSeconds;
        SshAutoReconnect = settings.SshAutoReconnect;
        SshAutoReconnectAttempts = settings.SshAutoReconnectAttempts;
        SftpBrowserEnabled = settings.SftpBrowserEnabled;
        SftpAutoOpenOnSsh = settings.SftpAutoOpenOnSsh;
        SftpFollowSshDirectory = settings.SftpFollowSshDirectory;
        X11ServerPath = settings.X11ServerPath ?? "";
        X11AutoStart = settings.X11AutoStart;
        SysinternalsPath = settings.SysinternalsPath ?? "";
        NirSoftPath = settings.NirSoftPath ?? "";
        NanaRunPath = settings.NanaRunPath ?? "";

        // Command Library Git Sync
        CmdLibGitSyncEnabled = settings.CmdLibGitSyncEnabled;
        CmdLibGitSyncUrl = settings.CmdLibGitSyncUrl ?? "";
        CmdLibGitSyncBranch = settings.CmdLibGitSyncBranch;
        CmdLibGitSyncAuthorName = settings.CmdLibGitSyncAuthorName;
        CmdLibGitSyncAuthorEmail = settings.CmdLibGitSyncAuthorEmail;
        CmdLibGitSyncOnStartup = settings.CmdLibGitSyncOnStartup;
        CmdLibGitSyncAutoPush = settings.CmdLibGitSyncAutoPush;

        // Session Health Monitor
        SessionHealthMonitorEnabled = settings.SessionHealthMonitorEnabled;
        SessionHealthCheckIntervalSeconds = settings.SessionHealthCheckIntervalSeconds;
        SessionHealthProbeTimeoutMs = settings.SessionHealthProbeTimeoutMs;
        SessionHealthMaxConcurrent = settings.SessionHealthMaxConcurrent;

        // RDP defaults
        DefaultResolutionWidth = settings.DefaultResolutionWidth;
        DefaultResolutionHeight = settings.DefaultResolutionHeight;
        RdpDefaultMode = settings.RdpDefaultMode;
        RdpDefaultNla = settings.RdpDefaultNla;
        RdpDefaultStrictServerAuthentication = settings.RdpDefaultStrictServerAuthentication;
        RdpDefaultColorDepth = settings.RdpDefaultColorDepth;
        RdpDefaultDynamicResolution = settings.RdpDefaultDynamicResolution;
        RdpDefaultMultiMonitor = settings.RdpDefaultMultiMonitor;
        RdpDefaultRedirectClipboard = settings.RdpDefaultRedirectClipboard;
        RdpDefaultRedirectDrives = settings.RdpDefaultRedirectDrives;
        RdpDefaultRedirectPrinters = settings.RdpDefaultRedirectPrinters;
        RdpDefaultRedirectComPorts = settings.RdpDefaultRedirectComPorts;
        RdpDefaultRedirectSmartCards = settings.RdpDefaultRedirectSmartCards;
        RdpDefaultRedirectWebcam = settings.RdpDefaultRedirectWebcam;
        RdpDefaultRedirectUsb = settings.RdpDefaultRedirectUsb;
        RdpDefaultAudioCapture = settings.RdpDefaultAudioCapture;
        RdpDefaultAutoReconnect = settings.RdpDefaultAutoReconnect;
        RdpDefaultBitmapCaching = settings.RdpDefaultBitmapCaching;
        RdpDefaultCompression = settings.RdpDefaultCompression;
        RdpDefaultHardwareAcceleration = settings.RdpDefaultHardwareAcceleration;
        RdpDefaultAudioMode = settings.RdpDefaultAudioMode;
        RdpResolutionPresets = settings.RdpResolutionPresets ?? [];
        RdpDialogAdvancedDefault = settings.RdpDialogAdvancedDefault;

        // Security
        UseExternalCredentialProvider = settings.UseExternalCredentialProvider;
        CredentialProviderType = settings.CredentialProviderType;
        CredentialProviderCommand = settings.CredentialProviderCommand ?? "";
        CredentialProviderDatabase = settings.CredentialProviderDatabase ?? "";
        CredentialProviderKeyFile = settings.CredentialProviderKeyFile ?? "";
        CredentialProviderUsernameCommand = settings.CredentialProviderUsernameCommand ?? "";
        CredentialProviderFirstLineOnly = settings.CredentialProviderFirstLineOnly;
        CredentialProviderUnlockSecret =
            CredentialProtector.Unprotect(settings.CredentialProviderUnlockSecretEncrypted) ?? "";
        CredentialProviderTimeoutMs = settings.CredentialProviderTimeoutMs;
        RequireCredentialGuard = settings.RequireCredentialGuard;
        RequireWindowsHelloOnConnect = settings.RequireWindowsHelloOnConnect;
        WindowsHelloGraceMinutes = settings.WindowsHelloGraceMinutes;
        AutoLockIdleMinutes = settings.AutoLockIdleMinutes;
        DisconnectOnLock = settings.DisconnectOnLock;
        IsPinConfigured = !string.IsNullOrEmpty(settings.PinHash) && !string.IsNullOrEmpty(settings.PinSalt);
        IsVaultEnabled = settings.VaultEnabled;
        IsVaultHelloEnrolled = settings.VaultHelloEnrolled;
        IsVaultHelloAvailable = false;
        RefreshVaultHelloUiState();

        // Advanced / Logging
        EnableLogging = settings.EnableLogging;
        SessionLoggingEnabled = settings.SessionLoggingEnabled;
        SessionLogDirectory = settings.SessionLogDirectory;
        TunnelEstablishmentDelayMs = settings.TunnelEstablishmentDelayMs;
        RdpConnectWatchdogTimeoutMs = settings.RdpConnectWatchdogTimeoutMs;
        ExternalToolTimeoutMs = settings.ExternalToolTimeoutMs;
        RdpResizeEnableDelayMs = settings.RdpResizeEnableDelayMs;
        RdpArtifactCleanupDelayMs = settings.RdpArtifactCleanupDelayMs;
        RdpCredentialAutofillTimeoutMs = settings.RdpCredentialAutofillTimeoutMs;
        RdpAutoReconnectMaxAttempts = settings.RdpAutoReconnectMaxAttempts;
        RdpKeepAliveIntervalMs = settings.RdpKeepAliveIntervalMs;

        UnsubscribeExternalToolTracking();

        ExternalTools = new ObservableCollection<ExternalToolItemViewModel>(
            settings.ExternalTools.Select(t => new ExternalToolItemViewModel
            {
                Name = t.Name,
                ExecutablePath = t.ExecutablePath,
                Arguments = t.Arguments,
                WorkingDirectory = t.WorkingDirectory,
                RunAsAdministrator = t.RunAsAdministrator,
                RunHidden = t.RunHidden
            }));

        SubscribeExternalToolTracking();

        Gateways = new ObservableCollection<GatewayItemViewModel>(
            settings.SshGateways.Select(g => new GatewayItemViewModel
            {
                Id = g.Id,
                Name = g.Name,
                Host = g.Host,
                Port = g.Port,
                User = g.User,
                HasKey = !string.IsNullOrEmpty(g.KeyPath),
                HasPassword = !string.IsNullOrEmpty(g.SshPasswordEncrypted),
                ParentGatewayId = g.ParentGatewayId
            }));

        Projects = new ObservableCollection<ProjectItemViewModel>(
            settings.Projects.Select(p => new ProjectItemViewModel
            {
                Id = p.Id,
                Name = p.Name,
                Color = p.Color ?? "#3B82F6",
                Description = p.Description ?? ""
            }));

        // Seed working buffers from loaded settings
        _pendingGateways = settings.SshGateways.Select(CloneGateway).ToList();
        _pendingProjects = settings.Projects.Select(CloneProject).ToList();
        _deletedProjectIds.Clear();
        _deletedGatewayIds.Clear();

        TrustedHostKeys.Refresh();
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (await TrySaveAsync(cancellationToken) || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Pressing Save and getting nothing back is the worst of the outcomes:
        // a validation failure at least raises a tab badge, while a persistence
        // failure was written to the log and nowhere else.
        _dialogService.ShowWarning(
            _localizer["SettingsCloseSaveFailedTitle"],
            _localizer["SettingsCloseSaveFailedMessage"]);
    }

    /// <summary>
    /// Validates and persists the current settings.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only after persistence completes and the view model
    /// is no longer dirty; otherwise <see langword="false"/>.
    /// </returns>
    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken = default)
    {
        ValidateAllProperties();
        RefreshValidationSummary();

        if (HasErrors)
        {
            return false;
        }

        // Validate external tools before persisting
        string? extToolError = ValidateExternalTools();
        if (extToolError is not null)
        {
            ValidationSummary = extToolError;
            HasValidationErrors = true;
            return false;
        }

        try
        {
            await PersistValidatedSettingsAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            FileLogger.Error("Settings save failed", ex);
            return false;
        }
    }

    private async Task PersistValidatedSettingsAsync(CancellationToken cancellationToken)
    {
        SshAgentPreferenceEnum parsedSshAgentPreference = Enum.TryParse<SshAgentPreferenceEnum>(
                SshAgentPreference,
                ignoreCase: false,
                out SshAgentPreferenceEnum sshAgentPreference)
                ? sshAgentPreference
                : SshAgentPreferenceEnum.AutoOpenSshFirst;
        List<ExternalToolDefinition> externalTools = ExternalTools.Select(tool => new ExternalToolDefinition
        {
            Name = tool.Name.Trim(),
            ExecutablePath = tool.ExecutablePath.Trim(),
            Arguments = tool.Arguments,
            WorkingDirectory = tool.WorkingDirectory,
            RunAsAdministrator = tool.RunAsAdministrator,
            RunHidden = tool.RunHidden
        }).ToList();
        List<SshGatewayDto> sshGateways = _pendingGateways.Select(CloneGateway).ToList();
        List<ProjectDto> projects = _pendingProjects.Select(CloneProject).ToList();
        HashSet<string> deletedProjectIds = _deletedProjectIds.ToHashSet(StringComparer.Ordinal);
        HashSet<string> deletedGatewayIds = new(_deletedGatewayIds, StringComparer.OrdinalIgnoreCase);

        // Clear inventory references first. If the following settings commit is interrupted,
        // the project or gateway still exists and can be reassigned; the inverse order leaves
        // dangling IDs.
        if (deletedProjectIds.Count > 0 || deletedGatewayIds.Count > 0)
        {
            await _configManager.MutateServersAsync(servers =>
            {
                int changedCount = 0;
                foreach (ServerProfileDto server in servers)
                {
                    if (server.ProjectId is not null && deletedProjectIds.Contains(server.ProjectId))
                    {
                        server.ProjectId = null;
                        changedCount++;
                    }

                    if (server.SshGatewayId is not null && deletedGatewayIds.Contains(server.SshGatewayId))
                    {
                        server.SshGatewayId = null;
                        changedCount++;
                    }
                }

                return changedCount;
            });
        }

        await _configManager.MergeSettingAsync((AppSettings settings) =>
        {
            if (deletedGatewayIds.Count > 0)
            {
                foreach (GroupDefaultsDto defaults in settings.GroupDefaults.Values)
                {
                    if (defaults.SshGatewayId is not null &&
                        deletedGatewayIds.Contains(defaults.SshGatewayId))
                    {
                        defaults.SshGatewayId = null;
                    }
                }
            }

            // General
            settings.DefaultLocale = DefaultLocale;
            settings.DefaultTheme = DefaultTheme;
            settings.AccentTint = AccentTint;
            settings.MaxEmbeddedSessions = MaxEmbeddedSessions;
            settings.PreventSleepDuringSession = PreventSleepDuringSession;
            settings.CollapseTunnelsPanelByDefault = CollapseTunnelsPanelByDefault;
            settings.ExternalEditorPath = ExternalEditorPath;
            settings.UpdateCheckEnabled = UpdateCheckEnabled;
            settings.UpdateCheckIntervalHours = UpdateCheckIntervalHours;

            // Terminal
            settings.TerminalFontFamily = TerminalFontFamily;
            settings.TerminalFontSize = TerminalFontSize;
            settings.TerminalColorScheme = TerminalColorScheme;
            settings.PowerShellExecutionPolicy = PowerShellExecutionPolicy;

            // SSH & SFTP
            settings.PlinkPath = PlinkPath;
            settings.PuttyPath = string.IsNullOrWhiteSpace(PuttyPath) ? null : PuttyPath;
            settings.SshDefaultMode = SshDefaultMode;
            settings.SshAgentPreference = parsedSshAgentPreference;
            settings.AntiIdleIntervalSeconds = AntiIdleInterval;
            settings.SshTmoutResetIntervalSeconds = SshTmoutResetInterval;
            settings.SshAutoReconnect = SshAutoReconnect;
            settings.SshAutoReconnectAttempts = SshAutoReconnectAttempts;
            settings.SftpBrowserEnabled = SftpBrowserEnabled;
            settings.SftpAutoOpenOnSsh = SftpAutoOpenOnSsh;
            settings.SftpFollowSshDirectory = SftpFollowSshDirectory;
            settings.X11ServerPath = string.IsNullOrWhiteSpace(X11ServerPath) ? null : X11ServerPath;
            settings.X11AutoStart = X11AutoStart;
            settings.SysinternalsPath = string.IsNullOrWhiteSpace(SysinternalsPath) ? null : SysinternalsPath;
            settings.NirSoftPath = string.IsNullOrWhiteSpace(NirSoftPath) ? null : NirSoftPath;
            settings.NanaRunPath = string.IsNullOrWhiteSpace(NanaRunPath) ? null : NanaRunPath;

            // Command Library Git Sync
            settings.CmdLibGitSyncEnabled = CmdLibGitSyncEnabled;
            settings.CmdLibGitSyncUrl = string.IsNullOrWhiteSpace(CmdLibGitSyncUrl) ? null : CmdLibGitSyncUrl;
            settings.CmdLibGitSyncBranch = CmdLibGitSyncBranch;
            settings.CmdLibGitSyncAuthorName = CmdLibGitSyncAuthorName;
            settings.CmdLibGitSyncAuthorEmail = CmdLibGitSyncAuthorEmail;
            settings.CmdLibGitSyncOnStartup = CmdLibGitSyncOnStartup;
            settings.CmdLibGitSyncAutoPush = CmdLibGitSyncAutoPush;

            // Session Health Monitor
            settings.SessionHealthMonitorEnabled = SessionHealthMonitorEnabled;
            settings.SessionHealthCheckIntervalSeconds = SessionHealthCheckIntervalSeconds;
            settings.SessionHealthProbeTimeoutMs = SessionHealthProbeTimeoutMs;
            settings.SessionHealthMaxConcurrent = SessionHealthMaxConcurrent;

            // RDP defaults
            settings.DefaultResolutionWidth = DefaultResolutionWidth;
            settings.DefaultResolutionHeight = DefaultResolutionHeight;
            settings.RdpDefaultMode = RdpDefaultMode;
            settings.RdpDefaultNla = RdpDefaultNla;
            settings.RdpDefaultStrictServerAuthentication = RdpDefaultStrictServerAuthentication;
            settings.RdpDefaultColorDepth = RdpDefaultColorDepth;
            settings.RdpDefaultDynamicResolution = RdpDefaultDynamicResolution;
            settings.RdpDefaultMultiMonitor = RdpDefaultMultiMonitor;
            settings.RdpDefaultRedirectClipboard = RdpDefaultRedirectClipboard;
            settings.RdpDefaultRedirectDrives = RdpDefaultRedirectDrives;
            settings.RdpDefaultRedirectPrinters = RdpDefaultRedirectPrinters;
            settings.RdpDefaultRedirectComPorts = RdpDefaultRedirectComPorts;
            settings.RdpDefaultRedirectSmartCards = RdpDefaultRedirectSmartCards;
            settings.RdpDefaultRedirectWebcam = RdpDefaultRedirectWebcam;
            settings.RdpDefaultRedirectUsb = RdpDefaultRedirectUsb;
            settings.RdpDefaultAudioCapture = RdpDefaultAudioCapture;
            settings.RdpDefaultAutoReconnect = RdpDefaultAutoReconnect;
            settings.RdpDefaultBitmapCaching = RdpDefaultBitmapCaching;
            settings.RdpDefaultCompression = RdpDefaultCompression;
            settings.RdpDefaultHardwareAcceleration = RdpDefaultHardwareAcceleration;
            settings.RdpDefaultAudioMode = RdpDefaultAudioMode;
            settings.RdpResolutionPresets = RdpResolutionPresets;
            settings.RdpDialogAdvancedDefault = RdpDialogAdvancedDefault;

            // Security
            settings.UseExternalCredentialProvider = UseExternalCredentialProvider;
            settings.CredentialProviderType = CredentialProviderType;
            settings.CredentialProviderCommand = CredentialProviderCommand;
            settings.CredentialProviderDatabase = CredentialProviderDatabase;
            settings.CredentialProviderKeyFile =
                string.IsNullOrWhiteSpace(CredentialProviderKeyFile)
                    ? null
                    : CredentialProviderKeyFile.Trim();
            settings.CredentialProviderUsernameCommand = CredentialProviderUsernameCommand;
            settings.CredentialProviderFirstLineOnly = CredentialProviderFirstLineOnly;
            settings.CredentialProviderUnlockSecretEncrypted =
                string.IsNullOrEmpty(CredentialProviderUnlockSecret)
                    ? null
                    : CredentialProtector.Protect(CredentialProviderUnlockSecret);
            settings.CredentialProviderTimeoutMs = CredentialProviderTimeoutMs;
            settings.RequireCredentialGuard = RequireCredentialGuard;
            settings.RequireWindowsHelloOnConnect = RequireWindowsHelloOnConnect;
            settings.WindowsHelloGraceMinutes = WindowsHelloGraceMinutes;
            settings.AutoLockIdleMinutes = AutoLockIdleMinutes;
            settings.DisconnectOnLock = DisconnectOnLock;

            // Advanced / Logging
            settings.EnableLogging = EnableLogging;
            settings.SessionLoggingEnabled = SessionLoggingEnabled;
            settings.SessionLogDirectory = SessionLogDirectory;
            settings.TunnelEstablishmentDelayMs = TunnelEstablishmentDelayMs;
            settings.RdpConnectWatchdogTimeoutMs = RdpConnectWatchdogTimeoutMs;
            settings.ExternalToolTimeoutMs = ExternalToolTimeoutMs;
            settings.RdpResizeEnableDelayMs = RdpResizeEnableDelayMs;
            settings.RdpArtifactCleanupDelayMs = RdpArtifactCleanupDelayMs;
            settings.RdpCredentialAutofillTimeoutMs = RdpCredentialAutofillTimeoutMs;
            settings.RdpAutoReconnectMaxAttempts = RdpAutoReconnectMaxAttempts;
            settings.RdpKeepAliveIntervalMs = RdpKeepAliveIntervalMs;

            // UI state
            settings.ShowToolsPanel = ShowToolsPanel;

            // Advanced / File sharing
            settings.FileShareEnableTftp = FileShareEnableTftp;
            settings.ExternalTools = externalTools;

            // Flush buffered gateways and projects. Gateways RECONCILE against what was
            // just read from disk instead of replacing it: the buffer is a snapshot taken
            // at LoadFromSettings, nothing reseeds it afterwards, and assigning it wholesale
            // erased every gateway another surface had persisted meanwhile.
            settings.SshGateways = ReconcileGateways(
                settings.SshGateways,
                sshGateways,
                deletedGatewayIds);
            settings.Projects = projects;
        });

        _deletedProjectIds.Clear();
        _deletedGatewayIds.Clear();

        _originalTheme = DefaultTheme;
        _originalAccentTint = AccentTint;

        if (!string.Equals(_localizer.CurrentLocale, DefaultLocale, StringComparison.OrdinalIgnoreCase))
        {
            await _localizer.SwitchLocaleAsync(DefaultLocale);
        }

        IsDirty = false;
        try
        {
            ConfigurationChanged?.Invoke();
        }
        catch (Exception ex)
        {
            FileLogger.Error("Settings change notification failed", ex);
        }
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync(CancellationToken cancellationToken)
    {
        bool confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["SettingsResetDefaultsConfirmTitle"],
            _localizer["SettingsResetDefaultsConfirmBody"],
            "warning");

        if (!confirmed) return;

        // Gateways and projects are inventory, not preferences. A gateway carries a
        // stored password and passphrase that the interface only ever reports as
        // booleans, so wiping one destroys a secret no user can read back, and the
        // servers this reset leaves alone hold references to both. Carry them across
        // the reload, together with the deletions still pending: LoadFromSettings
        // clears those sets, and losing them would orphan the references that the
        // save path cleans up.
        List<SshGatewayDto> keptGateways = _pendingGateways.Select(CloneGateway).ToList();
        List<ProjectDto> keptProjects = _pendingProjects.Select(CloneProject).ToList();
        List<string> keptDeletedProjectIds = _deletedProjectIds.ToList();
        List<string> keptDeletedGatewayIds = _deletedGatewayIds.ToList();

        var defaults = await LoadFactoryDefaultsAsync(cancellationToken);
        defaults.SshGateways = keptGateways;
        defaults.Projects = keptProjects;

        LoadFromSettings(defaults);

        _deletedProjectIds.AddRange(keptDeletedProjectIds);
        foreach (string gatewayId in keptDeletedGatewayIds)
        {
            _deletedGatewayIds.Add(gatewayId);
        }

        IsDirty = true;
    }

    private bool CanReofferLegacyMigrationNextStartup() =>
        LegacyMigrationReofferAvailable;

    [RelayCommand(CanExecute = nameof(CanReofferLegacyMigrationNextStartup))]
    private async Task ReofferLegacyMigrationNextStartupAsync()
    {
        try
        {
            await LegacyMigrationDecisionPolicy.ClearDeclineAsync(_configManager);
        }
        catch (Exception ex)
        {
            FileLogger.Error("Failed to clear the declined legacy migration offer.", ex);
            _dialogService.ShowError(
                _localizer["SettingsSectionLegacyMigration"],
                _localizer["SettingsLegacyMigrationReofferFailed"]);
            return;
        }

        LegacyMigrationReofferAvailable = false;
        _dialogService.ShowInfo(
            _localizer["SettingsSectionLegacyMigration"],
            _localizer["SettingsLegacyMigrationReofferScheduled"]);
    }

    [RelayCommand]
    private async Task ResetRdpDefaultsAsync(CancellationToken cancellationToken)
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["SettingsResetRdpDefaultsConfirmTitle"],
            _localizer["SettingsResetRdpDefaultsConfirmBody"],
            "warning");

        if (!confirmed) return;

        var defaults = await LoadFactoryDefaultsAsync(cancellationToken);
        ApplyRdpDefaults(defaults);
        IsDirty = true;
    }

    private static async Task<AppSettings> LoadFactoryDefaultsAsync(CancellationToken cancellationToken)
    {
        // Load factory defaults from settings.default.json (preserves bundled external tools)
        // rather than new AppSettings() which has empty defaults for collections.
        var defaultsPath = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            AppConstants.BundledConfigDirectoryName,
            "settings.default.json");

        if (System.IO.File.Exists(defaultsPath))
        {
            var json = await System.IO.File.ReadAllTextAsync(defaultsPath, cancellationToken);
            return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, ImportJsonOptions)
                   ?? new AppSettings();
        }

        return new AppSettings();
    }

    private void ApplyRdpDefaults(AppSettings defaults)
    {
        DefaultResolutionWidth = defaults.DefaultResolutionWidth;
        DefaultResolutionHeight = defaults.DefaultResolutionHeight;
        RdpDefaultMode = defaults.RdpDefaultMode;
        RdpDefaultNla = defaults.RdpDefaultNla;
        RdpDefaultStrictServerAuthentication = defaults.RdpDefaultStrictServerAuthentication;
        RdpDefaultColorDepth = defaults.RdpDefaultColorDepth;
        RdpDefaultDynamicResolution = defaults.RdpDefaultDynamicResolution;
        RdpDefaultMultiMonitor = defaults.RdpDefaultMultiMonitor;
        RdpDefaultRedirectClipboard = defaults.RdpDefaultRedirectClipboard;
        RdpDefaultRedirectDrives = defaults.RdpDefaultRedirectDrives;
        RdpDefaultRedirectPrinters = defaults.RdpDefaultRedirectPrinters;
        RdpDefaultRedirectComPorts = defaults.RdpDefaultRedirectComPorts;
        RdpDefaultRedirectSmartCards = defaults.RdpDefaultRedirectSmartCards;
        RdpDefaultRedirectWebcam = defaults.RdpDefaultRedirectWebcam;
        RdpDefaultRedirectUsb = defaults.RdpDefaultRedirectUsb;
        RdpDefaultAudioCapture = defaults.RdpDefaultAudioCapture;
        RdpDefaultAutoReconnect = defaults.RdpDefaultAutoReconnect;
        RdpDefaultBitmapCaching = defaults.RdpDefaultBitmapCaching;
        RdpDefaultCompression = defaults.RdpDefaultCompression;
        RdpDefaultHardwareAcceleration = defaults.RdpDefaultHardwareAcceleration;
        RdpDefaultAudioMode = defaults.RdpDefaultAudioMode;
        RdpResizeEnableDelayMs = defaults.RdpResizeEnableDelayMs;
        RdpArtifactCleanupDelayMs = defaults.RdpArtifactCleanupDelayMs;
        RdpCredentialAutofillTimeoutMs = defaults.RdpCredentialAutofillTimeoutMs;
        RdpAutoReconnectMaxAttempts = defaults.RdpAutoReconnectMaxAttempts;
        RdpKeepAliveIntervalMs = defaults.RdpKeepAliveIntervalMs;
        RdpDialogAdvancedDefault = defaults.RdpDialogAdvancedDefault;
        RdpResolutionPresets = defaults.RdpResolutionPresets;
        RdpConnectWatchdogTimeoutMs = defaults.RdpConnectWatchdogTimeoutMs;
    }

    [RelayCommand]
    private async Task ExportConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = _localizer["ExportDialogTitle"],
                Filter = _localizer["ExportDialogFilter"],
                DefaultExt = ".json",
                FileName = "servers.json"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var servers = await _configManager.LoadServersAsync();
            var settings = await _configManager.LoadSettingsAsync();
            var exportDocument = BuildExportConfigDocument(servers, settings);
            var json = JsonSerializer.Serialize(exportDocument, ExportJsonOptions);
            await File.WriteAllTextAsync(dialog.FileName, json, new System.Text.UTF8Encoding(false), cancellationToken);

            var count = servers.Count;
            FileLogger.Info($"Exported {count} server(s) to {dialog.FileName}");
            string message = _localizer.Format("StatusExportSuccess", count)
                + "\n\n" + _localizer["StatusExportCredentialsExcluded"];
            _dialogService.ShowInfo(
                _localizer["ExportDialogTitle"],
                message);
        }
        catch (Exception ex)
        {
            FileLogger.Error("Export failed", ex);
            _dialogService.ShowError(
                _localizer["ExportDialogTitle"],
                _localizer.Format("StatusExportFailed", ex.Message));
        }
    }

    internal static ProfileConfigDocument BuildExportConfigDocument(
        IReadOnlyList<ServerProfileDto> servers,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(settings);

        return new ProfileConfigDocument
        {
            SchemaVersion = ProfileConfigDocument.CurrentSchemaVersion,
            Servers = servers.ToList(),
            Gateways = settings.SshGateways.ToList()
        };
    }

    private static void RemoveJsonProperties(JsonTypeInfo typeInfo, IReadOnlyCollection<string> propertyNames)
    {
        for (int index = typeInfo.Properties.Count - 1; index >= 0; index--)
        {
            JsonPropertyInfo property = typeInfo.Properties[index];
            if (propertyNames.Contains(property.Name))
            {
                typeInfo.Properties.RemoveAt(index);
            }
        }
    }

    [RelayCommand]
    private async Task ImportConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filePath = PickImportFilePath();
            if (filePath is null)
            {
                return;
            }

            IsBusy = true;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is ".rdp" or ".json")
            {
                var importService = GetProfileImportService();
                var result = await importService.ImportFromPathAsync(filePath, cancellationToken);
                if (result.IsFailure)
                {
                    _dialogService.ShowError(
                        _localizer["ImportDialogTitle"],
                        result.ErrorMessage ?? _localizer["StatusImportFailed"]);
                    return;
                }

                if (result.HasChanges)
                {
                    ConfigurationChanged?.Invoke();
                }

                return;
            }

            if (ext is not ".mxtsessions" and not ".ini" and not ".mobaconf" and not ".rdg" and not ".xml")
            {
                var importService = GetProfileImportService();
                var result = await importService.ImportFromPathAsync(filePath, cancellationToken);
                if (result.IsFailure)
                {
                    _dialogService.ShowError(
                        _localizer["ImportDialogTitle"],
                        result.ErrorMessage ?? _localizer["StatusImportFailed"]);
                    return;
                }

                if (result.HasChanges)
                {
                    ConfigurationChanged?.Invoke();
                }

                return;
            }

            _mobaStoredCredentialCount = 0;
            var (imported, importWarnings) = ext switch
            {
                ".mxtsessions" or ".ini" or ".mobaconf" => await ImportMobaXtermAsync(filePath, cancellationToken),
                ".rdg" => await ImportRdcManAsync(filePath, cancellationToken),
                ".xml" => await ImportXmlAsync(filePath, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported import extension reached legacy parser: {ext}")
            };

            if (imported.Count == 0)
            {
                _dialogService.ShowInfo(
                    _localizer["ImportDialogTitle"],
                    _localizer["ImportNoSessionsFound"]);
                return;
            }

            ImportedProfileSanitizer.Sanitize(imported);

            (List<ServerProfileDto> validImported, List<string> validationFailures) =
                ImportedProfileValidator.FilterValid(imported, ProfileImportService.SupportedConnectionTypes);
            imported = validImported;
            if (validationFailures.Count > 0)
            {
                importWarnings ??= new List<string>();
                importWarnings.AddRange(validationFailures);
            }

            if (imported.Count == 0)
            {
                if (validationFailures.Count > 0)
                {
                    string warningText = string.Join("\n", validationFailures.Take(10));
                    string warningMessage = _localizer.Format("ImportMobaXtermWarnings", validationFailures.Count)
                        + "\n" + warningText;
                    _dialogService.ShowWarning(_localizer["ImportDialogTitle"], warningMessage);
                }
                else
                {
                    _dialogService.ShowInfo(
                        _localizer["ImportDialogTitle"],
                        _localizer["ImportNoSessionsFound"]);
                }

                return;
            }

            var confirmMessage = ext is ".mxtsessions" or ".ini" or ".mobaconf"
                ? _localizer.Format("ConfirmImportMobaXtermMessage", imported.Count)
                : _localizer.Format("ConfirmImportMessage", imported.Count);

            var confirmed = await _dialogService.ShowConfirmAsync(
                _localizer["ConfirmImportTitle"],
                confirmMessage);

            if (!confirmed)
            {
                return;
            }

            var importResult = await _configManager.MutateServersAsync(existing =>
            {
                int newCount = 0;
                int updatedCount = 0;
                foreach (ServerProfileDto server in imported)
                {
                    if (string.IsNullOrEmpty(server.Id))
                    {
                        server.Id = Guid.NewGuid().ToString();
                    }

                    int existingIndex = existing.FindIndex(
                        candidate => string.Equals(
                            candidate.Id,
                            server.Id,
                            StringComparison.OrdinalIgnoreCase));

                    if (existingIndex >= 0)
                    {
                        existing[existingIndex] = server;
                        updatedCount++;
                    }
                    else
                    {
                        existing.Add(server);
                        newCount++;
                    }
                }

                return (NewCount: newCount, UpdatedCount: updatedCount);
            });

            int totalImported = importResult.NewCount + importResult.UpdatedCount;
            FileLogger.Info(
                $"Imported {totalImported} server(s) from {filePath} ({importResult.NewCount} new, {importResult.UpdatedCount} updated)");

            var statusMessage = _localizer.Format(
                "StatusImportBreakdown",
                totalImported,
                importResult.NewCount,
                importResult.UpdatedCount);

            if (importWarnings is { Count: > 0 })
            {
                var warningText = string.Join("\n", importWarnings.Take(10));
                statusMessage += "\n\n" + _localizer.Format("ImportMobaXtermWarnings", importWarnings.Count)
                    + "\n" + warningText;
            }

            if (ext is ".mxtsessions" or ".ini" or ".mobaconf")
            {
                string passwordNotice = _mobaStoredCredentialCount > 0
                    ? _localizer.Format("ImportMobaXtermPasswordNoticeDetected", _mobaStoredCredentialCount)
                    : _localizer["ImportMobaXtermPasswordNotice"];
                statusMessage += "\n\n" + passwordNotice;
                _dialogService.ShowWarning(_localizer["ImportDialogTitle"], statusMessage);
            }
            else
            {
                _dialogService.ShowInfo(_localizer["ImportDialogTitle"], statusMessage);
            }

            ConfigurationChanged?.Invoke();
        }
        catch (JsonException ex)
        {
            FileLogger.Error("Import failed: invalid JSON", ex);
            _dialogService.ShowError(
                _localizer["ImportDialogTitle"],
                _localizer.Format("StatusImportFailed", ex.Message));
        }
        catch (Exception ex)
        {
            FileLogger.Error("Import failed", ex);
            _dialogService.ShowError(
                _localizer["ImportDialogTitle"],
                _localizer.Format("StatusImportFailed", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string? PickImportFilePath()
    {
        if (ImportFilePathProvider is not null)
        {
            return ImportFilePathProvider();
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = _localizer["ImportDialogTitle"],
            Filter = _localizer["ImportDialogFilterAll"]
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private IProfileImportService GetProfileImportService() =>
        _profileImportService ?? new ProfileImportService(
            _configManager,
            _localizer,
            _dialogService,
            new RdpImportService(_configManager, _localizer));

    private async Task<(List<ServerProfileDto> Servers, List<string>? Warnings)> ImportMobaXtermAsync(
        string filePath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var content = HasUtf8Bom(bytes)
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : System.Text.Encoding.GetEncoding(1252).GetString(bytes);
        var mobaResult = MobaXtermImporter.Parse(content);
        _mobaStoredCredentialCount = mobaResult.StoredCredentialCount;
        return (mobaResult.Servers, mobaResult.Warnings);
    }

    private async Task<(List<ServerProfileDto> Servers, List<string>? Warnings)> ImportRdcManAsync(
        string filePath, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var rdcResult = RdcManImporter.Parse(content);
        return (rdcResult.Servers, rdcResult.Warnings);
    }

    private async Task<(List<ServerProfileDto> Servers, List<string>? Warnings)> ImportXmlAsync(
        string filePath, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        if (content.Contains("<Connections", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Type=\"Connection\"", StringComparison.OrdinalIgnoreCase))
        {
            var mrnResult = MRemoteNgImporter.Parse(content);
            return (mrnResult.Servers, mrnResult.Warnings);
        }

        var rdcResult = RdcManImporter.Parse(content);
        return (rdcResult.Servers, rdcResult.Warnings);
    }

    [RelayCommand]
    private async Task ImportCitrixAppsAsync(CancellationToken cancellationToken)
    {
        try
        {
            CitrixScanResult scanResult = CitrixScanProvider?.Invoke() ?? CitrixCacheScanner.Scan();

            if (scanResult.Resources.Count == 0)
            {
                var warningMsg = scanResult.Warnings.Count > 0
                    ? string.Join("\n", scanResult.Warnings)
                    : _localizer["CitrixNoAppsFound"];
                _dialogService.ShowInfo(_localizer["CitrixScanTitle"], warningMsg);
                return;
            }

            var confirmed = await _dialogService.ShowConfirmAsync(
                _localizer["CitrixScanTitle"],
                _localizer.Format("CitrixScanConfirm", scanResult.Resources.Count));

            if (!confirmed) return;

            List<ServerProfileDto> imported = CitrixCacheScanner.ToServerProfiles(scanResult.Resources);
            if (!TryProtectCitrixImportsForCurrentVaultState(imported))
            {
                return;
            }

            int newCount = await _configManager.MutateServersAsync(existing =>
            {
                existing.AddRange(imported);
                return imported.Count;
            });

            var statusMsg = _localizer.Format("CitrixScanSuccess", newCount);
            if (scanResult.Warnings.Count > 0)
            {
                statusMsg += "\n\n" + string.Join("\n", scanResult.Warnings.Take(5));
            }

            _dialogService.ShowInfo(_localizer["CitrixScanTitle"], statusMsg);
            FileLogger.Info($"Imported {newCount} Citrix app(s) from local cache");
            ConfigurationChanged?.Invoke();
        }
        catch (VaultLockedException)
        {
            FileLogger.Warn("Citrix cache import refused because the vault locked before persistence");
            ShowCitrixImportVaultLocked();
        }
        catch (Exception ex)
        {
            FileLogger.Error("Citrix scan failed", ex);
            _dialogService.ShowError(
                _localizer["CitrixScanTitle"],
                _localizer.Format("StatusImportFailed", ex.Message));
        }
    }

    private bool TryProtectCitrixImportsForCurrentVaultState(List<ServerProfileDto> imported)
    {
        bool vaultEnabled = CredentialProtector.IsVaultEnabled;
        if (!vaultEnabled)
        {
            return true;
        }

        bool vaultUnlocked = CredentialProtector.IsVaultUnlocked;
        if (!vaultUnlocked)
        {
            ShowCitrixImportVaultLocked();
            return false;
        }

        foreach (ServerProfileDto profile in imported)
        {
            string? launchToken = profile.CitrixLaunchCommandLine;
            if (!string.IsNullOrEmpty(launchToken))
            {
                profile.CitrixLaunchCommandLine = CredentialProtector.Protect(launchToken);
            }
        }

        return true;
    }

    private void ShowCitrixImportVaultLocked()
    {
        _dialogService.ShowInfo(
            _localizer["CitrixScanTitle"],
            _localizer["CitrixImportVaultLocked"]);
    }

    [RelayCommand]
    private async Task AddGatewayAsync(CancellationToken cancellationToken)
    {
        var vm = new GatewayDialogViewModel();
        vm.AvailableParents = new ObservableCollection<GatewayOption>(
            _pendingGateways.Select(g => new GatewayOption(g.Id, $"{g.Name} ({g.Host})")));

        var result = await _dialogService.ShowGatewayDialogAsync(vm);
        if (result?.Saved == true)
        {
            result.Gateway.Id = Guid.NewGuid().ToString();
            _pendingGateways.Add(result.Gateway);
            Gateways.Add(CreateGatewayItem(result.Gateway));

            IsDirty = true;
        }
    }

    /// <summary>
    /// Adds a gateway from outside the settings panel and persists it immediately.
    /// </summary>
    /// <remarks>
    /// The panel's own Add buffers into the pending list because the panel owns a Save
    /// button. The Add menu and the tree context menu own no such button: buffering there
    /// produced a gateway that no session could select and that the next configuration
    /// reload discarded without a word. This path writes through
    /// <see cref="IConfigManager.MergeSettingAsync"/> - reload from disk, mutate, atomic
    /// write - so the server dialog, which reads settings afresh every time it opens,
    /// offers the gateway at once.
    /// </remarks>
    [RelayCommand]
    private async Task AddGatewayOutsidePanelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _gatewayCreation ??= new GatewayCreationService(_configManager, _dialogService);
        SshGatewayDto? created = await _gatewayCreation.CreateAsync();
        if (created is null)
        {
            return;
        }

        // The panel keeps its own buffer. Seeding it here keeps an open panel showing what
        // disk holds, and deliberately does NOT raise IsDirty: nothing is pending, the
        // write already happened.
        _pendingGateways.Add(CloneGateway(created));
        Gateways.Add(CreateGatewayItem(created));
    }

    /// <summary>
    /// Takes in gateways that appeared on disk while this panel was open, without disturbing
    /// anything the user has edited here but not yet saved.
    /// </summary>
    /// <remarks>
    /// The panel seeds its buffer once, at <c>LoadFromSettings</c>, which runs at startup, on a
    /// full configuration reload, on Discard and on Reset - and on none of those does simply
    /// walking to the Settings tab count. So a gateway created from a session dialog reached the
    /// disk and the session tree correctly and was still absent from this list, which read
    /// "no gateway configured" over a file that had one. Reported from a live session on
    /// 2026-08-25, right after the missing-badge defect it looks like but is not.
    ///
    /// Reseeding wholesale would be the obvious fix and it would be wrong: this panel buffers on
    /// purpose, because its Save button is the contract and Cancel must still discard. Throwing
    /// the buffer away on an unrelated external write would silently destroy edits in progress -
    /// a worse defect than the one being fixed. So this only ADDS ids the buffer has never seen,
    /// never touches an entry already held, and skips anything staged for deletion.
    ///
    /// It deliberately leaves <see cref="IsDirty"/> alone: absorbing someone else's write is not
    /// a user edit, and arming Save here would invite the user to write the buffer back.
    /// </remarks>
    internal void AbsorbExternallyCreatedGateways(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        foreach (SshGatewayDto gateway in settings.SshGateways)
        {
            if (string.IsNullOrWhiteSpace(gateway.Id)
                || _deletedGatewayIds.Contains(gateway.Id)
                || _pendingGateways.Any(pending => string.Equals(
                    pending.Id, gateway.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _pendingGateways.Add(CloneGateway(gateway));
            Gateways.Add(CreateGatewayItem(gateway));
        }
    }

    private static GatewayItemViewModel CreateGatewayItem(SshGatewayDto gateway) =>
        new()
        {
            Id = gateway.Id,
            Name = gateway.Name,
            Host = gateway.Host,
            Port = gateway.Port,
            User = gateway.User,
            HasKey = !string.IsNullOrEmpty(gateway.KeyPath),
            HasPassword = !string.IsNullOrEmpty(gateway.SshPasswordEncrypted),
            ParentGatewayId = gateway.ParentGatewayId
        };

    private bool CanEditGateway() => SelectedGateway is not null;

    [RelayCommand(CanExecute = nameof(CanEditGateway))]
    private async Task EditGatewayAsync(CancellationToken cancellationToken)
    {
        var gateway = SelectedGateway!;
        var gwDto = _pendingGateways.FirstOrDefault(g => g.Id == gateway.Id);
        if (gwDto == null) return;

        var vm = GatewayDialogViewModel.FromDto(gwDto);
        vm.AvailableParents = new ObservableCollection<GatewayOption>(
            _pendingGateways
                .Where(g => g.Id != gwDto.Id)
                .Select(g => new GatewayOption(g.Id, $"{g.Name} ({g.Host})")));

        var result = await _dialogService.ShowGatewayDialogAsync(vm);
        if (result?.Saved == true)
        {
            var idx = _pendingGateways.FindIndex(g => g.Id == gwDto.Id);
            if (idx >= 0)
            {
                result.Gateway.Id = gwDto.Id;
                _pendingGateways[idx] = result.Gateway;

                gateway.Name = result.Gateway.Name;
                gateway.Host = result.Gateway.Host;
                gateway.Port = result.Gateway.Port;
                gateway.User = result.Gateway.User;
                gateway.HasKey = !string.IsNullOrEmpty(result.Gateway.KeyPath);
                gateway.HasPassword = !string.IsNullOrEmpty(result.Gateway.SshPasswordEncrypted);
            }

            IsDirty = true;
        }
    }

    private bool CanDeleteGateway() => SelectedGateway is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteGateway))]
    private async Task DeleteGatewayAsync(CancellationToken cancellationToken)
    {
        var gateway = SelectedGateway!;
        SshGatewayDto? gatewayDto = _pendingGateways.LastOrDefault(candidate =>
            string.Equals(candidate.Id, gateway.Id, StringComparison.OrdinalIgnoreCase));
        if (gatewayDto is null)
        {
            return;
        }

        AppSettings persistedSettings = await _configManager.LoadSettingsAsync();
        cancellationToken.ThrowIfCancellationRequested();
        List<ServerProfileDto> servers = await _configManager.LoadServersAsync();
        cancellationToken.ThrowIfCancellationRequested();
        GatewayReferenceImpact impact = GatewayReferenceAnalyzer.AnalyzeDeletion(
            gatewayDto.Id,
            _pendingGateways,
            servers,
            persistedSettings.GroupDefaults);
        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["ConfirmDeleteGatewayTitle"],
            _localizer.Format(
                "ConfirmDeleteGatewayImpactMessage",
                gateway.Name,
                impact.ServerCount,
                impact.GroupDefaultCount,
                impact.ChildGatewayCount),
            "danger");

        if (!confirmed) return;

        foreach (SshGatewayDto child in _pendingGateways.Where(candidate =>
            string.Equals(
                candidate.ParentGatewayId,
                impact.GatewayId,
                StringComparison.OrdinalIgnoreCase)))
        {
            child.ParentGatewayId = null;
        }

        foreach (GatewayItemViewModel child in Gateways.Where(candidate =>
            string.Equals(
                candidate.ParentGatewayId,
                impact.GatewayId,
                StringComparison.OrdinalIgnoreCase)))
        {
            child.ParentGatewayId = null;
        }

        _pendingGateways.RemoveAll(candidate => string.Equals(
            candidate.Id,
            impact.GatewayId,
            StringComparison.OrdinalIgnoreCase));
        _deletedGatewayIds.Add(impact.GatewayId);
        Gateways.Remove(gateway);
        SelectedGateway = null;
        IsDirty = true;
    }

    [RelayCommand]
    private async Task ShowGatewayOverviewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            AppSettings persistedSettings = await _configManager.LoadSettingsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            List<ServerProfileDto> servers = await _configManager.LoadServersAsync();
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<SshGatewayDto> persistedGateways = persistedSettings.SshGateways;
            GatewayOverview overview = GatewayOverviewBuilder.Build(
                persistedGateways,
                servers,
                persistedSettings.GroupDefaults);
            string? warningMessage = HaveSameGatewayIds(persistedGateways, _pendingGateways)
                ? null
                : _localizer["GatewayOverviewUnsavedGatewayChangesWarning"];
            var viewModel = new GatewayOverviewDialogViewModel(
                overview,
                _localizer,
                BuildGatewayOverviewOptions(persistedGateways),
                GatewayReferenceMutationHandler,
                ReloadGatewayOverviewAsync,
                warningMessage);
            await _dialogService.ShowGatewayOverviewAsync(viewModel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.Error("Gateway overview failed", ex);
            _dialogService.ShowError(
                _localizer["GatewayOverviewTitle"],
                _localizer.Format("GatewayOverviewLoadFailed", ex.Message));
        }

        async Task<GatewayOverview> ReloadGatewayOverviewAsync(CancellationToken reloadCancellationToken)
        {
            AppSettings refreshedSettings = await _configManager.LoadSettingsAsync();
            reloadCancellationToken.ThrowIfCancellationRequested();
            List<ServerProfileDto> refreshedServers = await _configManager.LoadServersAsync();
            reloadCancellationToken.ThrowIfCancellationRequested();
            return GatewayOverviewBuilder.Build(
                refreshedSettings.SshGateways,
                refreshedServers,
                refreshedSettings.GroupDefaults);
        }
    }

    /// <summary>
    /// Merges the panel's gateway buffer into the list just read from disk.
    /// </summary>
    /// <remarks>
    /// Entries the panel knows about are replaced by its own version: it carries the edit
    /// the user just typed. Entries it never saw - persisted by the Add menu, the tree
    /// context menu, or a profile import while the panel was open - are preserved, and
    /// entries the panel deleted are dropped wherever they appear. Deleting a gateway also
    /// clears the parent reference of anything that pointed at it, on the reconciled list
    /// rather than on the buffer, so a gateway added elsewhere cannot keep a dangling
    /// parent.
    /// </remarks>
    internal static List<SshGatewayDto> ReconcileGateways(
        IEnumerable<SshGatewayDto> persisted,
        IEnumerable<SshGatewayDto> pending,
        IReadOnlySet<string> deletedGatewayIds)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(deletedGatewayIds);

        List<SshGatewayDto> pendingList = pending.ToList();
        Dictionary<string, SshGatewayDto> pendingById = pendingList
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway.Id))
            .GroupBy(gateway => gateway.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        List<SshGatewayDto> reconciled = [];
        HashSet<string> takenFromPending = new(StringComparer.OrdinalIgnoreCase);

        foreach (SshGatewayDto stored in persisted)
        {
            if (string.IsNullOrWhiteSpace(stored.Id))
            {
                reconciled.Add(stored);
                continue;
            }

            if (deletedGatewayIds.Contains(stored.Id))
            {
                continue;
            }

            if (pendingById.TryGetValue(stored.Id, out SshGatewayDto? edited))
            {
                reconciled.Add(edited);
                takenFromPending.Add(stored.Id);
                continue;
            }

            reconciled.Add(stored);
        }

        foreach (SshGatewayDto buffered in pendingList)
        {
            if (!string.IsNullOrWhiteSpace(buffered.Id)
                && (takenFromPending.Contains(buffered.Id) || deletedGatewayIds.Contains(buffered.Id)))
            {
                continue;
            }

            reconciled.Add(buffered);
        }

        foreach (SshGatewayDto gateway in reconciled)
        {
            if (gateway.ParentGatewayId is not null
                && deletedGatewayIds.Contains(gateway.ParentGatewayId))
            {
                gateway.ParentGatewayId = null;
            }
        }

        return reconciled;
    }

    internal static bool HaveSameGatewayIds(
        IEnumerable<SshGatewayDto> first,
        IEnumerable<SshGatewayDto> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        string[] firstIds = NormalizeGatewayIds(first);
        string[] secondIds = NormalizeGatewayIds(second);
        return firstIds.SequenceEqual(secondIds, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] NormalizeGatewayIds(IEnumerable<SshGatewayDto> gateways)
    {
        return gateways
            .Select(gateway => gateway.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<GatewayOption> BuildGatewayOverviewOptions(IEnumerable<SshGatewayDto> gateways)
    {
        return gateways
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway.Id))
            .Select(gateway => new GatewayOption(
                gateway.Id,
                FormatGatewayOverviewOption(gateway),
                gateway.Name,
                gateway.Host,
                gateway.Port))
            .ToList();
    }

    private static string FormatGatewayOverviewOption(SshGatewayDto gateway)
    {
        string name = string.IsNullOrWhiteSpace(gateway.Name) ? gateway.Id : gateway.Name;
        string endpoint = gateway.Port > 0 ? $"{gateway.Host}:{gateway.Port}" : gateway.Host;
        return string.IsNullOrWhiteSpace(endpoint) ? name : $"{name} ({endpoint})";
    }

    [RelayCommand]
    private async Task AddProjectAsync(CancellationToken cancellationToken)
    {
        var vm = new ProjectDialogViewModel
        {
            DialogTitle = _localizer["ProjectDialogTitleAdd"]
        };

        var result = await _dialogService.ShowProjectDialogAsync(vm);
        if (result is not { Saved: true }) return;

        result.Project.Id = Guid.NewGuid().ToString();
        _pendingProjects.Add(result.Project);

        Projects.Add(new ProjectItemViewModel
        {
            Id = result.Project.Id,
            Name = result.Project.Name,
            Color = result.Project.Color ?? "#3B82F6",
            Description = result.Project.Description ?? ""
        });

        IsDirty = true;
    }

    private bool CanEditProject() => SelectedProject is not null;

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task EditProjectAsync(CancellationToken cancellationToken)
    {
        var project = SelectedProject!;
        var projectDto = _pendingProjects.FirstOrDefault(p => p.Id == project.Id);
        if (projectDto is null) return;

        var vm = ProjectDialogViewModel.FromDto(projectDto);
        vm.DialogTitle = _localizer["ProjectDialogTitleEdit"];

        var result = await _dialogService.ShowProjectDialogAsync(vm);
        if (result is not { Saved: true }) return;

        var idx = _pendingProjects.FindIndex(p => p.Id == projectDto.Id);
        if (idx >= 0)
        {
            result.Project.Id = projectDto.Id;
            _pendingProjects[idx] = result.Project;

            project.Name = result.Project.Name;
            project.Color = result.Project.Color ?? "#3B82F6";
            project.Description = result.Project.Description ?? "";
        }

        IsDirty = true;
    }

    private bool CanDeleteProject() => SelectedProject is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteProject))]
    private async Task DeleteProjectAsync(CancellationToken cancellationToken)
    {
        var project = SelectedProject!;

        // Check server usage for the confirmation message
        var servers = await _configManager.LoadServersAsync();
        var usageCount = servers.Count(s =>
            string.Equals(s.ProjectId, project.Id, StringComparison.Ordinal));

        var message = usageCount > 0
            ? _localizer.Format("ConfirmDeleteProjectInUse", usageCount)
                + "\n" + _localizer.Format("ConfirmDeleteProjectMessage", project.Name)
            : _localizer.Format("ConfirmDeleteProjectMessage", project.Name);

        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["ConfirmDeleteProjectTitle"],
            message,
            "danger");

        if (!confirmed) return;

        _pendingProjects.RemoveAll(p => p.Id == project.Id);
        _deletedProjectIds.Add(project.Id);

        Projects.Remove(project);
        SelectedProject = null;
        IsDirty = true;
    }

    [RelayCommand]
    private Task AddExternalToolAsync(CancellationToken cancellationToken)
    {
        var newTool = new ExternalToolItemViewModel
        {
            Name = _localizer["ExternalToolDefaultName"],
            Arguments = "{Host}"
        };

        ExternalTools.Add(newTool);
        SelectedExternalTool = newTool;
        IsDirty = true;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RemoveExternalToolAsync(CancellationToken cancellationToken)
    {
        if (SelectedExternalTool is null) return Task.CompletedTask;

        ExternalTools.Remove(SelectedExternalTool);
        SelectedExternalTool = null;
        IsDirty = true;
        return Task.CompletedTask;
    }

    [ObservableProperty]
    private string? _credentialProviderTestResult;

    [RelayCommand]
    private async Task ConfigurePinAsync()
    {
        AppSettings current = await _configManager.LoadSettingsAsync();
        PinSetupDialogViewModel dialogViewModel =
            new PinSetupDialogViewModel(_pinManager, current.PinHash, current.PinSalt);
        PinSetupResult? result = await _dialogService.ShowPinSetupDialogAsync(dialogViewModel);
        if (result is null)
        {
            return;
        }

        if (result.Outcome == PinSetupOutcome.Set)
        {
            await _configManager.MergeSettingAsync((AppSettings settings) =>
            {
                settings.PinHash = result.Hash;
                settings.PinSalt = result.Salt;
                settings.PinFailureCount = 0;
                settings.PinLockoutUntilUtc = null;
            });

            IsPinConfigured = true;
            FileLogger.Info("PIN configured: set.");
            return;
        }

        await _configManager.MergeSettingAsync((AppSettings settings) =>
        {
            settings.PinHash = null;
            settings.PinSalt = null;
            settings.PinFailureCount = 0;
            settings.PinLockoutUntilUtc = null;
        });

        IsPinConfigured = false;
        FileLogger.Info("PIN configured: removed.");
    }

    [RelayCommand]
    private async Task EnableVaultAsync()
    {
        VaultEnableDialogViewModel dialogViewModel = new VaultEnableDialogViewModel(
            password => _vaultLifecycle.EnableAsync(password, Argon2idParameters.Recommended),
            _localizer);
        await _dialogService.ShowVaultEnableDialogAsync(dialogViewModel);
        await RefreshVaultStatusAsync();
    }

    [RelayCommand]
    private async Task ChangeMasterPasswordAsync()
    {
        VaultChangePasswordDialogViewModel dialogViewModel = new VaultChangePasswordDialogViewModel(
            (current, next) => _vaultLifecycle.ChangeMasterPasswordAsync(current, next, Argon2idParameters.Recommended),
            _localizer);
        await _dialogService.ShowVaultChangePasswordDialogAsync(dialogViewModel);
        await RefreshVaultStatusAsync();
    }

    [RelayCommand]
    private async Task DisableVaultAsync()
    {
        VaultDisableDialogViewModel dialogViewModel = new VaultDisableDialogViewModel(
            password => _vaultLifecycle.DisableAsync(password),
            _localizer);
        await _dialogService.ShowVaultDisableDialogAsync(dialogViewModel);
        await RefreshVaultStatusAsync();
    }

    private bool CanRunEnableVaultHello() => CanEnableVaultHello;

    [RelayCommand(CanExecute = nameof(CanRunEnableVaultHello))]
    private async Task EnableVaultHelloAsync(CancellationToken cancellationToken)
    {
        IsVaultHelloBusy = true;
        VaultHelloStatusText = _localizer["SettingsVaultHelloStatusEnrolling"];
        try
        {
            await _vaultLifecycle.EnrollHelloAsync(cancellationToken);
        }
        catch (VaultHelloException)
        {
            VaultHelloStatusText = _localizer["SettingsVaultHelloStatusUnavailable"];
            return;
        }
        catch (InvalidOperationException)
        {
            VaultHelloStatusText = _localizer["SettingsVaultHelloStatusUnlockRequired"];
            return;
        }
        finally
        {
            IsVaultHelloBusy = false;
        }

        await RefreshVaultStatusAsync();
    }

    private bool CanRunDisableVaultHello() => CanDisableVaultHello;

    [RelayCommand(CanExecute = nameof(CanRunDisableVaultHello))]
    private async Task DisableVaultHelloAsync(CancellationToken cancellationToken)
    {
        IsVaultHelloBusy = true;
        VaultHelloStatusText = _localizer["SettingsVaultHelloStatusRemoving"];
        try
        {
            await _vaultLifecycle.RemoveHelloAsync(cancellationToken);
        }
        finally
        {
            IsVaultHelloBusy = false;
        }

        await RefreshVaultStatusAsync();
    }

    public async Task RefreshVaultStatusAsync()
    {
        AppSettings settings = await _configManager.LoadSettingsAsync();
        IsVaultEnabled = settings.VaultEnabled;
        IsVaultHelloEnrolled = settings.VaultHelloEnrolled;
        IsVaultHelloAvailable = IsVaultEnabled
            && await _vaultLifecycle.IsHelloEnrollmentAvailableAsync().ConfigureAwait(true);
        RefreshVaultHelloUiState();
    }

    private void RefreshVaultHelloUiState()
    {
        OnPropertyChanged(nameof(VaultHelloSectionVisible));
        OnPropertyChanged(nameof(VaultHelloEnrollVisible));
        OnPropertyChanged(nameof(VaultHelloDisableVisible));
        OnPropertyChanged(nameof(VaultHelloUnavailableVisible));
        OnPropertyChanged(nameof(CanEnableVaultHello));
        OnPropertyChanged(nameof(CanDisableVaultHello));
        EnableVaultHelloCommand.NotifyCanExecuteChanged();
        DisableVaultHelloCommand.NotifyCanExecuteChanged();

        if (IsVaultHelloBusy)
        {
            return;
        }

        if (!IsVaultEnabled)
        {
            VaultHelloStatusText = "";
            return;
        }

        if (IsVaultHelloEnrolled)
        {
            VaultHelloStatusText = _localizer["SettingsVaultHelloStatusEnabled"];
            return;
        }

        VaultHelloStatusText = IsVaultHelloAvailable
            ? _localizer["SettingsVaultHelloStatusAvailable"]
            : _localizer["SettingsVaultHelloStatusUnavailable"];
    }

    [RelayCommand]
    private async Task TestCredentialProviderAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CredentialProviderCommand))
        {
            CredentialProviderTestResult = _localizer["CredProvTestNoCommand"];
            return;
        }

        // A {KeyFile} template (KeePassXC key-file presets) requires a key file path;
        // launching with an empty path would fail opaquely, so warn and stop here.
        if (CredentialProviderCommand.Contains("{KeyFile}", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(CredentialProviderKeyFile))
        {
            CredentialProviderTestResult = _localizer["CredProvTestNoKeyFile"];
            return;
        }

        CredentialProviderTestResult = _localizer["CredProvTestRunning"];

        try
        {
            var provider = new Core.Security.CommandCredentialProvider(
                CredentialProviderCommand, CredentialProviderDatabase,
                CredentialProviderTimeoutMs,
                string.IsNullOrEmpty(CredentialProviderUnlockSecret)
                    ? null
                    : CredentialProviderUnlockSecret,
                string.IsNullOrWhiteSpace(CredentialProviderUsernameCommand)
                    ? null
                    : CredentialProviderUsernameCommand,
                CredentialProviderFirstLineOnly,
                string.IsNullOrWhiteSpace(CredentialProviderKeyFile)
                    ? null
                    : CredentialProviderKeyFile.Trim());

            var result = await provider.GetCredentialAsync(
                "test.example.com", 22, "testuser", "TestEntry", cancellationToken);

            CredentialProviderTestResult = result is not null
                ? _localizer["CredProvTestSuccess"]
                : _localizer["CredProvTestNoResult"];
        }
        catch (OperationCanceledException)
        {
            CredentialProviderTestResult = _localizer["CredProvTestTimeout"];
        }
        catch (Exception ex)
        {
            CredentialProviderTestResult = _localizer.Format("CredProvTestError", ex.Message);
        }
    }

    partial void OnDefaultThemeChanged(string value)
    {
        ThemeChanged?.Invoke(value);
    }

    partial void OnAccentTintChanged(string value)
    {
        AccentTintChanged?.Invoke(value);
    }

    public async Task DiscardChangesAsync()
    {
        DefaultTheme = _originalTheme;
        AccentTint = _originalAccentTint;
        var settings = await _configManager.LoadSettingsAsync();
        LoadFromSettings(settings);
    }

    private void SubscribeExternalToolTracking()
    {
        foreach (var tool in ExternalTools)
            tool.PropertyChanged += OnExternalToolItemPropertyChanged;
        ExternalTools.CollectionChanged += OnExternalToolsCollectionChanged;
    }

    private void UnsubscribeExternalToolTracking()
    {
        if (ExternalTools is null) return;
        foreach (var tool in ExternalTools)
            tool.PropertyChanged -= OnExternalToolItemPropertyChanged;
        ExternalTools.CollectionChanged -= OnExternalToolsCollectionChanged;
    }

    private void OnExternalToolItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        IsDirty = true;
    }

    private void OnExternalToolsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ExternalToolItemViewModel tool in e.OldItems)
                tool.PropertyChanged -= OnExternalToolItemPropertyChanged;
        if (e.NewItems is not null)
            foreach (ExternalToolItemViewModel tool in e.NewItems)
                tool.PropertyChanged += OnExternalToolItemPropertyChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnsubscribeExternalToolTracking();
        TrustedHostKeys.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Mark dirty when any settings property changes, excluding non-settings properties
        if (e.PropertyName is not (nameof(IsDirty) or nameof(IsBusy)
            or nameof(IsCheckingUpdate) or nameof(UpdateStatusText)
            or nameof(CredentialGuardStatusText)
            or nameof(IsInstallingUpdate) or nameof(DownloadProgress) or nameof(IsUpdateAvailable)
            or nameof(IsUpdateReleaseAvailable)
            or nameof(SelectedGateway) or nameof(SelectedProject)
            or nameof(SelectedExternalTool) or nameof(HasValidationErrors)
            or nameof(IsPinConfigured) or nameof(PinStatusText)
            or nameof(IsVaultHelloAvailable) or nameof(IsVaultHelloEnrolled)
            or nameof(IsVaultHelloBusy) or nameof(VaultHelloStatusText)
            or nameof(VaultHelloSectionVisible) or nameof(VaultHelloEnrollVisible)
            or nameof(VaultHelloDisableVisible) or nameof(VaultHelloUnavailableVisible)
            or nameof(CanEnableVaultHello) or nameof(CanDisableVaultHello)
            or nameof(ValidationSummary)
            or nameof(GeneralTabErrorCount) or nameof(HasGeneralTabErrors)
            or nameof(TerminalTabErrorCount) or nameof(HasTerminalTabErrors)
            or nameof(SshTabErrorCount) or nameof(HasSshTabErrors)
            or nameof(AdvancedTabErrorCount) or nameof(HasAdvancedTabErrors)
            or nameof(RdpTabErrorCount) or nameof(HasRdpTabErrors)))
        {
            IsDirty = true;
        }
    }

    [ObservableProperty]
    private bool _hasValidationErrors;

    [ObservableProperty]
    private string? _validationSummary;

    [ObservableProperty]
    private int _generalTabErrorCount;

    [ObservableProperty]
    private int _terminalTabErrorCount;

    [ObservableProperty]
    private int _sshTabErrorCount;

    [ObservableProperty]
    private int _advancedTabErrorCount;

    [ObservableProperty]
    private int _rdpTabErrorCount;

    public bool HasGeneralTabErrors => GeneralTabErrorCount > 0;

    public bool HasTerminalTabErrors => TerminalTabErrorCount > 0;

    public bool HasSshTabErrors => SshTabErrorCount > 0;

    public bool HasAdvancedTabErrors => AdvancedTabErrorCount > 0;

    public bool HasRdpTabErrors => RdpTabErrorCount > 0;

    partial void OnGeneralTabErrorCountChanged(int value) => OnPropertyChanged(nameof(HasGeneralTabErrors));

    partial void OnTerminalTabErrorCountChanged(int value) => OnPropertyChanged(nameof(HasTerminalTabErrors));

    partial void OnSshTabErrorCountChanged(int value) => OnPropertyChanged(nameof(HasSshTabErrors));

    partial void OnAdvancedTabErrorCountChanged(int value) => OnPropertyChanged(nameof(HasAdvancedTabErrors));

    /// <summary>
    /// Validates the global RDP resize lockout delay while preserving zero as the explicit disable value.
    /// </summary>
    /// <param name="value">The configured delay in milliseconds.</param>
    /// <param name="context">The validation context supplied by the data annotations pipeline.</param>
    /// <returns>A validation error when the value is neither zero nor within the supported range.</returns>
    public static System.ComponentModel.DataAnnotations.ValidationResult? ValidateRdpResizeEnableDelayMsValue(
        int value,
        ValidationContext context)
    {
        _ = context;

        // Zero explicitly disables the resize lockout; 1..999 ms is too short to be meaningful.
        if (value == 0 || value is >= 1000 and <= 60000)
        {
            return System.ComponentModel.DataAnnotations.ValidationResult.Success;
        }

        return new System.ComponentModel.DataAnnotations.ValidationResult(
            "RDP resize delay must be zero or between 1000 and 60000 ms.");
    }

    /// <summary>
    /// Validates the RDP connection watchdog timeout while preserving zero as the explicit disable
    /// value.
    /// </summary>
    /// <param name="value">The configured timeout in milliseconds.</param>
    /// <param name="context">The validation context supplied by the data annotations pipeline.</param>
    /// <returns>A validation error when the value is neither zero nor within the supported range.</returns>
    /// <remarks>
    /// <para>The bounds come from <see cref="Views.EmbeddedRdp.RdpConnectWatchdogPolicy"/>, the type
    /// the watchdog itself reads, rather than being written out again here. A plain range attribute
    /// could not express "zero, or five seconds to ten minutes", so it declared the range alone and
    /// rejected the disable value that both the schema and the watchdog accept. A settings file with
    /// the watchdog disabled therefore opened with an error on a field the user had set correctly,
    /// and blocked saving anything else until it was changed.</para>
    /// </remarks>
    public static System.ComponentModel.DataAnnotations.ValidationResult? ValidateRdpConnectWatchdogTimeoutMsValue(
        int value,
        ValidationContext context)
    {
        _ = context;

        if (value == Views.EmbeddedRdp.RdpConnectWatchdogPolicy.DisabledTimeoutMs
            || (value >= Views.EmbeddedRdp.RdpConnectWatchdogPolicy.MinTimeoutMs
                && value <= Views.EmbeddedRdp.RdpConnectWatchdogPolicy.MaxTimeoutMs))
        {
            return System.ComponentModel.DataAnnotations.ValidationResult.Success;
        }

        return new System.ComponentModel.DataAnnotations.ValidationResult(
            "RDP connection watchdog timeout must be zero or between 5000 and 600000 ms.");
    }

    private static readonly Dictionary<string, string> SettingsValidationKeyMap = new(StringComparer.Ordinal)
    {
        ["Max embedded sessions must be between 1 and 20."] = "ValidationSettingsMaxSessions",
        ["Update check interval must be between 1 and 8760 hours."] = "ValidationSettingsUpdateCheckInterval",
        ["RDP keep-alive interval must be between 5000 and 300000 ms."] = "ValidationSettingsRdpKeepAlive",
        ["Terminal font size must be between 8 and 72."] = "ValidationSettingsFontSize",
        ["Anti-idle interval must be between 0 and 3600 seconds."] = "ValidationSettingsAntiIdle",
        ["SSH TMOUT reset interval must be between 0 and 3600 seconds."] = "ValidationSettingsTmoutReset",
        ["SSH auto-reconnect attempts must be between 1 and 10."] = "ValidationSettingsSshAutoReconnectAttempts",
        ["Tunnel establishment delay must be between 0 and 30000 ms."] = "ValidationSettingsTunnelDelay",
        ["RDP connection watchdog timeout must be zero or between 5000 and 600000 ms."] = "ValidationSettingsRdpTimeout",
        ["RDP resize delay must be zero or between 1000 and 60000 ms."] = "ValidationSettingsRdpResizeDelay",
        ["RDP artifact cleanup delay must be between 1000 and 60000 ms."] = "ValidationSettingsRdpArtifactCleanupDelay",
        ["RDP credential autofill timeout must be between 5000 and 300000 ms."] = "ValidationSettingsRdpCredentialAutofillTimeout",
        ["RDP auto-reconnect maximum attempts must be between 1 and 20."] = "ValidationSettingsRdpAutoReconnectMaxAttempts",
        ["External tool timeout must be between 5000 and 600000 ms."] = "ValidationSettingsExtToolTimeout",
        ["Health check interval must be between 15 and 3600 seconds."] = "ValidationSettingsHealthCheckInterval",
        ["Probe timeout must be between 250 and 30000 ms."] = "ValidationSettingsHealthProbeTimeout",
        ["Max concurrent probes must be between 1 and 50."] = "ValidationSettingsHealthMaxConcurrent",
    };

    private static readonly string[] GeneralValidatedSettingPropertyNames =
    [
        nameof(MaxEmbeddedSessions),
        nameof(UpdateCheckIntervalHours),
    ];

    private static readonly string[] TerminalValidatedSettingPropertyNames =
    [
        nameof(TerminalFontSize),
    ];

    private static readonly string[] SshValidatedSettingPropertyNames =
    [
        nameof(AntiIdleInterval),
        nameof(SshTmoutResetInterval),
        nameof(SshAutoReconnectAttempts),
    ];

    private static readonly string[] AdvancedValidatedSettingPropertyNames =
    [
        nameof(TunnelEstablishmentDelayMs),
        nameof(RdpConnectWatchdogTimeoutMs),
        nameof(ExternalToolTimeoutMs),
        nameof(SessionHealthCheckIntervalSeconds),
        nameof(SessionHealthProbeTimeoutMs),
        nameof(SessionHealthMaxConcurrent),
    ];

    /// <summary>
    /// The validated settings whose fields live on the RDP tab.
    /// </summary>
    /// <remarks>
    /// Four of these were counted on the Advanced badge while their fields are on this tab, so the
    /// badge sent the user to look somewhere the field is not. Two further settings were in no list
    /// at all, which is worse: the save was refused with no banner, no badge and no field error, so
    /// pressing Save simply did nothing.
    /// </remarks>
    private static readonly string[] RdpValidatedSettingPropertyNames =
    [
        nameof(RdpResizeEnableDelayMs),
        nameof(RdpArtifactCleanupDelayMs),
        nameof(RdpCredentialAutofillTimeoutMs),
        nameof(RdpAutoReconnectMaxAttempts),
        nameof(RdpKeepAliveIntervalMs),
    ];

    private string? GetLocalizedFieldError(string propertyName)
    {
        var error = GetErrors(propertyName)
            .OfType<System.ComponentModel.DataAnnotations.ValidationResult>()
            .FirstOrDefault();

        var message = error?.ErrorMessage;
        if (message is not null
            && SettingsValidationKeyMap.TryGetValue(message, out var key))
        {
            return _localizer[key];
        }

        return message;
    }

    private void RefreshValidationSummary()
    {
        GeneralTabErrorCount = CountValidationErrors(GeneralValidatedSettingPropertyNames);
        TerminalTabErrorCount = CountValidationErrors(TerminalValidatedSettingPropertyNames);
        SshTabErrorCount = CountValidationErrors(SshValidatedSettingPropertyNames);
        AdvancedTabErrorCount = CountValidationErrors(AdvancedValidatedSettingPropertyNames);
        RdpTabErrorCount = CountValidationErrors(RdpValidatedSettingPropertyNames);

        string? firstError = GetFirstLocalizedFieldError(GeneralValidatedSettingPropertyNames)
            ?? GetFirstLocalizedFieldError(TerminalValidatedSettingPropertyNames)
            ?? GetFirstLocalizedFieldError(SshValidatedSettingPropertyNames)
            ?? GetFirstLocalizedFieldError(RdpValidatedSettingPropertyNames)
            ?? GetFirstLocalizedFieldError(AdvancedValidatedSettingPropertyNames);

        ValidationSummary = firstError;
        HasValidationErrors = firstError is not null;
    }

    private int CountValidationErrors(string[] propertyNames)
    {
        int count = 0;
        foreach (string propertyName in propertyNames)
        {
            if (GetLocalizedFieldError(propertyName) is not null)
            {
                count++;
            }
        }

        return count;
    }

    private string? GetFirstLocalizedFieldError(string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            string? error = GetLocalizedFieldError(propertyName);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a localized error message if any external tool has an empty name,
    /// empty executable path, duplicate name, or references a non-existent binary.
    /// Returns null when valid.
    /// </summary>
    private string? ValidateExternalTools()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in ExternalTools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || string.IsNullOrWhiteSpace(tool.ExecutablePath))
            {
                return _localizer["ValidationExtToolIncomplete"];
            }

            if (!seen.Add(tool.Name.Trim()))
            {
                return _localizer.Format("ValidationExtToolDuplicate", tool.Name.Trim());
            }

            var exePath = tool.ExecutablePath.Trim();
            if (!ExeExistsOnDiskOrPath(exePath))
            {
                return _localizer.Format("ValidationExtToolNotFound", tool.Name.Trim(), exePath);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if the executable exists at the given absolute path
    /// or can be found on the system PATH.
    /// </summary>
    private static bool ExeExistsOnDiskOrPath(string exePath)
    {
        if (System.IO.File.Exists(exePath)) return true;

        // Bare filename like "ping.exe" — search PATH
        if (!System.IO.Path.IsPathRooted(exePath))
        {
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
            foreach (var dir in pathDirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var fullPath = System.IO.Path.Combine(dir.Trim(), exePath);
                if (System.IO.File.Exists(fullPath)) return true;
            }
        }

        return false;
    }

    // Field-by-field this raised the passphrase presence flag on every copy, which flips
    // UsesLegacySshCredentialMapping and changes how the gateway authenticates.
    private static SshGatewayDto CloneGateway(SshGatewayDto g) => g.CloneFaithfully();

    private static ProjectDto CloneProject(ProjectDto p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        Color = p.Color,
        DefaultSshUsername = p.DefaultSshUsername,
        DefaultSshKeyPath = p.DefaultSshKeyPath,
        DefaultGatewayId = p.DefaultGatewayId
    };
}
