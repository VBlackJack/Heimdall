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

using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels.Settings;

/// <summary>
/// Where a setting appears in the settings panel, as locale keys: the label the panel shows next
/// to its control, the tab it sits on, and the sub-tab or section inside that tab.
/// </summary>
/// <param name="LabelKey">The key of the label the panel shows for the setting.</param>
/// <param name="TabKey">The key of the settings tab the setting sits on.</param>
/// <param name="AreaKey">The key of the sub-tab, or of the section when the tab has none.</param>
internal sealed record SettingsPanelPlace(string LabelKey, string TabKey, string AreaKey);

/// <summary>
/// The panel place of every setting a settings file carries, keyed by its
/// <see cref="AppSettings"/> property name.
/// </summary>
/// <remarks>
/// The keys are the ones the settings panel already binds in MainWindow.xaml, so a setting reads
/// the same in the import preview as on screen and no wording is written twice. A setting the
/// panel starts to edit becomes transferable at once (see <see cref="SettingsTransfer"/>); the
/// test that every transferable setting has an entry here keeps it from reaching the preview
/// under its property name.
/// </remarks>
internal static class SettingsLabelCatalog
{
    private static readonly Dictionary<string, SettingsPanelPlace> Places = new(StringComparer.Ordinal)
    {
        [nameof(AppSettings.AccentTint)] = new("SettingsLabelAccent", "SettingsTabGeneral", "SettingsSectionAppearance"),
        [nameof(AppSettings.CollapseTunnelsPanelByDefault)] = new("SettingsLabelCollapseTunnelsPanelByDefault", "SettingsTabGeneral", "SettingsSectionAppearance"),
        [nameof(AppSettings.DefaultLocale)] = new("SettingsLabelLanguage", "SettingsTabGeneral", "SettingsSectionAppearance"),
        [nameof(AppSettings.DefaultTheme)] = new("SettingsLabelTheme", "SettingsTabGeneral", "SettingsSectionAppearance"),
        [nameof(AppSettings.MaxEmbeddedSessions)] = new("SettingsLabelMaxEmbeddedSessions", "SettingsTabGeneral", "SettingsSectionAppearance"),
        [nameof(AppSettings.PreventSleepDuringSession)] = new("SettingsLabelPreventSleep", "SettingsTabGeneral", "SettingsSectionAppearance"),
        [nameof(AppSettings.UpdateCheckEnabled)] = new("SettingsLabelUpdateCheckEnabled", "SettingsTabGeneral", "SettingsSectionUpdates"),
        [nameof(AppSettings.UpdateCheckIntervalHours)] = new("SettingsLabelUpdateInterval", "SettingsTabGeneral", "SettingsSectionUpdates"),
        [nameof(AppSettings.PowerShellExecutionPolicy)] = new("SettingsPsExecutionPolicy", "SettingsTabTerminal", "SettingsSectionTerminal"),
        [nameof(AppSettings.TerminalColorScheme)] = new("SettingsLabelTerminalColorScheme", "SettingsTabTerminal", "SettingsSectionTerminal"),
        [nameof(AppSettings.TerminalFontFamily)] = new("SettingsLabelTerminalFont", "SettingsTabTerminal", "SettingsSectionTerminal"),
        [nameof(AppSettings.TerminalFontSize)] = new("SettingsLabelTerminalFontSize", "SettingsTabTerminal", "SettingsSectionTerminal"),
        [nameof(AppSettings.PlinkPath)] = new("SettingsLabelPlinkPath", "SettingsTabSshSftp", "SettingsSshSubTabConnection"),
        [nameof(AppSettings.PuttyPath)] = new("SettingsLabelPuttyPath", "SettingsTabSshSftp", "SettingsSshSubTabConnection"),
        [nameof(AppSettings.SshAgentPreference)] = new("SettingsSshAgentPreferenceLabel", "SettingsTabSshSftp", "SettingsSshSubTabConnection"),
        [nameof(AppSettings.SshDefaultMode)] = new("SettingsLabelSshMode", "SettingsTabSshSftp", "SettingsSshSubTabConnection"),
        [nameof(AppSettings.SyncKnownHostsAtStartup)] = new("SettingsLabelSyncKnownHostsAtStartup", "SettingsTabSshSftp", "SettingsSshSubTabHostKeys"),
        [nameof(AppSettings.AntiIdleIntervalSeconds)] = new("SettingsLabelAntiIdleInterval", "SettingsTabSshSftp", "SettingsSshSubTabSession"),
        [nameof(AppSettings.SshAutoReconnect)] = new("SettingsSshAutoReconnectEnable", "SettingsTabSshSftp", "SettingsSshSubTabSession"),
        [nameof(AppSettings.SshAutoReconnectAttempts)] = new("SettingsSshAutoReconnectMaxAttempts", "SettingsTabSshSftp", "SettingsSshSubTabSession"),
        [nameof(AppSettings.SshKeepAliveIntervalSeconds)] = new("SettingsLabelSshKeepAliveInterval", "SettingsTabSshSftp", "SettingsSshSubTabSession"),
        [nameof(AppSettings.SshTmoutResetIntervalSeconds)] = new("SettingsLabelSshTmoutReset", "SettingsTabSshSftp", "SettingsSshSubTabSession"),
        [nameof(AppSettings.SftpAutoOpenOnSsh)] = new("SettingsLabelAutoOpenSftp", "SettingsTabSshSftp", "SettingsSshSubTabSftpX11"),
        [nameof(AppSettings.SftpBrowserEnabled)] = new("SettingsLabelSftpBrowserEnabled", "SettingsTabSshSftp", "SettingsSshSubTabSftpX11"),
        [nameof(AppSettings.SftpFollowSshDirectory)] = new("SettingsLabelSftpFollowSshDirectory", "SettingsTabSshSftp", "SettingsSshSubTabSftpX11"),
        [nameof(AppSettings.X11AutoStart)] = new("SettingsLabelX11AutoStart", "SettingsTabSshSftp", "SettingsSshSubTabSftpX11"),
        [nameof(AppSettings.X11ServerPath)] = new("SettingsLabelX11ServerPath", "SettingsTabSshSftp", "SettingsSshSubTabSftpX11"),
        [nameof(AppSettings.RdpArtifactCleanupDelayMs)] = new("SettingsLabelRdpArtifactCleanupDelay", "SettingsTabRdp", "SettingsRdpSubTabBehavior"),
        [nameof(AppSettings.RdpAutoReconnectMaxAttempts)] = new("SettingsLabelRdpAutoReconnectMaxAttempts", "SettingsTabRdp", "SettingsRdpSubTabBehavior"),
        [nameof(AppSettings.RdpCredentialAutofillTimeoutMs)] = new("SettingsLabelRdpCredentialAutofillTimeout", "SettingsTabRdp", "SettingsRdpSubTabBehavior"),
        [nameof(AppSettings.RdpDialogAdvancedDefault)] = new("SettingsLabelRdpDialogAdvancedDefault", "SettingsTabRdp", "SettingsRdpSubTabBehavior"),
        [nameof(AppSettings.RdpKeepAliveIntervalMs)] = new("SettingsLabelRdpKeepAliveInterval", "SettingsTabRdp", "SettingsRdpSubTabBehavior"),
        [nameof(AppSettings.RdpResizeEnableDelayMs)] = new("SettingsLabelRdpResizeEnableDelay", "SettingsTabRdp", "SettingsRdpSubTabBehavior"),
        [nameof(AppSettings.RdpResolutionPresets)] = new("SettingsLabelRdpResolutionPresets", "SettingsTabRdp", "SettingsRdpSubTabBehavior"),
        [nameof(AppSettings.RdpDefaultNla)] = new("SettingsLabelRdpNla", "SettingsTabRdp", "SettingsRdpSubTabCertificates"),
        [nameof(AppSettings.RdpDefaultStrictServerAuthentication)] = new("SettingsLabelRdpStrictServerAuth", "SettingsTabRdp", "SettingsRdpSubTabCertificates"),
        [nameof(AppSettings.RdpDefaultRedirectClipboard)] = new("ServerDialogRedirectClipboard", "SettingsTabRdp", "SettingsRdpSubTabDevices"),
        [nameof(AppSettings.RdpDefaultRedirectComPorts)] = new("ServerDialogRedirectComPorts", "SettingsTabRdp", "SettingsRdpSubTabDevices"),
        [nameof(AppSettings.RdpDefaultRedirectDrives)] = new("ServerDialogRedirectDrives", "SettingsTabRdp", "SettingsRdpSubTabDevices"),
        [nameof(AppSettings.RdpDefaultRedirectPrinters)] = new("ServerDialogRedirectPrinters", "SettingsTabRdp", "SettingsRdpSubTabDevices"),
        [nameof(AppSettings.RdpDefaultRedirectSmartCards)] = new("ServerDialogRedirectSmartCards", "SettingsTabRdp", "SettingsRdpSubTabDevices"),
        [nameof(AppSettings.RdpDefaultRedirectUsb)] = new("ServerDialogRedirectUsb", "SettingsTabRdp", "SettingsRdpSubTabDevices"),
        [nameof(AppSettings.RdpDefaultRedirectWebcam)] = new("ServerDialogRedirectWebcam", "SettingsTabRdp", "SettingsRdpSubTabDevices"),
        [nameof(AppSettings.DefaultResolutionHeight)] = new("SettingsLabelRdpHeight", "SettingsTabRdp", "SettingsRdpSubTabDisplayAudio"),
        [nameof(AppSettings.DefaultResolutionWidth)] = new("SettingsLabelRdpWidth", "SettingsTabRdp", "SettingsRdpSubTabDisplayAudio"),
        [nameof(AppSettings.RdpDefaultAudioCapture)] = new("RdpAudioCapture", "SettingsTabRdp", "SettingsRdpSubTabDisplayAudio"),
        [nameof(AppSettings.RdpDefaultAudioMode)] = new("SettingsLabelRdpAudio", "SettingsTabRdp", "SettingsRdpSubTabDisplayAudio"),
        [nameof(AppSettings.RdpDefaultColorDepth)] = new("SettingsLabelRdpColorDepth", "SettingsTabRdp", "SettingsRdpSubTabDisplayAudio"),
        [nameof(AppSettings.RdpDefaultDynamicResolution)] = new("SettingsLabelRdpDynamicRes", "SettingsTabRdp", "SettingsRdpSubTabDisplayAudio"),
        [nameof(AppSettings.RdpDefaultMode)] = new("SettingsLabelRdpMode", "SettingsTabRdp", "SettingsRdpSubTabDisplayAudio"),
        [nameof(AppSettings.RdpDefaultMultiMonitor)] = new("SettingsLabelRdpMultiMonitor", "SettingsTabRdp", "SettingsRdpSubTabDisplayAudio"),
        [nameof(AppSettings.RdpDefaultAutoReconnect)] = new("SettingsLabelRdpAutoReconnect", "SettingsTabRdp", "SettingsRdpSubTabPerformance"),
        [nameof(AppSettings.RdpDefaultBitmapCaching)] = new("SettingsLabelRdpBitmapCache", "SettingsTabRdp", "SettingsRdpSubTabPerformance"),
        [nameof(AppSettings.RdpDefaultCompression)] = new("SettingsLabelRdpCompression", "SettingsTabRdp", "SettingsRdpSubTabPerformance"),
        [nameof(AppSettings.RdpDefaultHardwareAcceleration)] = new("SettingsLabelRdpHardwareAcceleration", "SettingsTabRdp", "SettingsRdpSubTabPerformance"),
        [nameof(AppSettings.RdpHostPoolCapacity)] = new("SettingsLabelRdpHostPoolCapacity", "SettingsTabRdp", "SettingsRdpSubTabPerformance"),
        [nameof(AppSettings.RdpHostPoolIdleExpiryMinutes)] = new("SettingsLabelRdpHostPoolIdleExpiry", "SettingsTabRdp", "SettingsRdpSubTabPerformance"),
        [nameof(AppSettings.CredentialProviderCommand)] = new("SettingsLabelCredProviderCommand", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.CredentialProviderDatabase)] = new("SettingsLabelCredProviderDatabase", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.CredentialProviderFirstLineOnly)] = new("SettingsLabelCredProviderFirstLineOnly", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.CredentialProviderKeyFile)] = new("SettingsLabelCredProviderKeyFile", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.CredentialProviderTimeoutMs)] = new("SettingsLabelCredProviderTimeout", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.CredentialProviderType)] = new("SettingsLabelCredProviderType", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.CredentialProviderUsernameCommand)] = new("SettingsLabelCredProviderUsernameCmd", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.RequireCredentialGuard)] = new("SettingsLabelCredentialGuard", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.RequireWindowsHelloOnConnect)] = new("SettingsLabelRequireWindowsHello", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.UseExternalCredentialProvider)] = new("SettingsLabelCredProviderEnabled", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.WindowsHelloGraceMinutes)] = new("SettingsLabelWindowsHelloGrace", "SettingsTabSecurity", "SettingsSectionCredentialProvider"),
        [nameof(AppSettings.FileShareEnableTftp)] = new("CbxEnableTftpShare", "SettingsTabSecurity", "SettingsSectionFileSharing"),
        [nameof(AppSettings.AutoLockIdleMinutes)] = new("SettingsAutoLockLabel", "SettingsTabSecurity", "SettingsSectionVault"),
        [nameof(AppSettings.DisconnectOnLock)] = new("SettingsDisconnectOnLock", "SettingsTabSecurity", "SettingsSectionVault"),
        [nameof(AppSettings.VaultHelloMaxDaysBeforeMasterPassword)] = new("SettingsLabelVaultHelloMaxDays", "SettingsTabSecurity", "SettingsSectionVault"),
        [nameof(AppSettings.EnableLogging)] = new("SettingsLabelEnableLogging", "SettingsTabAdvanced", "SettingsAdvancedSubTabDiagnostics"),
        [nameof(AppSettings.ExternalToolTimeoutMs)] = new("SettingsLabelExtToolTimeout", "SettingsTabAdvanced", "SettingsAdvancedSubTabDiagnostics"),
        [nameof(AppSettings.RdpConnectWatchdogTimeoutMs)] = new("SettingsLabelRdpTimeout", "SettingsTabAdvanced", "SettingsAdvancedSubTabDiagnostics"),
        [nameof(AppSettings.SessionHealthCheckIntervalSeconds)] = new("SettingsLabelSessionHealthCheckInterval", "SettingsTabAdvanced", "SettingsAdvancedSubTabDiagnostics"),
        [nameof(AppSettings.SessionHealthMaxConcurrent)] = new("SettingsLabelSessionHealthMaxConcurrent", "SettingsTabAdvanced", "SettingsAdvancedSubTabDiagnostics"),
        [nameof(AppSettings.SessionHealthMonitorEnabled)] = new("SettingsLabelSessionHealthMonitorEnabled", "SettingsTabAdvanced", "SettingsAdvancedSubTabDiagnostics"),
        [nameof(AppSettings.SessionHealthProbeTimeoutMs)] = new("SettingsLabelSessionHealthProbeTimeout", "SettingsTabAdvanced", "SettingsAdvancedSubTabDiagnostics"),
        [nameof(AppSettings.SessionLogDirectory)] = new("SettingsLabelSessionLogDirectory", "SettingsTabAdvanced", "SettingsAdvancedSubTabDiagnostics"),
        [nameof(AppSettings.SessionLoggingEnabled)] = new("SettingsLabelSessionLoggingEnabled", "SettingsTabAdvanced", "SettingsAdvancedSubTabDiagnostics"),
        [nameof(AppSettings.TunnelEstablishmentDelayMs)] = new("SettingsLabelTunnelDelay", "SettingsTabAdvanced", "SettingsAdvancedSubTabDiagnostics"),
        [nameof(AppSettings.CmdLibGitSyncAuthorEmail)] = new("A11ySettingsCmdLibSyncAuthorEmail", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.CmdLibGitSyncAuthorName)] = new("A11ySettingsCmdLibSyncAuthorName", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.CmdLibGitSyncAutoPush)] = new("SettingsCmdLibSyncAutoPush", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.CmdLibGitSyncBranch)] = new("SettingsCmdLibSyncBranch", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.CmdLibGitSyncEnabled)] = new("SettingsCmdLibSyncEnable", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.CmdLibGitSyncOnStartup)] = new("SettingsCmdLibSyncOnStartup", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.CmdLibGitSyncUrl)] = new("SettingsCmdLibSyncUrl", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.ExternalEditorPath)] = new("SettingsLabelEditorPath", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.ExternalTools)] = new("SettingsSectionExternalToolsList", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.NanaRunPath)] = new("SettingsLblNanaRunPath", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.NirSoftPath)] = new("SettingsLblNirSoftPath", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
        [nameof(AppSettings.SysinternalsPath)] = new("SettingsLblSysinternalsPath", "SettingsTabAdvanced", "SettingsAdvancedSubTabTools"),
    };

    /// <summary>The panel place of a setting, when the catalog knows it.</summary>
    /// <param name="settingName">The <see cref="AppSettings"/> property name.</param>
    /// <param name="place">The place, or null when the setting has none.</param>
    internal static bool TryGetPlace(string settingName, out SettingsPanelPlace? place) =>
        Places.TryGetValue(settingName, out place);

    /// <summary>The settings among <paramref name="settingNames"/> the catalog has no place for.</summary>
    /// <param name="settingNames">The <see cref="AppSettings"/> property names to check.</param>
    internal static IReadOnlyList<string> Unplaced(IEnumerable<string> settingNames) =>
        settingNames.Where(name => !Places.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();

    /// <summary>Every place the catalog holds, for the locale key checks.</summary>
    internal static IEnumerable<SettingsPanelPlace> AllPlaces => Places.Values;
}
