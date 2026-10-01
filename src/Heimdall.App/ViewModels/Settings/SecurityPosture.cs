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

namespace Heimdall.App.ViewModels.Settings;

/// <summary>One security-relevant choice the posture card summarises.</summary>
public enum SecurityPostureKey
{
    RdpNla,
    RdpStrictServerAuthentication,
    TftpSharing,
    SessionTranscripts,
    PowerShellExecutionPolicy,
    Vault,
    AutoLock,
    DisconnectOnLock,
    CredentialGuard,
    WindowsHelloOnConnect,
    UpdateChecks,
    KnownHostsSync,
}

/// <summary>The state a posture line reports, worded by the view model.</summary>
public enum SecurityPostureState
{
    On,
    Off,
    Required,
    NotRequired,
    Enabled,
    Disabled,
    AfterMinutes,
    Never,
    RequiresVault,
    Policy,
}

/// <summary>The security-relevant values the posture card is computed from.</summary>
/// <remarks>
/// The panel's pending values, or the values on disk: the card is evaluated twice, and a line whose
/// two evaluations differ is a change that is not saved yet.
/// </remarks>
public sealed record SecurityPostureInputs(
    bool RdpNla,
    bool RdpStrictServerAuthentication,
    bool TftpSharing,
    bool SessionTranscripts,
    string PowerShellExecutionPolicy,
    bool VaultEnabled,
    int AutoLockIdleMinutes,
    bool DisconnectOnLock,
    bool RequireCredentialGuard,
    bool RequireWindowsHelloOnConnect,
    bool UpdateChecks,
    bool KnownHostsSync);

/// <summary>
/// One line of the posture card: what it is about, the state it is in, whether that state is the
/// documented insecure choice, and the settings control "Go to setting" lands on.
/// </summary>
/// <param name="Key">The choice the line is about.</param>
/// <param name="State">The state of the choice.</param>
/// <param name="IsRisky">True when the state is the documented insecure choice.</param>
/// <param name="TargetSettingId">The x:Name of the settings control the line jumps to.</param>
/// <param name="Detail">The value a state carries: a policy name or a number of minutes.</param>
public sealed record SecurityPostureItem(
    SecurityPostureKey Key,
    SecurityPostureState State,
    bool IsRisky,
    string TargetSettingId,
    string? Detail = null);

