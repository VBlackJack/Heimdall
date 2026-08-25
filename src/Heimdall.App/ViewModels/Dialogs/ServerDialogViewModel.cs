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
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Rdp;
using Heimdall.Rdp.Display;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;

namespace Heimdall.App.ViewModels.Dialogs;

public enum SshAgentChipState
{
    Off,
    Warn,
    Ok
}

public enum SshTestChipState
{
    Hidden,
    InProgress,
    Success,
    Failure,
    Cancelled
}

public enum SessionLoggingOverrideSelection
{
    Inherit,
    On,
    Off
}

/// <summary>
/// ViewModel for the redesigned server add/edit dialog.
/// Keeps the persisted DTO model intact while exposing UX-friendly
/// derived state for tunnel routing, authentication, and option grouping.
/// </summary>
public partial class ServerDialogViewModel : ObservableValidator
{
    private const int AgentIdentityProbeTimeoutMs = 750;
    private const int DefaultRdpFixedWidth = 1920;
    private const int DefaultRdpFixedHeight = 1080;
    private LocalizationManager? _localizer;
    private int _defaultRdpTunnelPort = DefaultPorts.RdpTunnel;
    private int _defaultSshTunnelPort = DefaultPorts.SshTunnel;
    private int _defaultRdpResizeEnableDelayMs = 10000;
    private SshAgentPreference _sshAgentPreference = SshAgentPreference.AutoOpenSshFirst;
    private bool? _rdpDialogAdvancedDefault;
    private bool _hasAppliedRdpDialogAdvancedDefault;
    private readonly IMonitorEnumerator _monitorEnumerator;
    private int _screenCount;

    /// <summary>
    /// Localizer for translating validation error messages. Set by the dialog service.
    /// </summary>
    public LocalizationManager? Localizer
    {
        get => _localizer;
        set
        {
            _localizer = value;
            OnPropertyChanged(nameof(ConnectionTypeDisplayName));
            OnPropertyChanged(nameof(SshAuthHint));
            OnPropertyChanged(nameof(SshKeyPassphraseHint));
            OnPropertyChanged(nameof(SessionKindLabel));
            OnPropertyChanged(nameof(GatewayToServerLabel));
            OnPropertyChanged(nameof(WinRmUseSslHelpText));
            OnPropertyChanged(nameof(CanSkipWinRmCertificate));
            OnPropertyChanged(nameof(RdpResizeEnableDelayPlaceholder));
            RefreshAvailableMonitors();
            RefreshAgentChipIfNeeded();
        }
    }

    /// <summary>Application settings for configurable defaults.</summary>
    public AppSettings? Settings
    {
        set
        {
            if (value is null) return;
            _defaultRdpTunnelPort = value.DefaultRdpTunnelPort;
            _defaultSshTunnelPort = value.DefaultSshTunnelPort;
            _defaultRdpResizeEnableDelayMs = value.RdpResizeEnableDelayMs;
            _sshAgentPreference = value.SshAgentPreference;
            ApplyRdpDialogAdvancedDefault(value.RdpDialogAdvancedDefault);
            OnPropertyChanged(nameof(RdpResizeEnableDelayPlaceholder));
            RefreshAgentChipIfNeeded();
        }
    }

    private string L(string key) => Localizer?[key] ?? key;

    // --- Dialog state ---

    [ObservableProperty]
    private string _dialogTitle = "";

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private bool _isAdvancedMode;

    internal bool IsApplyingRdpDialogAdvancedDefault { get; private set; }

    internal void ApplyRdpDialogAdvancedDefault(bool advancedDefault)
    {
        _rdpDialogAdvancedDefault = advancedDefault;
        TryApplyRdpDialogAdvancedDefault();
    }

    /// <summary>
    /// Whether the user has chosen a protocol (Step 1 complete).
    /// In edit mode this is always true. In add mode it starts false.
    /// </summary>
    [ObservableProperty]
    private bool _isProtocolSelected;

    /// <summary>
    /// Whether the protocol selector step should be displayed.
    /// True only in add mode before a protocol has been chosen.
    /// </summary>
    public bool ShowProtocolSelector => !IsEditMode && !IsProtocolSelected;

    /// <summary>
    /// Whether the form fields (Step 2) should be displayed.
    /// True after a protocol is selected, or always in edit mode.
    /// </summary>
    public bool ShowFormFields => IsEditMode || IsProtocolSelected;

    public bool IsLocalConnection => string.Equals(ConnectionType, "Local", StringComparison.OrdinalIgnoreCase);

