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
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using PostureRules = Heimdall.App.ViewModels.Settings.SecurityPosture;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The security posture card at the top of the Security tab.
/// </summary>
/// <remarks>
/// The card reads the pending values, so it answers "what will I have if I save now", and it marks
/// each line whose state differs from what is on disk. The decision of what is risky is
/// <see cref="PostureRules"/>; this part only words it.
/// </remarks>
public partial class SettingsViewModel
{
    /// <summary>The panel properties the card is computed from.</summary>
    private static readonly HashSet<string> SecurityPosturePropertyNames = new(StringComparer.Ordinal)
    {
        nameof(RdpDefaultNla),
        nameof(RdpDefaultStrictServerAuthentication),
        nameof(FileShareEnableTftp),
        nameof(SessionLoggingEnabled),
        nameof(PowerShellExecutionPolicy),
        nameof(IsVaultEnabled),
        nameof(AutoLockIdleMinutes),
        nameof(DisconnectOnLock),
        nameof(RequireCredentialGuard),
        nameof(RequireWindowsHelloOnConnect),
        nameof(UpdateCheckEnabled),
        nameof(SyncKnownHostsAtStartup),
    };

    /// <summary>The full stop a warning sentence ends with.</summary>
    private const char SentenceEnd = '.';

    private SecurityPostureLines? _securityPosture;

    /// <summary>The card's lines, looked up by key from the markup.</summary>
    public SecurityPostureLines SecurityPosture => _securityPosture ??= CreateSecurityPostureLines();

    /// <summary>
    /// The overall line: how many lines need attention, and whether unsaved changes are included.
    /// </summary>
    /// <remarks>Announced through a live region when it changes.</remarks>
    [ObservableProperty]
    private string _securityPostureSummary = string.Empty;

    /// <summary>How many lines of the card need attention.</summary>
    [ObservableProperty]
    private int _securityPostureRiskCount;

    /// <summary>True when at least one line needs attention.</summary>
    [ObservableProperty]
    private bool _hasSecurityPostureRisk;

    /// <summary>
    /// Raised when a "Go to setting" link is followed, with the x:Name of the control to show.
    /// </summary>
    public event Action<string>? SettingNavigationRequested;

    private SecurityPostureLines CreateSecurityPostureLines()
        => new(Enum.GetValues<SecurityPostureKey>()
            .Select(key => new SecurityPostureLine(key, target => SettingNavigationRequested?.Invoke(target))));

    private SecurityPostureInputs PendingPostureInputs() => new(
        RdpDefaultNla,
        RdpDefaultStrictServerAuthentication,
        FileShareEnableTftp,
        SessionLoggingEnabled,
        PowerShellExecutionPolicy,
        IsVaultEnabled,
        AutoLockIdleMinutes,
        DisconnectOnLock,
        RequireCredentialGuard,
        RequireWindowsHelloOnConnect,
        UpdateCheckEnabled,
        SyncKnownHostsAtStartup);

    private static SecurityPostureInputs PostureInputsOf(AppSettings settings) => new(
        settings.RdpDefaultNla,
        settings.RdpDefaultStrictServerAuthentication,
        settings.FileShareEnableTftp,
        settings.SessionLoggingEnabled,
        settings.PowerShellExecutionPolicy,
        settings.VaultEnabled,
        settings.AutoLockIdleMinutes,
        settings.DisconnectOnLock,
        settings.RequireCredentialGuard,
        settings.RequireWindowsHelloOnConnect,
        settings.UpdateCheckEnabled,
        settings.SyncKnownHostsAtStartup);

    /// <summary>Recomputes every line and the overall line from the pending and saved values.</summary>
    private void RefreshSecurityPosture()
    {
        SecurityPostureInputs pending = PendingPostureInputs();

        // The vault is never pending: it is written the moment it is turned on or off, so the
        // saved side takes the panel's value and the vault never reads as unsaved.
        SecurityPostureInputs onDiskInputs = _savedSettings is null ? pending : PostureInputsOf(_savedSettings);
        SecurityPostureInputs saved = onDiskInputs with { VaultEnabled = pending.VaultEnabled };

        IReadOnlyList<SecurityPostureItem> items = PostureRules.Evaluate(pending);
        Dictionary<SecurityPostureKey, SecurityPostureItem> savedItems = PostureRules
            .Evaluate(saved)
            .ToDictionary(item => item.Key);

        bool anyUnsaved = false;
        foreach (SecurityPostureItem item in items)
        {
            SecurityPostureItem onDisk = savedItems[item.Key];
            bool unsaved = onDisk.State != item.State
                || !string.Equals(onDisk.Detail, item.Detail, StringComparison.Ordinal);
            anyUnsaved |= unsaved;
            ApplyPostureItem(SecurityPosture.Get(item.Key), item, unsaved);
        }

        int risky = PostureRules.CountRisky(items);
        SecurityPostureRiskCount = risky;
        HasSecurityPostureRisk = risky > 0;

        string count = risky == 0
            ? _localizer["SecurityPostureSummaryNone"]
            : _localizer.FormatCount(risky, "SecurityPostureSummaryOne", "SecurityPostureSummary", risky);
        SecurityPostureSummary = anyUnsaved
            ? _localizer.Format("SecurityPostureSummaryWithUnsaved", count)
            : count;
    }