/// <summary>
/// Decides, for every security-relevant setting, its state and whether that state is risky.
/// </summary>
/// <remarks>
/// <para>The rule lives here, once, and not in the markup: the card, its accessible names, its
/// count and its tests all read the same decision.</para>
/// <para>Risky means the documented insecure choice and nothing more. NLA off sends the password
/// to a server that has not authenticated itself (SECURITY.md, RDP server certificate trust). TFTP
/// answers anyone on the network without a password, and transcripts keep everything typed into
/// every session; both already ask for confirmation when they are turned on. Bypass and
/// Unrestricted switch off the PowerShell script signing check for the shells Heimdall opens. An
/// enabled vault with no idle lock stays unlocked for as long as Heimdall runs. Without update
/// checks a security release goes unnoticed. The known_hosts sync adds, at every start and without
/// a prompt, any host key another program wrote into that file.</para>
/// <para>Strict server authentication off is not risky: it is the Windows default, which warns
/// and lets the user decide. The vault, Credential Guard, Windows Hello and disconnect on lock are
/// hardening a user opts into; their absence is reported, not flagged.</para>
/// <para>Two settings the audit asked about do not exist and are not invented here: there is no
/// global WinRM "skip certificate check" (it is per session), and no SSH host key policy (an unknown
/// or changed key always asks, and a changed key defaults to Reject).</para>
/// </remarks>
public static class SecurityPosture
{
    /// <summary>The PowerShell policies that turn the script signing check off.</summary>
    private static readonly HashSet<string> RiskyExecutionPolicies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bypass",
        "Unrestricted",
    };

    /// <summary>The control that turns the vault on, which is also where the lock settings start.</summary>
    internal const string VaultEnableTarget = "Mw_SettingsVaultEnableBtn";

    /// <summary>The control that manages an enabled vault.</summary>
    internal const string VaultChangeTarget = "Mw_SettingsVaultChangeBtn";

    /// <summary>Every target a line can name, so a guard can check each one exists.</summary>
    public static IReadOnlyList<string> AllTargetSettingIds { get; } =
    [
        "Mw_SettingsRdpNla",
        "Mw_SettingsRdpStrictServerAuth",
        "Mw_SettingsEnableTftpShareCheckBox",
        "Mw_SettingsSessionLoggingEnabled",
        "Mw_SettingsPsExecutionPolicy",
        VaultEnableTarget,
        VaultChangeTarget,
        "Mw_SettingsAutoLockMinutes",
        "Mw_SettingsDisconnectOnLock",
        "Mw_SettingsCredGuard",
        "Mw_SettingsRequireWindowsHello",
        "Mw_SettingsUpdateCheckEnabled",
        "Mw_SettingsSyncKnownHostsAtStartup",
    ];

    /// <summary>Evaluates every line of the card, in the order the card shows them.</summary>
    public static IReadOnlyList<SecurityPostureItem> Evaluate(SecurityPostureInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        string vaultTarget = inputs.VaultEnabled ? VaultChangeTarget : VaultEnableTarget;

        return
        [
            OnOff(SecurityPostureKey.RdpNla, inputs.RdpNla, riskyWhenOn: false, riskyWhenOff: true, "Mw_SettingsRdpNla"),
            OnOff(
                SecurityPostureKey.RdpStrictServerAuthentication,
                inputs.RdpStrictServerAuthentication,
                riskyWhenOn: false,
                riskyWhenOff: false,
                "Mw_SettingsRdpStrictServerAuth"),
            OnOff(SecurityPostureKey.TftpSharing, inputs.TftpSharing, riskyWhenOn: true, riskyWhenOff: false, "Mw_SettingsEnableTftpShareCheckBox"),
            OnOff(SecurityPostureKey.SessionTranscripts, inputs.SessionTranscripts, riskyWhenOn: true, riskyWhenOff: false, "Mw_SettingsSessionLoggingEnabled"),
            new SecurityPostureItem(
                SecurityPostureKey.PowerShellExecutionPolicy,
                SecurityPostureState.Policy,
                RiskyExecutionPolicies.Contains(inputs.PowerShellExecutionPolicy ?? string.Empty),
                "Mw_SettingsPsExecutionPolicy",
                inputs.PowerShellExecutionPolicy),
            new SecurityPostureItem(
                SecurityPostureKey.Vault,
                inputs.VaultEnabled ? SecurityPostureState.Enabled : SecurityPostureState.Disabled,
                IsRisky: false,
                vaultTarget),
            AutoLock(inputs),
            new SecurityPostureItem(
                SecurityPostureKey.DisconnectOnLock,
                !inputs.VaultEnabled
                    ? SecurityPostureState.RequiresVault
                    : inputs.DisconnectOnLock ? SecurityPostureState.On : SecurityPostureState.Off,
                IsRisky: false,
                inputs.VaultEnabled ? "Mw_SettingsDisconnectOnLock" : vaultTarget),
            new SecurityPostureItem(
                SecurityPostureKey.CredentialGuard,
                inputs.RequireCredentialGuard ? SecurityPostureState.Required : SecurityPostureState.NotRequired,
                IsRisky: false,
                "Mw_SettingsCredGuard"),
            new SecurityPostureItem(
                SecurityPostureKey.WindowsHelloOnConnect,
                inputs.RequireWindowsHelloOnConnect ? SecurityPostureState.Required : SecurityPostureState.NotRequired,
                IsRisky: false,
                "Mw_SettingsRequireWindowsHello"),
            OnOff(SecurityPostureKey.UpdateChecks, inputs.UpdateChecks, riskyWhenOn: false, riskyWhenOff: true, "Mw_SettingsUpdateCheckEnabled"),
            OnOff(SecurityPostureKey.KnownHostsSync, inputs.KnownHostsSync, riskyWhenOn: true, riskyWhenOff: false, "Mw_SettingsSyncKnownHostsAtStartup"),
        ];
    }

    /// <summary>How many lines need attention.</summary>
    public static int CountRisky(IEnumerable<SecurityPostureItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count(item => item.IsRisky);
    }

    private static SecurityPostureItem OnOff(
        SecurityPostureKey key,
        bool value,
        bool riskyWhenOn,
        bool riskyWhenOff,
        string target)
        => new(
            key,
            value ? SecurityPostureState.On : SecurityPostureState.Off,
            value ? riskyWhenOn : riskyWhenOff,
            target);

    /// <summary>
    /// The idle lock only exists with the vault: without it there is nothing to lock, so the line
    /// says what turns it on and points there instead of at a disabled box.
    /// </summary>
    private static SecurityPostureItem AutoLock(SecurityPostureInputs inputs)
    {
        if (!inputs.VaultEnabled)
        {
            return new SecurityPostureItem(
                SecurityPostureKey.AutoLock,
                SecurityPostureState.RequiresVault,
                IsRisky: false,
                VaultEnableTarget);
        }

        return inputs.AutoLockIdleMinutes <= 0
            ? new SecurityPostureItem(
                SecurityPostureKey.AutoLock,
                SecurityPostureState.Never,
                IsRisky: true,
                "Mw_SettingsAutoLockMinutes")
            : new SecurityPostureItem(
                SecurityPostureKey.AutoLock,
                SecurityPostureState.AfterMinutes,
                IsRisky: false,
                "Mw_SettingsAutoLockMinutes",
                inputs.AutoLockIdleMinutes.ToString(CultureInfo.InvariantCulture));
    }
}