    partial void OnIsProtocolSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowProtocolSelector));
        OnPropertyChanged(nameof(ShowFormFields));
        TryApplyRdpDialogAdvancedDefault();
    }

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowProtocolSelector));
        OnPropertyChanged(nameof(ShowFormFields));
        OnPropertyChanged(nameof(CanSwitchToAuto));
        TryApplyRdpDialogAdvancedDefault();
    }

    /// <summary>
    /// Selects a protocol and transitions from Step 1 to Step 2.
    /// </summary>
    [RelayCommand]
    private void SelectProtocol(string protocol)
    {
        ConnectionType = protocol;
        IsProtocolSelected = true;
    }

    /// <summary>
    /// Returns to the protocol selector (Step 1) from Step 2 in add mode.
    /// </summary>
    [RelayCommand]
    private void BackToProtocolSelector()
    {
        ClearValidationState();
        IsProtocolSelected = false;
    }

    // --- Identity ---

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Display name is required.")]
    [MinLength(1, ErrorMessage = "Display name cannot be empty.")]
    private string _displayName = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Server address is required.")]
    [MinLength(1, ErrorMessage = "Server address cannot be empty.")]
    private string _remoteServer = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
    private int _remotePort = DefaultPorts.Rdp;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 65535, ErrorMessage = "Local tunnel port must be between 1 and 65535.")]
    private int _localPort = DefaultPorts.RdpTunnel;

    [ObservableProperty]
    private bool _useAutomaticTunnelPort = true;

    [ObservableProperty]
    private int _socksProxyPort;

    public string SocksProxyDisplay => SocksProxyPort > 0
        ? $"127.0.0.1:{SocksProxyPort}"
        : L("TunnelingNoSocks");

    partial void OnSocksProxyPortChanged(int value)
        => OnPropertyChanged(nameof(SocksProxyDisplay));

    [ObservableProperty]
    private int _remoteBindPort;

    [ObservableProperty]
    private int _remoteLocalPort;

    public string RemoteForwardDisplay => RemoteBindPort > 0
        ? $"server:{RemoteBindPort} \u2192 local:{(RemoteLocalPort > 0 ? RemoteLocalPort : RemoteBindPort)}"
        : L("TunnelingNoRemoteFwd");

    partial void OnRemoteBindPortChanged(int value)
        => OnPropertyChanged(nameof(RemoteForwardDisplay));

    partial void OnRemoteLocalPortChanged(int value)
        => OnPropertyChanged(nameof(RemoteForwardDisplay));

    [ObservableProperty]
    private string _group = "";

    [ObservableProperty]
    private string _connectionType = "RDP";

    [ObservableProperty]
    private bool? _sessionLoggingOverride;

    [ObservableProperty]
    private SessionLoggingOverrideSelection _sessionLoggingOverrideSelection = SessionLoggingOverrideSelection.Inherit;

    partial void OnSessionLoggingOverrideChanged(bool? value)
    {
        var selection = value switch
        {
            true => SessionLoggingOverrideSelection.On,
            false => SessionLoggingOverrideSelection.Off,
            _ => SessionLoggingOverrideSelection.Inherit
        };

        if (SessionLoggingOverrideSelection != selection)
        {
            SessionLoggingOverrideSelection = selection;
        }
    }

    partial void OnSessionLoggingOverrideSelectionChanged(SessionLoggingOverrideSelection value)
    {
        bool? profileOverride = value switch
        {
            SessionLoggingOverrideSelection.On => true,
            SessionLoggingOverrideSelection.Off => false,
            _ => null
        };

        if (SessionLoggingOverride != profileOverride)
        {
            SessionLoggingOverride = profileOverride;
        }
    }

    /// <summary>
    /// Optional override for the external password-manager entry name (substituted for
    /// the credential provider's <c>{Title}</c> placeholder). Empty falls back to the
    /// display name.
    /// </summary>
    [ObservableProperty]
    private string _vaultEntryName = "";

    // --- WinRM settings ---

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 65535, ErrorMessage = "WinRM port must be between 1 and 65535.")]
    private int _winRmPort = DefaultPorts.WinRmHttp;

    [ObservableProperty]
    private string _winRmUsername = "";

    [ObservableProperty]
    private string _winRmPassword = "";

    [ObservableProperty]
    private bool _winRmUseSsl;

    [ObservableProperty]
    private bool _winRmSkipCertificateCheck;

    [ObservableProperty]
    private WinRmIdentityMode _winRmIdentityMode = WinRmIdentityMode.CurrentUser;

    public string? ExistingWinRmPasswordEncrypted { get; set; }

    // --- SSH settings ---

    [ObservableProperty]
    private string _sshUsername = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 65535, ErrorMessage = "SSH port must be between 1 and 65535.")]
    private int _sshPort = DefaultPorts.Ssh;

    [ObservableProperty]
    private string _sshKeyPath = "";

    [ObservableProperty]
    private string _sshPassword = "";

    [ObservableProperty]
    private string _sshKeyPassphrase = "";

    // Existing encrypted SSH secrets (preserved on edit if user doesn't change them)
    public string? ExistingSshPasswordEncrypted { get; set; }
    public string? ExistingSshKeyPassphraseEncrypted { get; set; }
    public string? ExistingRdpPasswordEncrypted { get; set; }

    [ObservableProperty]
    private bool _sshCompression;

    [ObservableProperty]
    private bool _sshX11Forwarding;

    [ObservableProperty]
    private bool _sshAgentForwarding;

    [ObservableProperty]
    private string _sshMode = "Embedded";

    [ObservableProperty]
    private SshAgentChipState _agentChipState = SshAgentChipState.Off;

    [ObservableProperty]
    private string _agentChipText = "";

    [ObservableProperty]
    private SshTestChipState _testChipState = SshTestChipState.Hidden;

    [ObservableProperty]
    private string _testChipText = "";

    [ObservableProperty]
    private bool _isTestingRdpConnection;

    [ObservableProperty]
    private string _postConnectCommand = "";

    [ObservableProperty]
    private int _postConnectDelayMs = 800;

    /// <summary>
    /// Whether the SSH key passphrase field should be shown.
    /// </summary>
    public bool HasSshKeyPath => !string.IsNullOrWhiteSpace(SshKeyPath);

    /// <summary>
    /// Returns the hint that describes which SSH authentication method will be
    /// attempted, based on the current state of the SSH credential fields.
    /// Order of precedence matches <see cref="Heimdall.App.Services.Handlers.SshHandler"/>:
    /// key path takes priority, then password (typed or preserved encrypted),
    /// then SSH agent fallback.
    /// </summary>
    public string SshAuthHint
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SshKeyPath))
            {
                return L("ServerDialogSshAuthHintKey");
            }
            if (!string.IsNullOrEmpty(SshPassword)
                || !string.IsNullOrEmpty(ExistingSshPasswordEncrypted))
            {
                return L("ServerDialogSshAuthHintPassword");
            }
            return L("ServerDialogSshAuthHintAgent");
        }
    }

    /// <summary>
    /// Returns the hint for the SSH key passphrase field.
    /// </summary>
    public string SshKeyPassphraseHint => L("ServerDialogSshKeyPassphraseHint");

    partial void OnSshKeyPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasSshKeyPath));
        OnPropertyChanged(nameof(SshAuthHint));
        ResetTestChip();
        if (string.IsNullOrWhiteSpace(value))
        {
            SshKeyPassphrase = "";
            ExistingSshKeyPassphraseEncrypted = null;
        }
    }

    partial void OnSshPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(SshAuthHint));
        ResetTestChip();
    }

    partial void OnWinRmIdentityModeChanged(WinRmIdentityMode value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsWinRmCredentialIdentity));
    }

    partial void OnWinRmUseSslChanged(bool value)
    {
        if (value && IsWinRmConnection && UsesGateway)
        {
            WinRmUseSsl = false;
            return;
        }

        if (!value)
        {
            WinRmSkipCertificateCheck = false;
        }

        if (!_isInitializing)
        {
            if (value && WinRmPort == DefaultPorts.WinRmHttp)
            {
                WinRmPort = DefaultPorts.WinRmHttps;
            }
            else if (!value && WinRmPort == DefaultPorts.WinRmHttps)
            {
                WinRmPort = DefaultPorts.WinRmHttp;
            }
        }

        OnPropertyChanged(nameof(CanSkipWinRmCertificate));
        RaisePortDerivedStateChanged();
    }

    private void CoerceWinRmSslForGateway()
    {
        if (IsWinRmConnection && UsesGateway && WinRmUseSsl)
        {
            WinRmUseSsl = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestSshConnection))]
    private async Task TestSshConnectionAsync(CancellationToken ct)
    {
        TestChipState = SshTestChipState.InProgress;
        TestChipText = L("ServerDialogTestChipInProgress");

        try
        {
            Heimdall.Ssh.PreflightResult preflight = Heimdall.Ssh.AuthPreflightChecker.Check(
                BuildTestConnectionParams(),
                isTunnelMode: true);
            if (!preflight.Success)
            {
                TestChipState = SshTestChipState.Failure;
                TestChipText = string.Format(
                    CultureInfo.CurrentCulture,
                    L("ServerDialogTestChipFailure"),
                    ResolvePreflightMessage(preflight));
                return;
            }

            SshConnectionProbe.ProbeResult probe = await SshConnectionProbe.ProbeAsync(
                    RemoteServer,
                    SshPort,
                    timeoutMs: 5000,
                    ct)
                .ConfigureAwait(true);

            if (probe.Success)
            {
                TestChipState = SshTestChipState.Success;
                TestChipText = string.Format(
                    CultureInfo.CurrentCulture,
                    L("ServerDialogTestChipSuccess"),
                    probe.Banner ?? "?");
                return;
            }

            TestChipState = SshTestChipState.Failure;
            TestChipText = string.Format(
                CultureInfo.CurrentCulture,
                L("ServerDialogTestChipFailure"),
                ResolveSshProbeMessage(probe));
        }
        catch (OperationCanceledException)
        {
            TestChipState = SshTestChipState.Hidden;
            TestChipText = "";
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[ServerDialog] test connection failed: {ex.Message}");
            TestChipState = SshTestChipState.Failure;
            TestChipText = string.Format(
                CultureInfo.CurrentCulture,
                L("ServerDialogTestChipFailure"),
                ex.Message);
        }
    }

    private bool CanTestSshConnection()
        => IsSshFamilyConnection
           && !string.IsNullOrWhiteSpace(RemoteServer)
           && SshPort is > 0 and <= 65535;

    [RelayCommand(CanExecute = nameof(CanTestRdpConnection))]
    private async Task TestRdpConnectionAsync(CancellationToken ct)
    {
        IsTestingRdpConnection = true;
        TestChipState = SshTestChipState.InProgress;
        TestChipText = L("ServerDialogTestChipInProgress");

        try
        {
            RdpConnectivityTester tester = new RdpConnectivityTester();
            RdpConnectivityTestResult result = await tester.TestAsync(
                    RemoteServer,
                    RemotePort,
                    TimeSpan.FromSeconds(5),
                    ct)
                .ConfigureAwait(true);

            ApplyRdpTestResult(result);
        }
        catch (OperationCanceledException)
        {
            ApplyRdpTestResult(RdpConnectivityTestResult.Cancelled());
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[ServerDialog] RDP test connection failed: {ex.Message}");
            TestChipState = SshTestChipState.Failure;
            TestChipText = string.Format(
                CultureInfo.CurrentCulture,
                L("ServerDialogTestChipFailure"),
                ex.Message);
        }
        finally
        {
            IsTestingRdpConnection = false;
        }
    }

    [RelayCommand]
    private void CancelRdpTest()
    {
        TestRdpConnectionCommand.Cancel();
    }

    private bool CanTestRdpConnection()
        => IsRdpConnection
           && !string.IsNullOrWhiteSpace(RemoteServer)
           && RemotePort is > 0 and <= 65535;

    private void ApplyRdpTestResult(RdpConnectivityTestResult result)
    {
        TestChipState = result.Outcome switch
        {
            RdpConnectivityTestOutcome.Success => SshTestChipState.Success,
            RdpConnectivityTestOutcome.Cancelled => SshTestChipState.Cancelled,
            _ => SshTestChipState.Failure
        };

        string wrapperKey = result.Outcome == RdpConnectivityTestOutcome.Success
            ? "ServerDialogTestChipSuccess"
            : result.Outcome == RdpConnectivityTestOutcome.Cancelled
                ? "{0}"
                : "ServerDialogTestChipFailure";

        string detail = FormatRdpTestResult(result);
        TestChipText = string.Equals(wrapperKey, "{0}", StringComparison.Ordinal)
            ? detail
            : string.Format(CultureInfo.CurrentCulture, L(wrapperKey), detail);
    }

    private string FormatRdpTestResult(RdpConnectivityTestResult result)
    {
        return result.Outcome switch
        {
            RdpConnectivityTestOutcome.Success => string.Format(
                CultureInfo.CurrentCulture,
                L("ServerDialogRdpTestSuccess"),
                result.ResolvedAddress ?? "?",
                (int)Math.Round(result.TcpElapsed?.TotalMilliseconds ?? 0)),
            RdpConnectivityTestOutcome.InvalidAddress => L("ServerDialogRdpTestInvalidAddress"),
            RdpConnectivityTestOutcome.InvalidPort => L("ServerDialogRdpTestInvalidPort"),
            RdpConnectivityTestOutcome.DnsTimeout => L("ServerDialogRdpTestDnsTimeout"),
            RdpConnectivityTestOutcome.DnsFailed => string.Format(
                CultureInfo.CurrentCulture,
                L("ServerDialogRdpTestDnsFailed"),
                result.Detail ?? string.Empty),
            RdpConnectivityTestOutcome.DnsNoResults => L("ServerDialogRdpTestDnsNoResults"),
            RdpConnectivityTestOutcome.TcpTimeout => string.Format(
                CultureInfo.CurrentCulture,
                L("ServerDialogRdpTestTcpTimeout"),
                result.ResolvedAddress ?? "?"),
            RdpConnectivityTestOutcome.TcpFailed => string.Format(
                CultureInfo.CurrentCulture,
                L("ServerDialogRdpTestTcpFailed"),
                result.ResolvedAddress ?? "?",
                result.Detail ?? result.SocketError?.ToString() ?? string.Empty),
            RdpConnectivityTestOutcome.Cancelled => L("ServerDialogRdpTestCancelled"),
            _ => L("ServerDialogRdpTestCancelled")
        };
    }

    private Heimdall.Ssh.SshConnectionParams BuildTestConnectionParams()
    {
        return new Heimdall.Ssh.SshConnectionParams
        {
            Host = RemoteServer,
            Port = SshPort,
            Username = string.IsNullOrWhiteSpace(SshUsername) ? "user" : SshUsername,
            KeyPath = string.IsNullOrWhiteSpace(SshKeyPath) ? null : SshKeyPath,
            Password = !string.IsNullOrEmpty(SshPassword)
                ? SshPassword
                : !string.IsNullOrEmpty(ExistingSshPasswordEncrypted)
                    ? "__preserved__"
                    : null,
            KeyPassphrase = !string.IsNullOrEmpty(SshKeyPassphrase)
                ? SshKeyPassphrase
                : !string.IsNullOrEmpty(ExistingSshKeyPassphraseEncrypted)
                    ? "__preserved__"
                    : null,
            SshAgentPreference = _sshAgentPreference,
            AgentForwarding = SshAgentForwarding,
            Compression = SshCompression,
            X11Forwarding = SshX11Forwarding,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
    }

    private string ResolvePreflightMessage(Heimdall.Ssh.PreflightResult preflight)
    {
        string message = preflight.Message ?? L("ErrorSshTestConnectionFailed");
        if (message.StartsWith("Error", StringComparison.Ordinal) && Localizer is not null)
        {
            string resolved = Localizer[message];
            if (!string.Equals(resolved, message, StringComparison.Ordinal))
            {
                return resolved;
            }
        }

        return message;
    }

    private string ResolveSshProbeMessage(SshConnectionProbe.ProbeResult probe)
    {
        if (string.IsNullOrWhiteSpace(probe.MessageKey) || Localizer is null)
        {
            return L("ErrorSshTestConnectionFailed");
        }

        string template = Localizer[probe.MessageKey];
        if (string.Equals(template, probe.MessageKey, StringComparison.Ordinal))
        {
            return L("ErrorSshTestConnectionFailed");
        }

        if (probe.MessageArguments.Count == 0)
        {
            return template;
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            template,
            probe.MessageArguments.ToArray());
    }

    private void ResetTestChip()
    {
        TestChipState = SshTestChipState.Hidden;
        TestChipText = "";
    }

    private void RaiseTestCommandCanExecuteChanged()
    {
        TestSshConnectionCommand.NotifyCanExecuteChanged();
        TestRdpConnectionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RefreshAgentChip()
    {
        if (!IsSshFamilyConnection)
        {
            AgentChipState = SshAgentChipState.Off;
            AgentChipText = "";
            return;
        }

        try
        {
            SshAgentRegistry registry = SshAgentRegistry.CreateDefault(_sshAgentPreference);
            (SshAgentChipState state, string text) = ProbeAgent(registry);
            AgentChipState = state;
            AgentChipText = text;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[ServerDialog] agent chip probe failed: {ex.Message}");
            AgentChipState = SshAgentChipState.Off;
            AgentChipText = L("ServerDialogAgentChipOff");
        }
    }

    private void RefreshAgentChipIfNeeded()
    {
        if (IsSshFamilyConnection)
        {
            RefreshAgentChip();
            return;
        }

        AgentChipState = SshAgentChipState.Off;
        AgentChipText = "";
    }

    private (SshAgentChipState State, string Text) ProbeAgent(SshAgentRegistry registry)
    {
        IReadOnlyList<ISshAgent> availableAgents = registry.GetAvailableAgents();
        if (availableAgents.Count == 0)
        {
            return (SshAgentChipState.Off, L("ServerDialogAgentChipOff"));
        }

        List<(string Name, int KeyCount)> counts = availableAgents
            .Select(agent => (agent.Name, KeyCount: SafeGetIdentityCount(agent)))
            .ToList();
        int totalKeys = counts.Sum(agent => agent.KeyCount);
        if (totalKeys == 0)
        {
            return (SshAgentChipState.Warn,
                string.Format(CultureInfo.CurrentCulture, L("ServerDialogAgentChipWarn"), counts[0].Name));
        }

        string displayAgent = counts.FirstOrDefault(agent => agent.KeyCount > 0).Name ?? counts[0].Name;
        return (SshAgentChipState.Ok,
            string.Format(CultureInfo.CurrentCulture, L("ServerDialogAgentChipOk"), displayAgent, totalKeys));
    }

    private static int SafeGetIdentityCount(ISshAgent agent)
    {
        try
        {
            Task<int> task = Task.Run(() => agent.GetIdentities().Count);
            if (!task.Wait(TimeSpan.FromMilliseconds(AgentIdentityProbeTimeoutMs)))
            {
                FileLogger.Warn($"SSH agent {agent.Name}: identity lookup timed out.");
                return 0;
            }

            return task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"SSH agent {agent.Name}: identity lookup failed: {ex.GetBaseException().Message}");
            return 0;
        }
    }

    // --- Local Shell settings ---

    [ObservableProperty]
    private string _localShellExecutable = "powershell.exe";

    [ObservableProperty]
    private string _localShellArguments = "";

    [ObservableProperty]
    private string _localShellWorkingDirectory = "";

    [ObservableProperty]
    private bool _localShellElevated;

    [ObservableProperty]
    private Core.Models.ElevationMode _elevationMode = Core.Models.ElevationMode.None;

    // --- Citrix settings ---

    [ObservableProperty]
    private string _citrixStoreFrontUrl = "";

    [ObservableProperty]
    private string _citrixAppName = "";

    [ObservableProperty]
    private string _citrixIcaFilePath = "";

    [ObservableProperty]
    private bool _citrixSeamlessMode = true;

    [ObservableProperty]
    private bool _citrixUseSso = true;

    // --- FTP settings ---

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 65535, ErrorMessage = "FTP port must be between 1 and 65535.")]
    private int _ftpPort = 21;

    [ObservableProperty]
    private string _ftpUsername = "";

    [ObservableProperty]
    private string _ftpPassword = "";

    // Existing encrypted FTP password (preserved on edit if user doesn't change it)
    public string? ExistingFtpPasswordEncrypted { get; set; }

    // --- Telnet settings ---

    [ObservableProperty]
    private string _telnetUsername = "";

    [ObservableProperty]
    private string _telnetPassword = "";

    public string? ExistingTelnetPasswordEncrypted { get; set; }

    // --- FTP options ---

    [ObservableProperty]
    private bool _ftpPassiveMode = true;

    [ObservableProperty]
    private bool _ftpUseSsl;

    // --- VNC settings ---

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 65535, ErrorMessage = "VNC port must be between 1 and 65535.")]
    private int _vncPort = DefaultPorts.Vnc;

    [ObservableProperty]
    private string _vncPassword = "";

    [ObservableProperty]
    private bool _vncViewOnly;

    // Existing encrypted VNC password (preserved on edit if user doesn't change it)
    public string? ExistingVncPasswordEncrypted { get; set; }

    // --- RDP settings ---

    [ObservableProperty]
    private string _rdpUsername = "";

    [ObservableProperty]
    private string _rdpDomain = "";

    [ObservableProperty]
    private string _rdpPassword = "";

    [ObservableProperty]
    private string _rdpMode = "Embedded";

    [ObservableProperty]
    private bool _rdpUseGlobalDefaults = true;

    [ObservableProperty]
    private bool _rdpAntiIdle;

    [ObservableProperty]
    private bool _redirectClipboard = true;

    [ObservableProperty]
    private bool _redirectDrives;

    [ObservableProperty]
    private bool _redirectPrinters;

    [ObservableProperty]
    private bool _rdpRedirectComPorts;

    [ObservableProperty]
    private bool _rdpRedirectSmartCards;

    [ObservableProperty]
    private bool _rdpRedirectWebcam;

    [ObservableProperty]
    private bool _rdpRedirectUsb;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(0, 2, ErrorMessage = "Audio mode must be 0 (disabled), 1 (local), or 2 (remote).")]
    private int _rdpAudioMode;

    [ObservableProperty]
    private bool _rdpAudioCapture;

    [ObservableProperty]
    private bool _rdpMultiMonitor;

    [ObservableProperty]
    private bool _rdpDynamicResolution = true;

    [ObservableProperty]
    private RdpResolutionMode _rdpResolutionMode = RdpResolutionMode.Auto;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(200, 7680, ErrorMessage = "RDP fixed width must be between 200 and 7680.")]
    private int _rdpFixedWidth = DefaultRdpFixedWidth;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(200, 4320, ErrorMessage = "RDP fixed height must be between 200 and 4320.")]
    private int _rdpFixedHeight = DefaultRdpFixedHeight;

    [ObservableProperty]
    private bool _rdpInitialSmartSizing = true;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ServerDialogViewModel), nameof(ValidateRdpResizeEnableDelayMsValue))]
    private int? _rdpResizeEnableDelayMs;

    [ObservableProperty]
    private bool _rdpNla = true;

    [ObservableProperty]
    private bool _rdpStrictServerAuthentication;

    [ObservableProperty]
    private string _rdpAspectRatio = "Stretch";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(8, 32, ErrorMessage = "Color depth must be between 8 and 32.")]
    private int _rdpColorDepth = 32;

    [ObservableProperty]
    private bool _rdpBitmapCaching = true;

    [ObservableProperty]
    private bool _rdpCompression = true;

    [ObservableProperty]
    private bool _rdpHardwareAcceleration;

    [ObservableProperty]
    private bool _rdpAutoReconnect = true;

    [ObservableProperty]
    private bool _rdpAdminMode;

    [ObservableProperty]
    private bool _rdpFullScreen;

    private const int PerfDisableWallpaperFlag = 0x01;
    private const int PerfDisableDragFlag = 0x02;
    private const int PerfDisableAnimationsFlag = 0x04;
    private const int PerfDisableThemesFlag = 0x08;
    private const int PerfDisableCursorShadowFlag = 0x20;
    private const int PerfEnableFontSmoothingFlag = 0x80;
    private const int PerfEnableCompositionFlag = 0x100;

    [ObservableProperty]
    private int _rdpPerformanceFlags;

    [ObservableProperty]
    private bool _rdpPerfDisableWallpaper;

    [ObservableProperty]
    private bool _rdpPerfDisableDrag;

    [ObservableProperty]
    private bool _rdpPerfDisableAnimations;

    [ObservableProperty]
    private bool _rdpPerfDisableThemes;

    [ObservableProperty]
    private bool _rdpPerfDisableCursorShadow;

    [ObservableProperty]
    private bool _rdpPerfEnableFontSmoothing;

    [ObservableProperty]
    private bool _rdpPerfEnableComposition;

    [ObservableProperty]
    private bool _rdpDisableUdp;

    [ObservableProperty]
    private string _rdpGateway = "";

    // --- Gateway ---

    [ObservableProperty]
    private string _selectedGatewayId = "";

    [ObservableProperty]
    private bool _directConnection;

    [ObservableProperty]
    private ObservableCollection<GatewayOption> _availableGateways = [];

    /// <summary>
    /// Supplied by the shell so this tab can create a gateway without owning the
    /// configuration stack. Returns the option to select, or <see langword="null"/> when the
    /// user cancelled.
    /// </summary>
    /// <remarks>
    /// The tab is where the gateway is chosen, so it is where creating one belongs. Sending
    /// the user to Settings, and only to Settings, is what made this whole area hard to
    /// understand: the list offered no way to fill itself, and said nothing when empty.
    /// </remarks>
    public Func<Task<GatewayOption?>>? CreateGatewayRequested { get; set; }

    /// <summary>
    /// True when the protocol tunnels but nothing has ever been configured to tunnel
    /// through, which is the state that used to be an empty dropdown and no explanation.
    /// </summary>
    public bool HasNoGateway => SupportsGateway && AvailableGateways.Count == 0;

    public bool CanCreateGateway => CanSelectGateway && CreateGatewayRequested is not null;

    [RelayCommand(CanExecute = nameof(CanCreateGateway))]
    private async Task CreateGatewayAsync()
    {
        if (CreateGatewayRequested is null)
        {
            return;
        }

        GatewayOption? created = await CreateGatewayRequested();
        if (created is null)
        {
            return;
        }

        AvailableGateways.Add(created);
        SelectedGatewayId = created.Id;
        RaiseDerivedStateChanged();
    }

    // --- Project ---

    [ObservableProperty]
    private string _selectedProjectId = "";

    [ObservableProperty]
    private ObservableCollection<ProjectOption> _availableProjects = [];

    // --- Metadata ---

    [ObservableProperty]
    private string _tags = "";

    [ObservableProperty]
    private string _macAddress = "";

    [ObservableProperty]
    private string _environment = "None";

    [ObservableProperty]
    private bool _isFavorite;

    // --- Dirty state tracking ---

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// Suppresses dirty tracking during initialization (e.g., FromDto).
    /// </summary>
    private bool _isInitializing;

    /// <summary>
    /// Properties excluded from dirty tracking (dialog state, validation, computed).
    /// </summary>
    private static readonly HashSet<string> DirtyExcludedProperties = new(StringComparer.Ordinal)
    {
        nameof(IsDirty),
        nameof(DialogTitle),
        nameof(IsEditMode),
        nameof(IsAdvancedMode),
        nameof(IsProtocolSelected),
        nameof(ValidationError),
        nameof(DisplayNameError),
        nameof(RemoteServerError),
        nameof(EndpointPortError),
        nameof(LocalPortError),
        nameof(AudioModeError),
        nameof(ColorDepthError),
        nameof(RdpFixedWidthError),
        nameof(RdpFixedHeightError),
        nameof(RdpResizeEnableDelayMsError),
        nameof(GeneralTabErrorCount),
        nameof(NetworkTabErrorCount),
        nameof(OptionsTabErrorCount),
        nameof(FirstInvalidField),
        nameof(AvailableGateways),
        nameof(HasNoGateway),
        nameof(CanCreateGateway),
        nameof(AvailableProjects),
        nameof(ConnectionTypeDisplayName),
        nameof(CanUseWinRmSsl),
        nameof(CanSkipWinRmCertificate),
        nameof(WinRmUseSslHelpText),
        nameof(SessionKindLabel),
        nameof(GatewayToServerLabel),
        nameof(SelectedPostConnectStep),
        nameof(PostConnectFailureOptions),
        nameof(HasLegacyPostConnectCommand),
        nameof(LegacyPostConnectCommandText),
        nameof(LegacyPostConnectDelayText),
        nameof(CanRemoveSelectedPostConnectStep),
        nameof(CanMoveSelectedPostConnectStepUp),
        nameof(CanMoveSelectedPostConnectStepDown),
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_isInitializing) return;
        if (e.PropertyName is null) return;
        if (DirtyExcludedProperties.Contains(e.PropertyName)) return;

        IsDirty = true;
    }

    // --- Validation ---

    [ObservableProperty]
    private string? _validationError;

    // Per-field inline validation errors (populated by Validate)
    [ObservableProperty]
    private string? _displayNameError;

    [ObservableProperty]
    private string? _remoteServerError;

    [ObservableProperty]
    private string? _endpointPortError;

    [ObservableProperty]
    private string? _localPortError;

    [ObservableProperty]
    private string? _audioModeError;

    [ObservableProperty]
    private string? _colorDepthError;

    [ObservableProperty]
    private string? _rdpFixedWidthError;

    [ObservableProperty]
    private string? _rdpFixedHeightError;

    [ObservableProperty]
    private string? _rdpResizeEnableDelayMsError;

    // Tab error counts for badge display
    [ObservableProperty]
    private int _generalTabErrorCount;

    [ObservableProperty]
    private int _networkTabErrorCount;

    [ObservableProperty]
    private int _optionsTabErrorCount;

    // Name of the first property with an error (for focus management by the View)
    [ObservableProperty]
    private string? _firstInvalidField;

    public bool HasGeneralTabErrors => GeneralTabErrorCount > 0;

    public bool HasNetworkTabErrors => NetworkTabErrorCount > 0;

    public bool HasOptionsTabErrors => OptionsTabErrorCount > 0;

    partial void OnGeneralTabErrorCountChanged(int value) => OnPropertyChanged(nameof(HasGeneralTabErrors));

    partial void OnNetworkTabErrorCountChanged(int value) => OnPropertyChanged(nameof(HasNetworkTabErrors));

    partial void OnOptionsTabErrorCountChanged(int value) => OnPropertyChanged(nameof(HasOptionsTabErrors));

    public bool IsRdpConnection => string.Equals(ConnectionType, "RDP", StringComparison.OrdinalIgnoreCase);

    public bool IsAutoResolutionMode =>
        IsRdpConnection && RdpResolutionMode == RdpResolutionMode.Auto;

    public bool IsMultimonAvailable =>
        IsRdpConnection && RdpDisplayCapabilities.IsMultimonAvailable(_screenCount);

    public bool IsAdvancedResolutionExpanded =>
        IsRdpConnection && RdpResolutionMode != RdpResolutionMode.Auto;

    public bool CanSwitchToAuto =>
        IsRdpConnection && RdpResolutionMode != RdpResolutionMode.Auto;

    public bool ShowRdpFixedResolutionFields =>
        IsRdpConnection && RdpResolutionMode == RdpResolutionMode.Fixed;

    public bool ShowRdpInitialSmartSizing =>
        IsRdpConnection && RdpResolutionMode == RdpResolutionMode.Fixed;

    public bool ShowRdpResizeEnableDelay =>
        IsRdpConnection
        && (RdpResolutionMode == RdpResolutionMode.FitWindow
            || RdpResolutionMode == RdpResolutionMode.Fixed);

    public bool ShowRdpMultimonNote =>
        IsRdpConnection && RdpResolutionMode == RdpResolutionMode.Multimon;

    public bool ShowRdpSelectedMonitors =>
        ShowRdpMultimonNote && IsMultimonAvailable;

    public string RdpResizeEnableDelayPlaceholder =>
        string.Format(
            CultureInfo.InvariantCulture,
            L("ServerDialogRdpResizeDelayGlobalDefault"),
            _defaultRdpResizeEnableDelayMs);

    public bool IsSshConnection => string.Equals(ConnectionType, "SSH", StringComparison.OrdinalIgnoreCase);

    public bool IsSftpConnection => string.Equals(ConnectionType, "SFTP", StringComparison.OrdinalIgnoreCase);

    public bool IsCitrixConnection => string.Equals(ConnectionType, "Citrix", StringComparison.OrdinalIgnoreCase);

    public bool IsFtpConnection => string.Equals(ConnectionType, "FTP", StringComparison.OrdinalIgnoreCase);

    public bool IsVncConnection => string.Equals(ConnectionType, "VNC", StringComparison.OrdinalIgnoreCase);

    public bool IsTelnetConnection => string.Equals(ConnectionType, "Telnet", StringComparison.OrdinalIgnoreCase);

    public bool IsWinRmConnection => string.Equals(ConnectionType, "WINRM", StringComparison.OrdinalIgnoreCase);

    public bool IsWinRmCredentialIdentity => WinRmIdentityMode == WinRmIdentityMode.Credential;

    public string ConnectionTypeDisplayName => IsWinRmConnection
        ? _localizer?["ServerDialogProtocolWinRmName"] ?? "WinRM"
        : ConnectionType;

    public bool CanUseWinRmSsl => IsWinRmConnection && !UsesGateway;

    public bool CanSkipWinRmCertificate => IsWinRmConnection && WinRmUseSsl;

    public string WinRmUseSslHelpText => IsWinRmConnection && UsesGateway
        ? L("ServerDialogWinRmUseSslGatewayHint")
        : L("ServerDialogWinRmUseSslHint");

    public bool IsSshFamilyConnection => IsSshConnection || IsSftpConnection;

    public bool RequiresNetworkEndpoint =>
        !string.Equals(ConnectionType, "Local", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(ConnectionType, "Citrix", StringComparison.OrdinalIgnoreCase);

    public bool SupportsGateway => ProtocolCapabilities.SupportsSshGateway(ConnectionType);

    public bool UsesGateway =>
        SupportsGateway
        && !DirectConnection
        && !string.IsNullOrWhiteSpace(SelectedGatewayId);

    public bool CanSelectGateway => SupportsGateway && !DirectConnection;

    /// <summary>
    /// Says why the gateway dropdown is disabled, and says the right thing.
    /// </summary>
    /// <remarks>
    /// Two disjoint causes used to be fused into one message - "Gateway selection requires
    /// SSH protocol" - which was wrong on both counts: the direct-connection checkbox has
    /// nothing to do with the protocol, and gateways were never SSH-only. A screen reader
    /// user asking why the control is dead was told to change something that was already
    /// correct.
    /// </remarks>
    public string GatewayComboHelpText => CanSelectGateway
        ? ""
        : SupportsGateway
            ? L("ServerDialogGatewayDisabledDirectHint")
            : L("ServerDialogGatewayDisabledProtocolHint");

    public bool CanEditTunnelPort => UsesGateway && !UseAutomaticTunnelPort;

    public int EndpointPort
    {
        get => IsRdpConnection ? RemotePort
            : IsWinRmConnection ? WinRmPort
            : IsVncConnection ? VncPort
            : IsFtpConnection ? FtpPort
            : IsTelnetConnection ? RemotePort
            : SshPort;
        set
        {
            if (IsRdpConnection || IsTelnetConnection)
            {
                RemotePort = value;
            }
            else if (IsWinRmConnection)
            {
                WinRmPort = value;
            }
            else if (IsVncConnection)
            {
                VncPort = value;
            }
            else if (IsFtpConnection)
            {
                FtpPort = value;
            }
            else
            {
                SshPort = value;
            }
        }
    }

    public string EndpointPortLabel => IsRdpConnection ? L("ServerDialogPortLabelRdp")
        : IsWinRmConnection ? L("ServerDialogPortLabelWinRm")
        : IsVncConnection ? L("ServerDialogPortLabelVnc")
        : IsFtpConnection ? L("ServerDialogPortLabelFtp")
        : IsTelnetConnection ? L("ServerDialogPortLabelTelnet")
        : L("ServerDialogPortLabelSsh");

    public string EndpointPortHelpText => IsRdpConnection
        ? L("ServerDialogPortHelpRdp")
        : IsWinRmConnection ? L("ServerDialogPortHelpWinRm")
        : IsVncConnection ? L("ServerDialogPortHelpVnc")
        : IsFtpConnection ? L("ServerDialogPortHelpFtp")
        : IsTelnetConnection ? L("ServerDialogPortHelpTelnet")
        : L("ServerDialogPortHelpSsh");

    public string LocalTunnelPortDisplay => UseAutomaticTunnelPort
        ? string.Format(CultureInfo.InvariantCulture, L("ServerDialogTunnelPortAuto"), LocalPort)
        : LocalPort.ToString(CultureInfo.InvariantCulture);

    public string ConnectionPathHeadline => UsesGateway
        ? L("ServerDialogPathHeadlineTunnel")
        : L("ServerDialogPathHeadlineDirect");

    public string GatewayExplanation => UsesGateway
        ? L("ServerDialogGatewayExplainTunnel")
        : L("ServerDialogGatewayExplainDirect");

    public string GatewayRouteText => SelectedGateway?.EffectiveRouteText ?? L("ServerDialogNoGatewaySelected");

    public string SelectedGatewayTitle => SelectedGateway?.EffectiveName ?? L("ServerDialogNoGatewaySelected");

    public string SelectedGatewayEndpoint => SelectedGateway?.EndpointText ?? L("ServerDialogNoSshGateway");

    public string SessionKindLabel => IsRdpConnection ? L("ServerDialogSessionRdp")
        : IsFtpConnection ? L("ServerDialogSessionFtp")
        : IsSftpConnection ? L("ServerDialogSessionSftp")
        : IsWinRmConnection ? L("ServerDialogSessionWinRm")
        : IsVncConnection ? L("ServerDialogSessionVnc")
        : IsTelnetConnection ? L("ServerDialogSessionTelnet")
        : IsCitrixConnection ? L("ServerDialogSessionCitrix")
        : IsLocalConnection ? L("ServerDialogSessionLocal")
        : L("ServerDialogSessionSsh");

    public string SessionModeSummary => IsRdpConnection ? L("ServerDialogModeSummaryRdp")
        : IsFtpConnection ? L("ServerDialogModeSummaryFtp")
        : IsSftpConnection ? L("ServerDialogModeSummarySftp")
        : IsVncConnection ? L("ServerDialogModeSummaryVnc")
        : IsTelnetConnection ? L("ServerDialogModeSummaryTelnet")
        : IsCitrixConnection ? L("ServerDialogModeSummaryCitrix")
        : IsLocalConnection ? L("ServerDialogModeSummaryLocal")
        : L("ServerDialogModeSummarySsh");

    public string TunnelSummary => UsesGateway
        ? string.Format(
            CultureInfo.InvariantCulture,
            L("ServerDialogTunnelSummaryFormat"),
            LocalTunnelPortDisplay,
            GetDestinationHost(),
            EndpointPort,
            SelectedGateway?.EffectiveName ?? L("ServerDialogTunnelSummaryFallbackGw"))
        : L("ServerDialogTunnelSummaryNone");

    public string ClientNodeCaption => UsesGateway
        ? string.Format(CultureInfo.InvariantCulture, L("ServerDialogClientNodeTunnel"), LocalTunnelPortDisplay)
        : L("ServerDialogClientNodeDirect");

    public string GatewayNodeCaption => UsesGateway
        ? string.Format(CultureInfo.InvariantCulture, "{0}", SelectedGateway?.EffectiveName ?? L("ServerDialogGatewayNodeDefault"))
        : L("ServerDialogGatewayNodeUnused");

    public string DestinationNodeCaption => string.IsNullOrWhiteSpace(RemoteServer)
        ? L("ServerDialogDestinationNode")
        : string.Format(CultureInfo.InvariantCulture, "{0}:{1}", RemoteServer, EndpointPort);

    public string ClientToGatewayLabel => UsesGateway ? L("ServerDialogLabelSshTunnel") : L("ServerDialogLabelDirectTransport");

    public string GatewayToServerLabel => SessionKindLabel;

    /// <summary>
    /// Triggers full validation of all annotated properties.
    /// Populates per-field errors, tab error counts, and first invalid field for focus.
    /// </summary>
    private void ClearValidationState()
    {
        ClearErrors();
        DisplayNameError = null;
        RemoteServerError = null;
        EndpointPortError = null;
        LocalPortError = null;
        AudioModeError = null;
        ColorDepthError = null;
        RdpFixedWidthError = null;
        RdpFixedHeightError = null;
        RdpResizeEnableDelayMsError = null;
        GeneralTabErrorCount = 0;
        NetworkTabErrorCount = 0;
        OptionsTabErrorCount = 0;
        FirstInvalidField = null;
        ValidationError = null;
    }

    private void RefreshValidationSummary()
    {
        ValidationError = DisplayNameError ?? RemoteServerError ?? EndpointPortError
            ?? LocalPortError ?? AudioModeError ?? ColorDepthError
            ?? RdpFixedWidthError ?? RdpFixedHeightError ?? RdpResizeEnableDelayMsError;
        GeneralTabErrorCount = (DisplayNameError is not null ? 1 : 0)
            + (RemoteServerError is not null ? 1 : 0)
            + (EndpointPortError is not null ? 1 : 0);
        NetworkTabErrorCount = LocalPortError is not null ? 1 : 0;
        OptionsTabErrorCount = (AudioModeError is not null ? 1 : 0)
            + (ColorDepthError is not null ? 1 : 0)
            + (RdpFixedWidthError is not null ? 1 : 0)
            + (RdpFixedHeightError is not null ? 1 : 0)
            + (RdpResizeEnableDelayMsError is not null ? 1 : 0);
    }

    [RelayCommand]
    private void Validate()
    {
        ValidateAllProperties();

        // Clear annotation errors for fields not relevant to this protocol,
        // so HasErrors stays consistent with the displayed validation state.
        if (!RequiresNetworkEndpoint)
        {
            ClearErrors(nameof(RemoteServer));
            ClearErrors(nameof(RemotePort));
        }
        if (!IsSshFamilyConnection) ClearErrors(nameof(SshPort));
        if (!IsWinRmConnection) ClearErrors(nameof(WinRmPort));
        if (!IsFtpConnection) ClearErrors(nameof(FtpPort));
        if (!IsVncConnection) ClearErrors(nameof(VncPort));
        if (!IsRdpConnection)
        {
            ClearErrors(nameof(RdpAudioMode));
            ClearErrors(nameof(RdpColorDepth));
            ClearErrors(nameof(RdpFixedWidth));
            ClearErrors(nameof(RdpFixedHeight));
            ClearErrors(nameof(RdpResizeEnableDelayMs));
        }
        if (!ShowRdpFixedResolutionFields)
        {
            ClearErrors(nameof(RdpFixedWidth));
            ClearErrors(nameof(RdpFixedHeight));
        }
        if (!ShowRdpResizeEnableDelay)
        {
            ClearErrors(nameof(RdpResizeEnableDelayMs));
        }
        if (!UsesGateway) ClearErrors(nameof(LocalPort));

        // Per-field inline errors (localized, ConnectionType-aware)
        DisplayNameError = GetLocalizedFieldError(nameof(DisplayName));
        RemoteServerError = RequiresNetworkEndpoint ? GetLocalizedFieldError(nameof(RemoteServer)) : null;
        EndpointPortError = RequiresNetworkEndpoint ? GetEndpointPortError() : null;
        LocalPortError = UsesGateway ? GetLocalizedFieldError(nameof(LocalPort)) : null;

        // Custom tunnel port check
        if (LocalPortError is null && UsesGateway && !UseAutomaticTunnelPort && LocalPort <= 0)
        {
            LocalPortError = L("ValidationTunnelPortRequired");
        }

        // Options tab errors (RDP-specific)
        AudioModeError = IsRdpConnection ? GetLocalizedFieldError(nameof(RdpAudioMode)) : null;
        ColorDepthError = IsRdpConnection ? GetLocalizedFieldError(nameof(RdpColorDepth)) : null;
        RdpFixedWidthError = ShowRdpFixedResolutionFields ? GetLocalizedFieldError(nameof(RdpFixedWidth)) : null;
        RdpFixedHeightError = ShowRdpFixedResolutionFields ? GetLocalizedFieldError(nameof(RdpFixedHeight)) : null;
        RdpResizeEnableDelayMsError = ShowRdpResizeEnableDelay
            ? GetLocalizedFieldError(nameof(RdpResizeEnableDelayMs))
            : null;

        // Tab error counts
        GeneralTabErrorCount = (DisplayNameError is not null ? 1 : 0)
            + (RemoteServerError is not null ? 1 : 0)
            + (EndpointPortError is not null ? 1 : 0);
        NetworkTabErrorCount = LocalPortError is not null ? 1 : 0;
        OptionsTabErrorCount = (AudioModeError is not null ? 1 : 0)
            + (ColorDepthError is not null ? 1 : 0)
            + (RdpFixedWidthError is not null ? 1 : 0)
            + (RdpFixedHeightError is not null ? 1 : 0)
            + (RdpResizeEnableDelayMsError is not null ? 1 : 0);

        // First invalid field for auto-focus
        FirstInvalidField = DisplayNameError is not null ? nameof(DisplayName)
            : RemoteServerError is not null ? nameof(RemoteServer)
            : EndpointPortError is not null ? "EndpointPort"
            : LocalPortError is not null ? nameof(LocalPort)
            : AudioModeError is not null ? nameof(RdpAudioMode)
            : ColorDepthError is not null ? nameof(RdpColorDepth)
            : RdpFixedWidthError is not null ? nameof(RdpFixedWidth)
            : RdpFixedHeightError is not null ? nameof(RdpFixedHeight)
            : RdpResizeEnableDelayMsError is not null ? nameof(RdpResizeEnableDelayMs)
            : null;

        // Aggregate summary
        ValidationError = DisplayNameError ?? RemoteServerError ?? EndpointPortError
            ?? LocalPortError ?? AudioModeError ?? ColorDepthError
            ?? RdpFixedWidthError ?? RdpFixedHeightError ?? RdpResizeEnableDelayMsError;
    }

    partial void OnRdpPerformanceFlagsChanged(int value)
    {
        DecomposePerformanceFlags(value);
    }

    partial void OnRdpResolutionModeChanged(RdpResolutionMode value)
    {
        RdpMultiMonitor = value == RdpResolutionMode.Multimon;
        ClearHiddenRdpResolutionErrors();
        RaiseRdpResolutionProfileStateChanged();
        OnPropertyChanged(nameof(IsMultimonModeSelected));
        RefreshValidationSummary();
    }

    /// <summary>
    /// Two-way alias of <c>RdpResolutionMode == Multimon</c> exposed for the
    /// "Enable multi-monitor" toggle in the Display section. Toggling on
    /// switches the mode to <see cref="RdpResolutionMode.Multimon"/>; toggling
    /// off reverts to <see cref="RdpResolutionMode.Auto"/>. The toggle has no
    /// effect when multi-monitor is unavailable on the host.
    /// </summary>
    public bool IsMultimonModeSelected
    {
        get => RdpResolutionMode == RdpResolutionMode.Multimon;
        set
        {
            if (value == IsMultimonModeSelected)
            {
                return;
            }

            if (value && !IsMultimonAvailable)
            {
                OnPropertyChanged(nameof(IsMultimonModeSelected));
                return;
            }

            RdpResolutionMode = value
                ? RdpResolutionMode.Multimon
                : RdpResolutionMode.Auto;
        }
    }

    [RelayCommand]
    private void SwitchToAuto()
    {
        RdpResolutionMode = RdpResolutionMode.Auto;
    }

    /// <summary>
    /// Pre-fills <see cref="RdpFixedWidth"/> and <see cref="RdpFixedHeight"/>
    /// from a "WIDTHxHEIGHT" preset string (e.g. "1920x1080"). Accepts the
    /// regular ASCII <c>x</c> as well as the typographic multiplication sign
    /// <c>×</c>. Invalid input is silently ignored — the user can still
    /// type custom dimensions in the boxes.
    /// </summary>
    [RelayCommand]
    private void ApplyResolutionPreset(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        string[] parts = preset.Split(['x', 'X', '×'], 2);
        if (parts.Length != 2)
        {
            return;
        }

        if (int.TryParse(parts[0].Trim(), out int width)
            && int.TryParse(parts[1].Trim(), out int height)
            && width > 0
            && height > 0)
        {
            RdpFixedWidth = width;
            RdpFixedHeight = height;
        }
    }

    partial void OnRdpFixedWidthChanged(int value)
    {
        if (RdpFixedWidthError is not null)
        {
            ValidateProperty(value, nameof(RdpFixedWidth));
            RdpFixedWidthError = ShowRdpFixedResolutionFields ? GetLocalizedFieldError(nameof(RdpFixedWidth)) : null;
            RefreshValidationSummary();
        }
    }

    partial void OnRdpFixedHeightChanged(int value)
    {
        if (RdpFixedHeightError is not null)
        {
            ValidateProperty(value, nameof(RdpFixedHeight));
            RdpFixedHeightError = ShowRdpFixedResolutionFields ? GetLocalizedFieldError(nameof(RdpFixedHeight)) : null;
            RefreshValidationSummary();
        }
    }

    partial void OnRdpResizeEnableDelayMsChanged(int? value)
    {
        if (RdpResizeEnableDelayMsError is not null)
        {
            ValidateProperty(value, nameof(RdpResizeEnableDelayMs));
            RdpResizeEnableDelayMsError = ShowRdpResizeEnableDelay
                ? GetLocalizedFieldError(nameof(RdpResizeEnableDelayMs))
                : null;
            RefreshValidationSummary();
        }
    }

    private void ClearHiddenRdpResolutionErrors()
    {
        if (!ShowRdpFixedResolutionFields)
        {
            ClearErrors(nameof(RdpFixedWidth));
            ClearErrors(nameof(RdpFixedHeight));
            RdpFixedWidthError = null;
            RdpFixedHeightError = null;
        }

        if (!ShowRdpResizeEnableDelay)
        {
            ClearErrors(nameof(RdpResizeEnableDelayMs));
            RdpResizeEnableDelayMsError = null;
        }
    }

    private void RaiseRdpResolutionProfileStateChanged()
    {
        OnPropertyChanged(nameof(IsAutoResolutionMode));
        OnPropertyChanged(nameof(IsMultimonAvailable));
        OnPropertyChanged(nameof(IsAdvancedResolutionExpanded));
        OnPropertyChanged(nameof(CanSwitchToAuto));
        OnPropertyChanged(nameof(ShowRdpFixedResolutionFields));
        OnPropertyChanged(nameof(ShowRdpInitialSmartSizing));
        OnPropertyChanged(nameof(ShowRdpResizeEnableDelay));
        OnPropertyChanged(nameof(ShowRdpMultimonNote));
        OnPropertyChanged(nameof(ShowRdpSelectedMonitors));
    }

    [RelayCommand]
    private void RefreshMonitors()
    {
        RefreshAvailableMonitors();
    }

    private void RefreshAvailableMonitors(IEnumerable<int>? preferredSelection = null)
    {
        HashSet<int> selectedIndices = preferredSelection is null
            ? AvailableMonitors
                .Where(monitor => monitor.IsSelected)
                .Select(monitor => monitor.Index)
                .ToHashSet()
            : preferredSelection.ToHashSet();

        MonitorInfo[] monitors = _monitorEnumerator.GetMonitors()
            .OrderBy(monitor => monitor.Index)
            .ToArray();

        AvailableMonitors.Clear();
        foreach (MonitorInfo monitor in monitors)
        {
            AvailableMonitors.Add(CreateMonitorChoice(monitor, selectedIndices.Contains(monitor.Index)));
        }

        _screenCount = AvailableMonitors.Count;
        RaiseRdpResolutionProfileStateChanged();
    }

    private MonitorChoiceViewModel CreateMonitorChoice(MonitorInfo monitor, bool isSelected)
    {
        string label = string.Format(
            CultureInfo.CurrentCulture,
            L("ServerDialogMonitorLabelFormat"),
            monitor.Index + 1,
            monitor.Width,
            monitor.Height);

        if (monitor.IsPrimary)
        {
            label += L("ServerDialogMonitorPrimarySuffix");
        }

        if (monitor.Width > 0 && monitor.Height > 0 && monitor.Width < monitor.Height)
        {
            label += L("ServerDialogMonitorVerticalSuffix");
        }

        return new MonitorChoiceViewModel(
            monitor.Index,
            monitor.Width,
            monitor.Height,
            monitor.IsPrimary,
            monitor.DeviceName,
            label,
            isSelected);
    }

    private int ComposePerformanceFlags()
    {
        int flags = 0;

        if (RdpPerfDisableWallpaper) flags |= PerfDisableWallpaperFlag;
        if (RdpPerfDisableDrag) flags |= PerfDisableDragFlag;
        if (RdpPerfDisableAnimations) flags |= PerfDisableAnimationsFlag;
        if (RdpPerfDisableThemes) flags |= PerfDisableThemesFlag;
        if (RdpPerfDisableCursorShadow) flags |= PerfDisableCursorShadowFlag;
        if (RdpPerfEnableFontSmoothing) flags |= PerfEnableFontSmoothingFlag;
        if (RdpPerfEnableComposition) flags |= PerfEnableCompositionFlag;

        return flags;
    }

    private void DecomposePerformanceFlags(int flags)
    {
        RdpPerfDisableWallpaper = (flags & PerfDisableWallpaperFlag) != 0;
        RdpPerfDisableDrag = (flags & PerfDisableDragFlag) != 0;
        RdpPerfDisableAnimations = (flags & PerfDisableAnimationsFlag) != 0;
        RdpPerfDisableThemes = (flags & PerfDisableThemesFlag) != 0;
        RdpPerfDisableCursorShadow = (flags & PerfDisableCursorShadowFlag) != 0;
        RdpPerfEnableFontSmoothing = (flags & PerfEnableFontSmoothingFlag) != 0;
        RdpPerfEnableComposition = (flags & PerfEnableCompositionFlag) != 0;
    }

    /// <summary>
    /// Maps the current ViewModel state to a flat DTO for persistence.
    /// </summary>
    public ServerProfileDto ToDto()
    {
        string? sshKeyPath = string.IsNullOrWhiteSpace(SshKeyPath) ? null : SshKeyPath;
        int snappedRdpFixedWidth = RdpDisplayHelper.SnapToMultipleOf(RdpFixedWidth, 4);
        bool supportsGateway = ProtocolCapabilities.SupportsSshGateway(ConnectionType);

        ServerProfileDto dto = new ServerProfileDto
        {
            DisplayName = DisplayName,
            Origin = Origin,
            ExecutionConfirmed = true,
            RemoteServer = RemoteServer,
            RemotePort = RemotePort,
            LocalPort = LocalPort,
            Group = string.IsNullOrWhiteSpace(Group) ? null : Group,
            ConnectionType = ConnectionType,
            SessionLoggingOverride = SessionLoggingOverride,
            VaultEntryName = string.IsNullOrWhiteSpace(VaultEntryName) ? null : VaultEntryName,
            WinRmPort = WinRmPort,
            WinRmUsername = string.IsNullOrWhiteSpace(WinRmUsername) ? null : WinRmUsername,
            WinRmPasswordEncrypted = string.IsNullOrEmpty(WinRmPassword)
                ? ExistingWinRmPasswordEncrypted
                : Heimdall.Core.Security.CredentialProtector.Protect(WinRmPassword),
            WinRmUseSsl = WinRmUseSsl,
            WinRmSkipCertificateCheck = WinRmUseSsl && WinRmSkipCertificateCheck,
            WinRmIdentityMode = WinRmIdentityMode,
            SshUsername = string.IsNullOrWhiteSpace(SshUsername) ? null : SshUsername,
            SshPort = SshPort,
            SshKeyPath = sshKeyPath,
            SshPasswordEncrypted = string.IsNullOrEmpty(SshPassword)
                ? ExistingSshPasswordEncrypted
                : Heimdall.Core.Security.CredentialProtector.Protect(SshPassword),
            SshKeyPassphraseEncrypted = sshKeyPath is null
                ? null
                : string.IsNullOrEmpty(SshKeyPassphrase)
                    ? ExistingSshKeyPassphraseEncrypted ?? string.Empty
                    : Heimdall.Core.Security.CredentialProtector.Protect(SshKeyPassphrase),
            SshCompression = SshCompression,
            SshX11Forwarding = SshX11Forwarding,
            SshAgentForwarding = SshAgentForwarding,
            SocksProxyPort = SocksProxyPort,
            RemoteBindPort = RemoteBindPort,
            RemoteLocalPort = RemoteLocalPort,
            SshMode = SshMode,
            PostConnectSteps = [.. PostConnectSteps.Select(step => step.ToModel())],
            PostConnectCommand = PostConnectCommand,
            PostConnectDelayMs = PostConnectDelayMs,
            LocalShellExecutable = string.IsNullOrWhiteSpace(LocalShellExecutable) ? null : LocalShellExecutable,
            LocalShellArguments = string.IsNullOrWhiteSpace(LocalShellArguments) ? null : LocalShellArguments,
            LocalShellWorkingDirectory = string.IsNullOrWhiteSpace(LocalShellWorkingDirectory) ? null : LocalShellWorkingDirectory,
            LocalShellElevated = ElevationMode != Core.Models.ElevationMode.None,
            ElevationMode = ElevationMode,
            CitrixStoreFrontUrl = string.IsNullOrWhiteSpace(CitrixStoreFrontUrl) ? null : CitrixStoreFrontUrl,
            CitrixAppName = string.IsNullOrWhiteSpace(CitrixAppName) ? null : CitrixAppName,
            CitrixIcaFilePath = string.IsNullOrWhiteSpace(CitrixIcaFilePath) ? null : CitrixIcaFilePath,
            CitrixSeamlessMode = CitrixSeamlessMode,
            CitrixUseSso = CitrixUseSso,
            FtpPort = FtpPort,
            FtpUsername = string.IsNullOrWhiteSpace(FtpUsername) ? null : FtpUsername,
            FtpPasswordEncrypted = string.IsNullOrEmpty(FtpPassword)
                ? ExistingFtpPasswordEncrypted
                : Heimdall.Core.Security.CredentialProtector.Protect(FtpPassword),
            FtpPassiveMode = FtpPassiveMode,
            FtpUseSsl = FtpUseSsl,
            VncPort = VncPort,
            VncPassword = string.IsNullOrEmpty(VncPassword)
                ? ExistingVncPasswordEncrypted
                : Heimdall.Core.Security.CredentialProtector.Protect(VncPassword),
            VncViewOnly = VncViewOnly,
            TelnetPort = IsTelnetConnection ? RemotePort : 23,
            TelnetUsername = string.IsNullOrWhiteSpace(TelnetUsername) ? null : TelnetUsername,
            TelnetPasswordEncrypted = string.IsNullOrEmpty(TelnetPassword)
                ? ExistingTelnetPasswordEncrypted
                : Heimdall.Core.Security.CredentialProtector.Protect(TelnetPassword),
            RdpUsername = string.IsNullOrWhiteSpace(RdpUsername) ? null : RdpUsername,
            RdpDomain = string.IsNullOrWhiteSpace(RdpDomain) ? null : RdpDomain,
            RdpPasswordEncrypted = string.IsNullOrEmpty(RdpPassword)
                ? ExistingRdpPasswordEncrypted
                : Heimdall.Core.Security.CredentialProtector.Protect(RdpPassword),
            RdpMode = RdpMode,
            RdpUseGlobalDefaults = RdpUseGlobalDefaults,
            RdpAntiIdle = RdpAntiIdle,
            RdpRedirectClipboard = RedirectClipboard,
            RdpRedirectDrives = RedirectDrives,
            RdpRedirectPrinters = RedirectPrinters,
            RdpRedirectComPorts = RdpRedirectComPorts,
            RdpRedirectSmartCards = RdpRedirectSmartCards,
            RdpRedirectWebcam = RdpRedirectWebcam,
            RdpRedirectUsb = RdpRedirectUsb,
            RdpAudioMode = RdpAudioMode,
            RdpAudioCapture = RdpAudioCapture,
            RdpMultiMonitor = RdpResolutionMode == RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [.. AvailableMonitors
                .Where(monitor => monitor.IsSelected)
                .Select(monitor => monitor.Index)],
            RdpDynamicResolution = RdpDynamicResolution,
            RdpResolutionMode = RdpResolutionMode,
            RdpFixedWidth = snappedRdpFixedWidth,
            RdpFixedHeight = RdpFixedHeight,
            RdpInitialSmartSizing = RdpInitialSmartSizing,
            RdpResizeEnableDelayMs = RdpResizeEnableDelayMs,
            RdpNla = RdpNla,
            RdpStrictServerAuthentication = RdpStrictServerAuthentication,
            RdpAspectRatio = RdpAspectRatio,
            RdpColorDepth = RdpColorDepth,
            RdpBitmapCaching = RdpBitmapCaching,
            RdpCompression = RdpCompression,
            RdpHardwareAcceleration = RdpHardwareAcceleration,
            RdpAutoReconnect = RdpAutoReconnect,
            RdpAdminMode = RdpAdminMode,
            RdpFullScreen = RdpFullScreen,
            RdpPerformanceFlags = ComposePerformanceFlags(),
            RdpDisableUdp = RdpDisableUdp,
            RdpGateway = string.IsNullOrWhiteSpace(RdpGateway) ? null : RdpGateway,
            SshGatewayId = !supportsGateway || DirectConnection || string.IsNullOrWhiteSpace(SelectedGatewayId)
                ? null
                : SelectedGatewayId,
            UseDirectConnection = supportsGateway && DirectConnection,
            ProjectId = string.IsNullOrWhiteSpace(SelectedProjectId) ? null : SelectedProjectId,
            Tags = string.IsNullOrWhiteSpace(Tags) ? null : Tags,
            Environment = Environment == "None" ? null : Environment,
            MacAddress = string.IsNullOrWhiteSpace(MacAddress) ? null : MacAddress,
            IsFavorite = IsFavorite
        };

        CarryForwardUneditedFields(dto);
        return dto;
    }

    /// <summary>
    /// Copies the parts of the seed profile this dialog never edits onto the object it returns.
    /// </summary>
    /// <remarks>
    /// <para>The dialog composes a fresh profile and the caller assigns it over the stored record,
    /// so a field the dialog does not write is not left alone: it is replaced by the default. That
    /// silently reset a profile's position in the gateway overview, collapsed its tunnels panel,
    /// discarded any setting written by a newer version of the application, and erased the launch
    /// command line a Citrix profile needs in order to start at all.</para>
    /// <para>Copied out of a faithful clone rather than field by field off the seed, so the
    /// extension data arrives detached from the document the seed parsed it from. The identity is
    /// deliberately not carried: every caller assigns it after this returns, a fresh one when
    /// adding or duplicating and the original one when editing, and quietly inheriting the seed's
    /// identity on a duplicate would produce two profiles claiming to be the same record.</para>
    /// </remarks>
    private void CarryForwardUneditedFields(ServerProfileDto dto)
    {
        if (_seed is null)
        {
            return;
        }

        ServerProfileDto seed = _seed.CloneFaithfully();

        dto.SortOrder = seed.SortOrder;
        dto.TunnelsPanelExpanded = seed.TunnelsPanelExpanded;
        dto.CitrixLaunchCommandLine = seed.CitrixLaunchCommandLine;
        dto.ExtensionData = seed.ExtensionData;
    }

    /// <summary>
    /// Creates a ViewModel pre-populated from an existing DTO (for edit mode).
    /// </summary>
    /// <summary>
    /// The profile this dialog was seeded from, or null when it is composing a new one.
    /// </summary>
    /// <remarks>
    /// Held so that <see cref="ToDto"/> can carry forward the parts of a profile the dialog does
    /// not edit. The caller replaces the stored record wholesale with what the dialog returns, so
    /// anything absent from that object is not merely unedited, it is deleted.
    /// </remarks>
    private ServerProfileDto? _seed;

    public static ServerDialogViewModel FromDto(ServerProfileDto dto)
        => FromDto(dto, monitorEnumerator: null);

    internal static ServerDialogViewModel FromDto(ServerProfileDto dto, IMonitorEnumerator? monitorEnumerator)
    {
        ArgumentNullException.ThrowIfNull(dto);
        PostConnectMigration.Migrate(dto);
        RdpResolutionProfileMigration.Migrate(dto);

        string connectionType = string.IsNullOrWhiteSpace(dto.ConnectionType) ? "RDP" : dto.ConnectionType;
        int suggestedTunnelPort = string.Equals(connectionType, "RDP", StringComparison.OrdinalIgnoreCase)
            ? DefaultPorts.RdpTunnel
            : string.Equals(connectionType, "WINRM", StringComparison.OrdinalIgnoreCase)
                ? DefaultPorts.WinRmTunnel
                : DefaultPorts.SshTunnel;
        int storedLocalPort = dto.LocalPort <= 0 ? suggestedTunnelPort : dto.LocalPort;

        ServerDialogViewModel vm = monitorEnumerator is null
            ? new ServerDialogViewModel()
            : new ServerDialogViewModel(monitorEnumerator);
        vm._seed = dto;
        vm._isInitializing = true;
        vm.IsEditMode = true;
        vm.IsProtocolSelected = true;
        vm.DisplayName = dto.DisplayName;
        vm.Origin = dto.Origin;
        vm.RemoteServer = dto.RemoteServer;
        vm.RemotePort = string.Equals(connectionType, "Telnet", StringComparison.OrdinalIgnoreCase)
            ? (dto.TelnetPort > 0 ? dto.TelnetPort : DefaultPorts.Telnet)
            : dto.RemotePort;
        vm.LocalPort = storedLocalPort;
        vm.UseAutomaticTunnelPort = dto.LocalPort <= 0 || dto.LocalPort == suggestedTunnelPort;
        vm.Group = dto.Group ?? "";
        vm.ConnectionType = connectionType;
        vm.SessionLoggingOverride = dto.SessionLoggingOverride;
        vm.VaultEntryName = dto.VaultEntryName ?? "";
        vm.WinRmPort = dto.HasWinRmPortField && dto.WinRmPort > 0
            ? dto.WinRmPort
            : dto.WinRmUseSsl ? DefaultPorts.WinRmHttps : DefaultPorts.WinRmHttp;
        vm.WinRmUsername = dto.WinRmUsername ?? "";
        vm.ExistingWinRmPasswordEncrypted = dto.WinRmPasswordEncrypted;
        vm.WinRmUseSsl = dto.WinRmUseSsl;
        vm.WinRmSkipCertificateCheck = dto.WinRmUseSsl && dto.WinRmSkipCertificateCheck;
        vm.WinRmIdentityMode = dto.WinRmIdentityMode;
        vm.SshUsername = dto.SshUsername ?? "";
        vm.SshPort = dto.SshPort;
        vm.SshKeyPath = dto.SshKeyPath ?? "";
        vm.SshCompression = dto.SshCompression;
        vm.SshX11Forwarding = dto.SshX11Forwarding;
        vm.SshAgentForwarding = dto.SshAgentForwarding;
        vm.SocksProxyPort = dto.SocksProxyPort;
        vm.RemoteBindPort = dto.RemoteBindPort;
        vm.RemoteLocalPort = dto.RemoteLocalPort;
        vm.SshMode = dto.SshMode;
        vm.PostConnectCommand = dto.PostConnectCommand;
        vm.PostConnectDelayMs = dto.PostConnectDelayMs;
        vm.LoadPostConnectSteps(dto.PostConnectSteps);
        vm.LocalShellExecutable = dto.LocalShellExecutable ?? "powershell.exe";
        vm.LocalShellArguments = dto.LocalShellArguments ?? "";
        vm.LocalShellWorkingDirectory = dto.LocalShellWorkingDirectory ?? "";
        vm.LocalShellElevated = dto.LocalShellElevated;

        // The effective mode, not the stored one. A profile written before the mode existed carries
        // the elevation in the legacy flag with the mode left at None, and the dialog shows only the
        // mode. Seeding the raw value showed None for a profile that does elevate, and saving then
        // wrote that None back over it, so opening the dialog and pressing save silently took the
        // elevation away. This is the same reconciliation the launcher applies.
        vm.ElevationMode = dto.EffectiveElevationMode;
        vm.CitrixStoreFrontUrl = dto.CitrixStoreFrontUrl ?? "";
        vm.CitrixAppName = dto.CitrixAppName ?? "";
        vm.CitrixIcaFilePath = dto.CitrixIcaFilePath ?? "";
        vm.CitrixSeamlessMode = dto.CitrixSeamlessMode;
        vm.CitrixUseSso = dto.CitrixUseSso;
        vm.FtpPort = dto.FtpPort > 0 ? dto.FtpPort : DefaultPorts.Ftp;
        vm.FtpUsername = dto.FtpUsername ?? "";
        vm.ExistingFtpPasswordEncrypted = dto.FtpPasswordEncrypted;
        vm.FtpPassiveMode = dto.FtpPassiveMode;
        vm.FtpUseSsl = dto.FtpUseSsl;
        vm.VncPort = dto.VncPort > 0 ? dto.VncPort : DefaultPorts.Vnc;
        vm.VncViewOnly = dto.VncViewOnly;
        vm.ExistingVncPasswordEncrypted = dto.VncPassword;
        vm.TelnetUsername = dto.TelnetUsername ?? "";
        vm.ExistingTelnetPasswordEncrypted = dto.TelnetPasswordEncrypted;
        vm.RdpUsername = dto.RdpUsername ?? "";
        vm.RdpDomain = dto.RdpDomain ?? "";
        vm.ExistingRdpPasswordEncrypted = dto.RdpPasswordEncrypted;
        vm.ExistingSshPasswordEncrypted = dto.SshPasswordEncrypted;
        vm.ExistingSshKeyPassphraseEncrypted = dto.SshKeyPassphraseEncrypted;
        vm.RdpMode = dto.RdpMode;
        vm.RdpUseGlobalDefaults = dto.RdpUseGlobalDefaults;
        vm.RdpAntiIdle = dto.RdpAntiIdle;
        vm.RedirectClipboard = dto.RdpRedirectClipboard;
        vm.RedirectDrives = dto.RdpRedirectDrives;
        vm.RedirectPrinters = dto.RdpRedirectPrinters;
        vm.RdpRedirectComPorts = dto.RdpRedirectComPorts;
        vm.RdpRedirectSmartCards = dto.RdpRedirectSmartCards;
        vm.RdpRedirectWebcam = dto.RdpRedirectWebcam;
        vm.RdpRedirectUsb = dto.RdpRedirectUsb;
        vm.RdpAudioMode = dto.RdpAudioMode;
        vm.RdpAudioCapture = dto.RdpAudioCapture;
        vm.RdpResolutionMode = dto.RdpResolutionMode;
        vm.RdpFixedWidth = dto.RdpFixedWidth > 0 ? dto.RdpFixedWidth : DefaultRdpFixedWidth;
        vm.RdpFixedHeight = dto.RdpFixedHeight > 0 ? dto.RdpFixedHeight : DefaultRdpFixedHeight;
        vm.RdpInitialSmartSizing = dto.RdpInitialSmartSizing;
        vm.RdpResizeEnableDelayMs = dto.RdpResizeEnableDelayMs;
        vm.RdpMultiMonitor = dto.RdpResolutionMode == RdpResolutionMode.Multimon;
        vm.RefreshAvailableMonitors(dto.RdpSelectedMonitorIndices);
        vm.RdpDynamicResolution = dto.RdpDynamicResolution;
        vm.RdpNla = dto.RdpNla;
        vm.RdpStrictServerAuthentication = dto.RdpStrictServerAuthentication;
        vm.RdpAspectRatio = dto.RdpAspectRatio;
        vm.RdpColorDepth = dto.RdpColorDepth;
        vm.RdpBitmapCaching = dto.RdpBitmapCaching;
        vm.RdpCompression = dto.RdpCompression;
        vm.RdpHardwareAcceleration = dto.RdpHardwareAcceleration;
        vm.RdpAutoReconnect = dto.RdpAutoReconnect;
        vm.RdpAdminMode = dto.RdpAdminMode;
        vm.RdpFullScreen = dto.RdpFullScreen;
        vm.RdpPerformanceFlags = dto.RdpPerformanceFlags;
        vm.DecomposePerformanceFlags(dto.RdpPerformanceFlags);
        vm.RdpDisableUdp = dto.RdpDisableUdp;
        vm.RdpGateway = dto.RdpGateway ?? "";
        vm.DirectConnection = dto.UseDirectConnection;
        vm.SelectedGatewayId = dto.SshGatewayId ?? "";
        vm.SelectedProjectId = dto.ProjectId ?? "";
        vm.Tags = dto.Tags ?? "";
        vm.MacAddress = dto.MacAddress ?? "";
        vm.Environment = dto.Environment ?? "None";
        vm.IsFavorite = dto.IsFavorite;
        vm._isInitializing = false;
        vm.CoerceWinRmSslForGateway();
        vm.RaiseDerivedStateChanged();
        return vm;
    }

    partial void OnConnectionTypeChanged(string value)
    {
        ClearValidationState();

        // In edit mode, preserve the loaded port (FromDto already set the correct value)
        if (!IsEditMode)
        {
            EndpointPort = GetDefaultEndpointPort(value);

            if (UseAutomaticTunnelPort)
            {
                LocalPort = GetSuggestedTunnelPort(value);
            }
        }

        CoerceWinRmSslForGateway();
        RaiseDerivedStateChanged();
        TryApplyRdpDialogAdvancedDefault();
        RefreshAgentChipIfNeeded();
        ResetTestChip();
        RaiseTestCommandCanExecuteChanged();
    }

    private void TryApplyRdpDialogAdvancedDefault()
    {
        if (_hasAppliedRdpDialogAdvancedDefault
            || !_rdpDialogAdvancedDefault.HasValue
            || !ServerDialogAdvancedModePolicy.ShouldApplyRdpDefault(ConnectionType, IsEditMode, IsProtocolSelected))
        {
            return;
        }

        ServerDialogAdvancedModePolicy.AdvancedRdpSnapshot snapshot = new ServerDialogAdvancedModePolicy.AdvancedRdpSnapshot(
            UseGlobalDefaults: RdpUseGlobalDefaults,
            AntiIdle: RdpAntiIdle,
            BitmapCaching: RdpBitmapCaching,
            Compression: RdpCompression,
            AutoReconnect: RdpAutoReconnect,
            AdminMode: RdpAdminMode,
            FullScreen: RdpFullScreen);

        bool resolved = ServerDialogAdvancedModePolicy.ResolveAdvancedDefault(
            _rdpDialogAdvancedDefault.Value,
            IsEditMode,
            snapshot);

        IsApplyingRdpDialogAdvancedDefault = true;
        try
        {
            IsAdvancedMode = resolved;
            _hasAppliedRdpDialogAdvancedDefault = true;
        }
        finally
        {
            IsApplyingRdpDialogAdvancedDefault = false;
        }
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (DisplayNameError is not null)
        {
            ValidateProperty(value, nameof(DisplayName));
            DisplayNameError = GetLocalizedFieldError(nameof(DisplayName));
            RefreshValidationSummary();
        }
    }

    partial void OnRemoteServerChanged(string value)
    {
        if (RemoteServerError is not null)
        {
            ValidateProperty(value, nameof(RemoteServer));
            RemoteServerError = RequiresNetworkEndpoint ? GetLocalizedFieldError(nameof(RemoteServer)) : null;
            RefreshValidationSummary();
        }
        RaiseDerivedStateChanged();
        ResetTestChip();
        RaiseTestCommandCanExecuteChanged();
    }

    partial void OnRemotePortChanged(int value)
    {
        if (EndpointPortError is not null)
        {
            ValidateProperty(value, nameof(RemotePort));
            EndpointPortError = RequiresNetworkEndpoint ? GetEndpointPortError() : null;
            RefreshValidationSummary();
        }
        RaisePortDerivedStateChanged();
        ResetTestChip();
        RaiseTestCommandCanExecuteChanged();
    }

    partial void OnSshPortChanged(int value)
    {
        if (EndpointPortError is not null)
        {
            ValidateProperty(value, nameof(SshPort));
            EndpointPortError = IsSshFamilyConnection ? GetLocalizedFieldError(nameof(SshPort)) : null;
            RefreshValidationSummary();
        }
        RaisePortDerivedStateChanged();
        ResetTestChip();
        RaiseTestCommandCanExecuteChanged();
    }

    partial void OnWinRmPortChanged(int value)
    {
        if (EndpointPortError is not null)
        {
            ValidateProperty(value, nameof(WinRmPort));
            EndpointPortError = IsWinRmConnection ? GetLocalizedFieldError(nameof(WinRmPort)) : null;
            RefreshValidationSummary();
        }
        RaisePortDerivedStateChanged();
    }

    partial void OnVncPortChanged(int value)
    {
        if (EndpointPortError is not null)
        {
            ValidateProperty(value, nameof(VncPort));
            EndpointPortError = IsVncConnection ? GetLocalizedFieldError(nameof(VncPort)) : null;
            RefreshValidationSummary();
        }
        RaisePortDerivedStateChanged();
    }

    partial void OnFtpPortChanged(int value)
    {
        if (EndpointPortError is not null)
        {
            ValidateProperty(value, nameof(FtpPort));
            EndpointPortError = IsFtpConnection ? GetLocalizedFieldError(nameof(FtpPort)) : null;
            RefreshValidationSummary();
        }
        RaisePortDerivedStateChanged();
    }

    partial void OnLocalPortChanged(int value)
    {
        if (LocalPortError is not null)
        {
            ValidateProperty(value, nameof(LocalPort));
            LocalPortError = UsesGateway ? GetLocalizedFieldError(nameof(LocalPort)) : null;
            RefreshValidationSummary();
        }
        RaiseDerivedStateChanged();
    }

    partial void OnUseAutomaticTunnelPortChanged(bool value)
    {
        if (value)
        {
            LocalPort = GetSuggestedTunnelPort(ConnectionType);
        }

        RaiseDerivedStateChanged();
    }

    partial void OnSelectedGatewayIdChanged(string value)
    {
        if (_isInitializing) return;
        if (LocalPortError is not null) { LocalPortError = null; RefreshValidationSummary(); }
        CoerceWinRmSslForGateway();
        RaiseDerivedStateChanged();
    }

    partial void OnDirectConnectionChanged(bool value)
    {
        if (_isInitializing) return;
        if (LocalPortError is not null) { LocalPortError = null; RefreshValidationSummary(); }
        CoerceWinRmSslForGateway();
        RaiseDerivedStateChanged();
    }

    partial void OnAvailableGatewaysChanged(ObservableCollection<GatewayOption> value)
    {
        RaiseDerivedStateChanged();
    }

    private GatewayOption? SelectedGateway =>
        AvailableGateways.FirstOrDefault(gateway =>
            string.Equals(gateway.Id, SelectedGatewayId, StringComparison.Ordinal));

    private int GetDefaultEndpointPort(string connectionType)
    {
        if (string.Equals(connectionType, "RDP", StringComparison.OrdinalIgnoreCase))
            return DefaultPorts.Rdp;
        if (string.Equals(connectionType, "WINRM", StringComparison.OrdinalIgnoreCase))
            return WinRmUseSsl ? DefaultPorts.WinRmHttps : DefaultPorts.WinRmHttp;
        if (string.Equals(connectionType, "VNC", StringComparison.OrdinalIgnoreCase))
            return DefaultPorts.Vnc;
        if (string.Equals(connectionType, "FTP", StringComparison.OrdinalIgnoreCase))
            return DefaultPorts.Ftp;
        if (string.Equals(connectionType, "Telnet", StringComparison.OrdinalIgnoreCase))
            return DefaultPorts.Telnet;
        return DefaultPorts.Ssh;
    }

    private int GetSuggestedTunnelPort(string connectionType)
    {
        if (string.Equals(connectionType, "RDP", StringComparison.OrdinalIgnoreCase))
        {
            return _defaultRdpTunnelPort;
        }

        if (string.Equals(connectionType, "WINRM", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultPorts.WinRmTunnel;
        }

        return _defaultSshTunnelPort;
    }

    private string GetDestinationHost()
    {
        return string.IsNullOrWhiteSpace(RemoteServer) ? L("ServerDialogDestinationNode") : RemoteServer;
    }

    private void RaisePortDerivedStateChanged()
    {
        OnPropertyChanged(nameof(EndpointPort));
        OnPropertyChanged(nameof(DestinationNodeCaption));
        OnPropertyChanged(nameof(TunnelSummary));
    }

    private void RaiseDerivedStateChanged()
    {
        OnPropertyChanged(nameof(IsRdpConnection));
        OnPropertyChanged(nameof(IsSshConnection));
        OnPropertyChanged(nameof(IsSftpConnection));
        OnPropertyChanged(nameof(IsFtpConnection));
        OnPropertyChanged(nameof(IsCitrixConnection));
        OnPropertyChanged(nameof(IsTelnetConnection));
        OnPropertyChanged(nameof(IsWinRmConnection));
        OnPropertyChanged(nameof(IsWinRmCredentialIdentity));
        OnPropertyChanged(nameof(ConnectionTypeDisplayName));
        OnPropertyChanged(nameof(CanUseWinRmSsl));
        OnPropertyChanged(nameof(CanSkipWinRmCertificate));
        OnPropertyChanged(nameof(WinRmUseSslHelpText));
        OnPropertyChanged(nameof(IsLocalConnection));
        OnPropertyChanged(nameof(IsSshFamilyConnection));
        OnPropertyChanged(nameof(SupportsGateway));
        OnPropertyChanged(nameof(UsesGateway));
        OnPropertyChanged(nameof(CanSelectGateway));
        OnPropertyChanged(nameof(HasNoGateway));
        OnPropertyChanged(nameof(CanCreateGateway));
        CreateGatewayCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(GatewayComboHelpText));
        OnPropertyChanged(nameof(CanEditTunnelPort));
        OnPropertyChanged(nameof(EndpointPort));
        OnPropertyChanged(nameof(EndpointPortLabel));
        OnPropertyChanged(nameof(EndpointPortHelpText));
        OnPropertyChanged(nameof(LocalTunnelPortDisplay));
        OnPropertyChanged(nameof(ConnectionPathHeadline));
        OnPropertyChanged(nameof(GatewayExplanation));
        OnPropertyChanged(nameof(GatewayRouteText));
        OnPropertyChanged(nameof(SelectedGatewayTitle));
        OnPropertyChanged(nameof(SelectedGatewayEndpoint));
        OnPropertyChanged(nameof(SessionKindLabel));
        OnPropertyChanged(nameof(SessionModeSummary));
        OnPropertyChanged(nameof(TunnelSummary));
        OnPropertyChanged(nameof(ClientNodeCaption));
        OnPropertyChanged(nameof(GatewayNodeCaption));
        OnPropertyChanged(nameof(DestinationNodeCaption));
        OnPropertyChanged(nameof(ClientToGatewayLabel));
        OnPropertyChanged(nameof(GatewayToServerLabel));
        OnPropertyChanged(nameof(IsVncConnection));
        OnPropertyChanged(nameof(RequiresNetworkEndpoint));
        RaiseRdpResolutionProfileStateChanged();
    }

    private static readonly Dictionary<string, string> ValidationKeyMap = new(StringComparer.Ordinal)
    {
        ["Display name is required."] = "ValidationDisplayNameRequired",
        ["Display name cannot be empty."] = "ValidationDisplayNameEmpty",
        ["Server address is required."] = "ValidationServerAddressRequired",
        ["Server address cannot be empty."] = "ValidationServerAddressEmpty",
        ["Port must be between 1 and 65535."] = "ValidationPortRange",
        ["Local tunnel port must be between 1 and 65535."] = "ValidationLocalPortRange",
        ["SSH port must be between 1 and 65535."] = "ValidationSshPortRange",
        ["WinRM port must be between 1 and 65535."] = "ValidationWinRmPortRange",
        ["Audio mode must be 0 (disabled), 1 (local), or 2 (remote)."] = "ValidationAudioMode",
        ["Color depth must be between 8 and 32."] = "ValidationColorDepth",
        ["RDP fixed width must be between 200 and 7680."] = "ValidationRdpFixedWidthRange",
        ["RDP fixed height must be between 200 and 4320."] = "ValidationRdpFixedHeightRange",
        ["RDP resize delay must be inherited, zero, or between 1000 and 60000 ms."] = "ValidationRdpResizeEnableDelayRange",
        ["FTP port must be between 1 and 65535."] = "ValidationFtpPortRange",
        ["VNC port must be between 1 and 65535."] = "ValidationVncPortRange",
    };

    public static System.ComponentModel.DataAnnotations.ValidationResult? ValidateRdpResizeEnableDelayMsValue(
        int? value,
        ValidationContext context)
    {
        _ = context;

        // Zero explicitly disables the resize lockout; 1..999 is too short to be meaningful.
        if (!value.HasValue || value.Value == 0 || value.Value is >= 1000 and <= 60000)
        {
            return System.ComponentModel.DataAnnotations.ValidationResult.Success;
        }

        return new System.ComponentModel.DataAnnotations.ValidationResult(
            "RDP resize delay must be inherited, zero, or between 1000 and 60000 ms.");
    }

    private string? GetEndpointPortError()
    {
        if (IsRdpConnection || IsTelnetConnection) return GetLocalizedFieldError(nameof(RemotePort));
        if (IsWinRmConnection) return GetLocalizedFieldError(nameof(WinRmPort));
        if (IsFtpConnection) return GetLocalizedFieldError(nameof(FtpPort));
        if (IsVncConnection) return GetLocalizedFieldError(nameof(VncPort));
        if (IsSshFamilyConnection) return GetLocalizedFieldError(nameof(SshPort));
        return null;
    }

    private string? GetLocalizedFieldError(string propertyName)
    {
        System.ComponentModel.DataAnnotations.ValidationResult? error = GetErrors(propertyName)
            .OfType<System.ComponentModel.DataAnnotations.ValidationResult>()
            .FirstOrDefault();

        string? message = error?.ErrorMessage;
        if (message is not null && Localizer is not null
            && ValidationKeyMap.TryGetValue(message, out string? key))
        {
            return Localizer[key];
        }

        return message;
    }
}

/// <summary>
/// Represents an SSH gateway option in the dialog's gateway dropdown.
/// Additional metadata is carried so the UX can explain the route.
/// </summary>
public sealed record GatewayOption(
    string Id,
    string DisplayText,
    string Name = "",
    string Host = "",
    int Port = 22,
    string RouteText = "")
{
    public string EffectiveName => string.IsNullOrWhiteSpace(Name) ? DisplayText : Name;

    public string EndpointText => string.IsNullOrWhiteSpace(Host)
        ? DisplayText
        : string.Format(CultureInfo.InvariantCulture, "{0}:{1}", Host, Port);

    public string EffectiveRouteText => string.IsNullOrWhiteSpace(RouteText) ? DisplayText : RouteText;
}

/// <summary>
/// Represents a project option in the server dialog's project dropdown.
/// </summary>
public sealed record ProjectOption(string Id, string Name, string Color);

/// <summary>
/// Immutable result returned by the server dialog on close.
/// </summary>
public sealed record ServerDialogResult(ServerProfileDto Server, bool Saved);