    private void ApplyPostureItem(SecurityPostureLine line, SecurityPostureItem item, bool unsaved)
    {
        string label = _localizer[PostureLabelKey(item.Key)];
        string state = PostureStateText(item);
        string warning = item.IsRisky ? _localizer[PostureWarningKey(item.Key)] : string.Empty;

        string text = _localizer.Format("SecurityPostureLineText", label, state);
        string accessible = text;
        if (item.IsRisky)
        {
            accessible = _localizer.Format("SecurityPostureLineAccessibleRisky", accessible, warning);
        }

        if (unsaved)
        {
            // A warning ends its own sentence; the unsaved note starts the next one.
            accessible = _localizer.Format("SecurityPostureLineAccessibleUnsaved", accessible.TrimEnd(SentenceEnd));
        }

        line.Label = label;
        line.StateText = state;
        line.Text = text;
        line.IsRisky = item.IsRisky;
        line.WarningText = warning;
        line.IsUnsaved = unsaved;
        line.AccessibleName = accessible;
        line.GoToAccessibleName = _localizer.Format("SecurityPostureGoToAccessible", label);
        line.TargetSettingId = item.TargetSettingId;
    }

    private string PostureStateText(SecurityPostureItem item) => item.State switch
    {
        SecurityPostureState.On => _localizer["SettingsValueOn"],
        SecurityPostureState.Off => _localizer["SettingsValueOff"],
        SecurityPostureState.Required => _localizer["SecurityPostureStateRequired"],
        SecurityPostureState.NotRequired => _localizer["SecurityPostureStateNotRequired"],
        SecurityPostureState.Enabled => _localizer["SecurityPostureStateEnabled"],
        SecurityPostureState.Disabled => _localizer["SecurityPostureStateDisabled"],
        SecurityPostureState.AfterMinutes => _localizer.FormatCount(
            int.TryParse(item.Detail, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int minutes) ? minutes : 0,
            "SecurityPostureStateAfterMinutesOne",
            "SecurityPostureStateAfterMinutes",
            item.Detail ?? string.Empty),
        SecurityPostureState.Never => _localizer["SecurityPostureStateNever"],
        SecurityPostureState.RequiresVault => _localizer["SecurityPostureStateRequiresVault"],
        SecurityPostureState.Policy => PowerShellPolicyText(item.Detail),
        _ => string.Empty,
    };

    /// <summary>The policy name as the Terminal tab's list words it.</summary>
    private string PowerShellPolicyText(string? policy) => policy switch
    {
        "Bypass" => _localizer["PsPolicyBypass"],
        "RemoteSigned" => _localizer["PsPolicyRemoteSigned"],
        "Unrestricted" => _localizer["PsPolicyUnrestricted"],
        "AllSigned" => _localizer["PsPolicyAllSigned"],
        "Default" => _localizer["PsPolicyDefault"],
        _ => policy ?? string.Empty,
    };

    private static string PostureLabelKey(SecurityPostureKey key) => key switch
    {
        SecurityPostureKey.RdpNla => "SecurityPostureLabelRdpNla",
        SecurityPostureKey.RdpStrictServerAuthentication => "SecurityPostureLabelRdpStrictServerAuth",
        SecurityPostureKey.TftpSharing => "SecurityPostureLabelTftp",
        SecurityPostureKey.SessionTranscripts => "SecurityPostureLabelTranscripts",
        SecurityPostureKey.PowerShellExecutionPolicy => "SecurityPostureLabelPsPolicy",
        SecurityPostureKey.Vault => "SecurityPostureLabelVault",
        SecurityPostureKey.AutoLock => "SecurityPostureLabelAutoLock",
        SecurityPostureKey.DisconnectOnLock => "SecurityPostureLabelDisconnectOnLock",
        SecurityPostureKey.CredentialGuard => "SecurityPostureLabelCredentialGuard",
        SecurityPostureKey.WindowsHelloOnConnect => "SecurityPostureLabelWindowsHello",
        SecurityPostureKey.UpdateChecks => "SecurityPostureLabelUpdateChecks",
        SecurityPostureKey.KnownHostsSync => "SecurityPostureLabelKnownHostsSync",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    /// <summary>The warning a line shows when its state is risky.</summary>
    /// <remarks>Only the keys whose state can be risky have one; the others never ask for it.</remarks>
    private static string PostureWarningKey(SecurityPostureKey key) => key switch
    {
        SecurityPostureKey.RdpNla => "SecurityPostureWarningRdpNla",
        SecurityPostureKey.TftpSharing => "SecurityPostureWarningTftp",
        SecurityPostureKey.SessionTranscripts => "SecurityPostureWarningTranscripts",
        SecurityPostureKey.PowerShellExecutionPolicy => "SecurityPostureWarningPsPolicy",
        SecurityPostureKey.AutoLock => "SecurityPostureWarningAutoLock",
        SecurityPostureKey.UpdateChecks => "SecurityPostureWarningUpdateChecks",
        SecurityPostureKey.KnownHostsSync => "SecurityPostureWarningKnownHostsSync",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };
}
