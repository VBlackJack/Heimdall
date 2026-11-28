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

using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Parses SSH keys (public and private, OpenSSH and PEM formats) and audits
/// their security strength. Displays algorithm, key size, fingerprint, format,
/// encryption status, and actionable security recommendations.
/// </summary>
public partial class SshKeyAuditView : UserControl, IToolView
{
    private const int DebounceDelayMs = 200;
    private const string KeyFileFilterKey = "ToolSshAuditKeyFileFilter";

    private LocalizationManager? _localizer;
    private HeimdallThemeService? _themeService;
    private readonly SshKeyAuditViewModel _vm;
    private DispatcherTimer? _debounceTimer;
    private SshFleetComplianceReport? _lastFleetReport;

    private sealed class FindingDisplayItem
    {
        public string Icon { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public Brush IconBrush { get; init; } = Brushes.Transparent;
    }

    private sealed class AgentKeyRowItem
    {
        public required SshAgentKeyInfo Key { get; init; }
        public string AgentName => Key.AgentName;
        public string KeyType => Key.KeyType;
        public string Fingerprint => Key.Fingerprint;
        public string Comment => Key.Comment;
        public string MatchedProfilesText { get; init; } = string.Empty;
        public bool HasMatchedProfiles => !string.IsNullOrWhiteSpace(MatchedProfilesText);
        public string AuditLabel { get; init; } = string.Empty;
        public string DeployLabel { get; init; } = string.Empty;
        public string RemoveLabel { get; init; } = string.Empty;
    }

    public SshKeyAuditView()
    {
        InitializeComponent();
        _vm = new SshKeyAuditViewModel();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        InitializeDebounceTimer();
    }

    /// <inheritdoc/>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (_localizer is not null) _localizer.LocaleChanged -= OnLocaleChanged;
        _localizer = localizer;
        if (_localizer is not null) _localizer.LocaleChanged += OnLocaleChanged;

        if (_themeService is not null) _themeService.ThemeChanged -= OnThemeChanged;
        _themeService = (Application.Current as App)?.Services?.GetService<HeimdallThemeService>();
        if (_themeService is not null) _themeService.ThemeChanged += OnThemeChanged;

        _vm.Initialize(localizer);
        ApplyLocalization();
        _ = RefreshAgentInspectorAsync();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            KeyInput.Focus();
            KeyInput.SelectAll();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolSshAuditTitle");
        InputLabel.Text = L("ToolSshAuditInput");
        BtnBrowse.Content = L("ToolSshAuditBtnBrowse");
        BtnLoadFromAgent.Content = L("ToolSshAuditBtnLoadAgent");
        BtnRemoveFromAgent.Content = L("ToolSshAuditBtnRemoveAgent");
        BtnClearAgent.Content = L("ToolSshAuditBtnClearAgent");
        BtnDeployKey.Content = L("ToolSshKeyGenBtnDeploy");
        AgentInspectorTitle.Text = L("ToolSshAuditAgentInspectorTitle");
        BtnRefreshAgentInspector.Content = L("ToolSshAuditAgentInspectorRefresh");
        BtnLoadKeyFileToAgent.Content = L("ToolSshAuditAgentInspectorLoadFile");
        AgentInspectorEmptyText.Text = L("ToolSshAuditAgentInspectorEmpty");
        FleetComplianceTitle.Text = L("ToolSshAuditFleetTitle");
        BtnScanFleet.Content = L("ToolSshAuditFleetBtnScan");
        BtnExportFleetCsv.Content = L("ToolSshAuditFleetBtnExportCsv");
        if (_lastFleetReport is null)
        {
            FleetComplianceStatusText.Text = L("ToolSshAuditFleetEmpty");
        }
        EmptyState.Text = L("ToolSshAuditEmptyState");
        ParseError.Text = L("ToolSshAuditParseError");
        BtnCopyFingerprint.Content = L("ToolSshAuditBtnCopy");
        FindingsTitle.Text = L("ToolSshAuditRating");

        DetailAlgorithmLabel.Text = L("ToolSshAuditAlgorithm");
        DetailKeySizeLabel.Text = L("ToolSshAuditKeySize");
        DetailFingerprintLabel.Text = L("ToolSshAuditFingerprint");
        DetailFormatLabel.Text = L("ToolSshAuditFormat");
        DetailTypeLabel.Text = L("ToolSshAuditType");
        DetailEncryptedLabel.Text = L("ToolSshAuditEncrypted");

        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnBrowse, L("ToolSshAuditBtnBrowse"));
        System.Windows.Automation.AutomationProperties.SetName(BtnLoadFromAgent, L("ToolSshAuditBtnLoadAgent"));
        System.Windows.Automation.AutomationProperties.SetName(BtnRemoveFromAgent, L("ToolSshAuditBtnRemoveAgent"));
        System.Windows.Automation.AutomationProperties.SetName(BtnClearAgent, L("ToolSshAuditBtnClearAgent"));
        System.Windows.Automation.AutomationProperties.SetName(BtnDeployKey, L("ToolSshKeyGenBtnDeploy"));
        System.Windows.Automation.AutomationProperties.SetName(BtnRefreshAgentInspector, L("ToolSshAuditAgentInspectorRefresh"));
        System.Windows.Automation.AutomationProperties.SetName(BtnLoadKeyFileToAgent, L("ToolSshAuditAgentInspectorLoadFile"));
        System.Windows.Automation.AutomationProperties.SetName(BtnScanFleet, L("ToolSshAuditFleetBtnScan"));
        System.Windows.Automation.AutomationProperties.SetName(BtnExportFleetCsv, L("ToolSshAuditFleetBtnExportCsv"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopyFingerprint, L("ToolSshAuditBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(KeyInput, L("ToolSshAuditInput"));
        System.Windows.Automation.AutomationProperties.SetName(DetailFingerprintValue, L("ToolSshAuditFingerprint"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        BtnCopyFingerprint.ToolTip = L("ToolBtnCopyToClipboard");
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        KeyInput.Tag = L("ToolWatermarkSshKey");
    }

    private void InitializeDebounceTimer()
    {
        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DebounceDelayMs)
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _vm.RunAuditCommand.Execute(null);
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SshKeyAuditViewModel.Rating):
                UpdateBadgeBrushes();
                break;
            case nameof(SshKeyAuditViewModel.Findings):
                ProjectFindings();
                break;
            case nameof(SshKeyAuditViewModel.IsStatusError):
                UpdateStatusBrush();
                break;
            case nameof(SshKeyAuditViewModel.ShowResults):
                if (_vm.ShowResults)
                {
                    UpdateResultDisplay();
                }

                UpdateDeployButtonVisibility();
                break;
        }
    }

    private void UpdateStatusBrush()
    {
        FileError.SetResourceReference(
            TextBlock.ForegroundProperty,
            _vm.IsStatusError ? "ErrorTextBrush" : "SuccessTextBrush");
    }

    private void UpdateDeployButtonVisibility()
    {
        bool canDeploy = _vm.ShowResults
            && !_vm.IsPrivateKey
            && SshPublicKeyDeployer.TryValidatePublicKeyLine(_vm.KeyText, out _);
        BtnDeployKey.Visibility = canDeploy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnDeployKeyClick(object sender, RoutedEventArgs e)
    {
        if (_localizer is null || !SshPublicKeyDeployer.TryValidatePublicKeyLine(_vm.KeyText, out var publicKeyLine))
        {
            return;
        }

        BtnDeployKey.IsEnabled = false;
        try
        {
            var services = (Application.Current as App)?.Services;
            await SshPublicKeyDeployUiHelper.DeployInteractivelyAsync(
                    services,
                    publicKeyLine,
                    _localizer,
                    status =>
                    {
                        bool isErr = status.StartsWith(
                            L("ToolSshKeyGenDeployFailed").Split('{')[0],
                            StringComparison.OrdinalIgnoreCase);
                        _vm.ShowStatus(status, isErr);
                    })
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SSH public key deployment failed: {ex.Message}");
            _vm.ShowStatus(
                string.Format(
                    CultureInfo.CurrentCulture,
                    L("ToolSshKeyGenDeployFailed"),
                    ex.Message),
                isError: true);
        }
        finally
        {
            BtnDeployKey.IsEnabled = true;
        }
    }

    private void UpdateBadgeBrushes()
    {
        AlgorithmBadge.Background = GetAlgorithmBrush(_vm.Algorithm);
        RatingBadge.Background = GetRatingBrush(_vm.Rating);
        RatingBadgeText.Text = GetRatingDisplayText(_vm.Rating);
    }

    private void UpdateResultDisplay()
    {
        AlgorithmBadgeText.Text = _vm.Algorithm;
        DetailKeySizeValue.Text = string.Format(L("ToolSshAuditBitsLabel"), _vm.KeySize);
        DetailTypeValue.Text = _vm.IsPrivateKey ? L("ToolSshAuditPrivate") : L("ToolSshAuditPublic");
        DetailEncryptedValue.Text = _vm.IsEncrypted ? L("ToolSshAuditYes") : L("ToolSshAuditNo");
    }

    private void ProjectFindings()
    {
        FindingsList.ItemsSource = _vm.Findings.Select(f => new FindingDisplayItem
        {
            Icon = f.Severity switch
            {
                FindingSeverity.Pass => "\u2713",
                FindingSeverity.Warning => "\u26A0",
                FindingSeverity.Fail => "\u2717",
                _ => string.Empty
            },
            Text = f.Text,
            IconBrush = f.Severity switch
            {
                FindingSeverity.Pass => (Brush)FindResource("SuccessTextBrush"),
                FindingSeverity.Warning => (Brush)FindResource("WarningTextBrush"),
                FindingSeverity.Fail => (Brush)FindResource("ErrorTextBrush"),
                _ => (Brush)FindResource("TextSecondaryBrush")
            }
        }).ToList();
    }

    private void OnKeyInputTextChanged(object sender, TextChangedEventArgs e)
    {
        _debounceTimer?.Stop();
        _debounceTimer?.Start();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = L(KeyFileFilterKey)
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        // The view model reports an oversized or unreadable file through
        // FileErrorMessage; the view used to drop those cases without a word.
        _vm.LoadKeyFile(dialog.FileName);
    }

    private async void OnLoadFromAgentClick(object sender, RoutedEventArgs e)
    {
        var services = (Application.Current as App)?.Services;
        var configManager = services?.GetService<IConfigManager>();
        var dialogService = services?.GetService<IDialogService>();

        BtnLoadFromAgent.IsEnabled = false;
        try
        {
            var preference = await ResolveAgentPreferenceAsync(configManager).ConfigureAwait(true);

            IReadOnlyList<SshAgentKeyInfo> loadedKeys;
            try
            {
                loadedKeys = await Task.Run(() =>
                    SshAgentInspector.ListLoadedKeys(SshAgentRegistry.CreateDefault(preference)));
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"SSH agent inspector failed: {ex.Message}");
                _vm.ShowStatus(ex.Message, isError: true);
                return;
            }

            if (loadedKeys.Count == 0)
            {
                _vm.ShowStatus(L("ToolSshAuditAgentEmpty"), isError: true);
                return;
            }

            _vm.ShowStatus(string.Empty, isError: false);

            if (loadedKeys.Count == 1 || dialogService is null)
            {
                _vm.KeyText = loadedKeys[0].PublicKeyOpenSsh;
                return;
            }

            var choices = loadedKeys
                .Select(k => string.IsNullOrWhiteSpace(k.Comment)
                    ? $"{k.AgentName}: {k.KeyType} ({k.Fingerprint})"
                    : $"{k.AgentName}: {k.KeyType} - {k.Comment} ({k.Fingerprint})")
                .ToList();

            var selectedIndex = await dialogService.ShowChoiceAsync(
                L("ToolSshAuditAgentTitle"),
                L("ToolSshAuditAgentPrompt"),
                choices,
                defaultIndex: 0,
                cancelLabel: L("BtnCancel"));

            if (selectedIndex is int index && index >= 0 && index < loadedKeys.Count)
            {
                _vm.KeyText = loadedKeys[index].PublicKeyOpenSsh;
            }
        }
        finally
        {
            BtnLoadFromAgent.IsEnabled = true;
        }
    }

    private async void OnRemoveFromAgentClick(object sender, RoutedEventArgs e)
    {
        var services = (Application.Current as App)?.Services;
        var configManager = services?.GetService<IConfigManager>();
        var dialogService = services?.GetService<IDialogService>();

        BtnRemoveFromAgent.IsEnabled = false;
        try
        {
            var preference = await ResolveAgentPreferenceAsync(configManager).ConfigureAwait(true);

            var loadedKeys = await Task.Run(() =>
                SshAgentInspector.ListLoadedKeys(SshAgentRegistry.CreateDefault(preference))).ConfigureAwait(true);

            if (loadedKeys.Count == 0)
            {
                _vm.FileErrorMessage = L("ToolSshAuditAgentEmpty");
                _vm.ShowFileError = true;
                return;
            }

            if (dialogService is null)
            {
                return;
            }

            var choices = loadedKeys
                .Select(k => string.IsNullOrWhiteSpace(k.Comment)
                    ? $"{k.AgentName}: {k.KeyType} ({k.Fingerprint})"
                    : $"{k.AgentName}: {k.KeyType} - {k.Comment} ({k.Fingerprint})")
                .ToList();

            var selectedIndex = await dialogService.ShowChoiceAsync(
                L("ToolSshAuditBtnRemoveAgent"),
                L("ToolSshAuditAgentRemovePrompt"),
                choices,
                defaultIndex: 0,
                cancelLabel: L("BtnCancel"));

            if (selectedIndex is not int idx || idx < 0 || idx >= loadedKeys.Count)
            {
                return;
            }

            var chosen = loadedKeys[idx];
            var result = await Task.Run(() => SshAgentManager.RemoveKeyByFingerprint(
                SshAgentRegistry.CreateDefault(preference),
                chosen.Fingerprint,
                chosen.AgentName)).ConfigureAwait(true);

            _vm.ShowStatus(
                result.Success
                    ? string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentRemoveSuccess"), chosen.Fingerprint)
                    : string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentRemoveFailed"), SshAgentErrorLocalizer.Format(result, _localizer)),
                isError: !result.Success);
            await RefreshAgentInspectorAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SSH agent remove failed: {ex.Message}");
            _vm.ShowStatus(
                string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentRemoveFailed"), ex.Message),
                isError: true);
        }
        finally
        {
            BtnRemoveFromAgent.IsEnabled = true;
        }
    }

    private async void OnClearAgentClick(object sender, RoutedEventArgs e)
    {
        var services = (Application.Current as App)?.Services;
        var configManager = services?.GetService<IConfigManager>();
        var dialogService = services?.GetService<IDialogService>();

        if (dialogService is null)
        {
            return;
        }

        BtnClearAgent.IsEnabled = false;
        try
        {
            var confirmed = await dialogService.ShowConfirmAsync(
                L("ToolSshAuditAgentClearConfirmTitle"),
                L("ToolSshAuditAgentClearConfirmMessage"),
                "warning").ConfigureAwait(true);

            if (!confirmed)
            {
                return;
            }

            var preference = await ResolveAgentPreferenceAsync(configManager).ConfigureAwait(true);
            var result = await Task.Run(() => SshAgentManager.RemoveAllKeys(
                SshAgentRegistry.CreateDefault(preference))).ConfigureAwait(true);

            _vm.ShowStatus(
                result.Success
                    ? L("ToolSshAgentClearSuccess")
                    : string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentClearFailed"), SshAgentErrorLocalizer.Format(result, _localizer)),
                isError: !result.Success);
            await RefreshAgentInspectorAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SSH agent clear failed: {ex.Message}");
            _vm.ShowStatus(
                string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentClearFailed"), ex.Message),
                isError: true);
        }
        finally
        {
            BtnClearAgent.IsEnabled = true;
        }
    }

    private async void OnRefreshAgentInspectorClick(object sender, RoutedEventArgs e)
    {
        await RefreshAgentInspectorAsync().ConfigureAwait(true);
    }

    private async Task RefreshAgentInspectorAsync()
    {
        var services = (Application.Current as App)?.Services;
        var configManager = services?.GetService<IConfigManager>();

        BtnRefreshAgentInspector.IsEnabled = false;
        try
        {
            var preference = await ResolveAgentPreferenceAsync(configManager).ConfigureAwait(true);
            var loadedKeys = await Task.Run(() =>
                SshAgentInspector.ListLoadedKeys(SshAgentRegistry.CreateDefault(preference))).ConfigureAwait(true);

            var profileNamesByFp = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (configManager is not null)
            {
                try
                {
                    var servers = await configManager.LoadServersAsync().ConfigureAwait(true);
                    var settings = await configManager.LoadSettingsAsync().ConfigureAwait(true);
                    foreach (var s in servers)
                    {
                        if (!string.IsNullOrWhiteSpace(s.SshAgentKeyFingerprint))
                        {
                            if (!profileNamesByFp.TryGetValue(s.SshAgentKeyFingerprint, out var list))
                            {
                                list = [];
                                profileNamesByFp[s.SshAgentKeyFingerprint] = list;
                            }

                            list.Add(s.DisplayName);
                        }
                    }

                    foreach (var gw in settings.SshGateways)
                    {
                        if (!string.IsNullOrWhiteSpace(gw.SshAgentKeyFingerprint))
                        {
                            if (!profileNamesByFp.TryGetValue(gw.SshAgentKeyFingerprint, out var list))
                            {
                                list = [];
                                profileNamesByFp[gw.SshAgentKeyFingerprint] = list;
                            }

                            list.Add(string.IsNullOrWhiteSpace(gw.Name) ? gw.Host : gw.Name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Debug($"SSH agent inspector profile correlation failed: {ex.Message}");
                }
            }

            var auditLabel = L("ToolSshAuditAgentInspectorBtnAudit");
            var deployLabel = L("ToolSshAuditAgentInspectorBtnDeploy");
            var removeLabel = L("ToolSshAuditAgentInspectorBtnRemove");

            var rows = loadedKeys.Select(k =>
            {
                var matchedText = profileNamesByFp.TryGetValue(k.Fingerprint, out var names) && names.Count > 0
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        L("ToolSshAuditAgentInspectorProfilesFormat"),
                        string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase).Take(6)))
                    : string.Empty;

                return new AgentKeyRowItem
                {
                    Key = k,
                    MatchedProfilesText = matchedText,
                    AuditLabel = auditLabel,
                    DeployLabel = deployLabel,
                    RemoveLabel = removeLabel
                };
            }).ToList();

            AgentKeysList.ItemsSource = rows;
            AgentInspectorSummary.Text = string.Format(
                CultureInfo.CurrentCulture,
                L("ToolSshAuditAgentInspectorSummary"),
                rows.Count);
            AgentInspectorEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SSH agent inspector refresh failed: {ex.Message}");
            AgentKeysList.ItemsSource = Array.Empty<AgentKeyRowItem>();
            AgentInspectorEmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            BtnRefreshAgentInspector.IsEnabled = true;
        }
    }

    private void OnAgentRowAuditClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AgentKeyRowItem row)
        {
            _vm.KeyText = row.Key.PublicKeyOpenSsh;
        }
    }

    private async void OnAgentRowDeployClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AgentKeyRowItem row || _localizer is null)
        {
            return;
        }

        try
        {
            var services = (Application.Current as App)?.Services;
            await SshPublicKeyDeployUiHelper.DeployInteractivelyAsync(
                    services,
                    row.Key.PublicKeyOpenSsh,
                    _localizer,
                    status =>
                    {
                        bool isErr = status.StartsWith(
                            L("ToolSshKeyGenDeployFailed").Split('{')[0],
                            StringComparison.OrdinalIgnoreCase);
                        _vm.ShowStatus(status, isErr);
                    })
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SSH agent key deploy failed: {ex.Message}");
            _vm.ShowStatus(string.Format(CultureInfo.CurrentCulture, L("ToolSshKeyGenDeployFailed"), ex.Message), isError: true);
        }
    }

    private async void OnAgentRowRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AgentKeyRowItem row)
        {
            return;
        }

        var services = (Application.Current as App)?.Services;
        var configManager = services?.GetService<IConfigManager>();
        try
        {
            var preference = await ResolveAgentPreferenceAsync(configManager).ConfigureAwait(true);
            var result = await Task.Run(() => SshAgentManager.RemoveKeyByFingerprint(
                SshAgentRegistry.CreateDefault(preference),
                row.Key.Fingerprint,
                row.Key.AgentName)).ConfigureAwait(true);

            _vm.ShowStatus(
                result.Success
                    ? string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentRemoveSuccess"), row.Key.Fingerprint)
                    : string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentRemoveFailed"), SshAgentErrorLocalizer.Format(result, _localizer)),
                isError: !result.Success);
            await RefreshAgentInspectorAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SSH agent key removal failed: {ex.Message}");
            _vm.ShowStatus(string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentRemoveFailed"), ex.Message), isError: true);
        }
    }

    private async void OnLoadKeyFileToAgentClick(object sender, RoutedEventArgs e)
    {
        var services = (Application.Current as App)?.Services;
        var configManager = services?.GetService<IConfigManager>();
        var dialogService = services?.GetService<IDialogService>();

        var openDialog = new OpenFileDialog
        {
            Filter = L(KeyFileFilterKey),
            CheckFileExists = true
        };

        if (openDialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        BtnLoadKeyFileToAgent.IsEnabled = false;
        try
        {
            var keyContent = await System.IO.File.ReadAllTextAsync(openDialog.FileName).ConfigureAwait(true);
            uint? lifetimeSeconds = null;
            if (dialogService is not null)
            {
                var ttlChoices = new List<string>
                {
                    L("ToolSshAgentTtlSession"),
                    L("ToolSshAgentTtl1h"),
                    L("ToolSshAgentTtl4h"),
                    L("ToolSshAgentTtl8h")
                };

                var picked = await dialogService.ShowChoiceAsync(
                    L("ToolSshAuditAgentInspectorLoadFile"),
                    L("ToolSshAgentTtlPrompt"),
                    ttlChoices,
                    defaultIndex: 0,
                    cancelLabel: L("BtnCancel")).ConfigureAwait(true);

                if (picked is not int choiceIndex)
                {
                    return;
                }

                lifetimeSeconds = choiceIndex switch
                {
                    1 => 3600u,
                    2 => 14400u,
                    3 => 28800u,
                    _ => null
                };
            }

            var preference = await ResolveAgentPreferenceAsync(configManager).ConfigureAwait(true);
            var comment = System.IO.Path.GetFileName(openDialog.FileName);
            var result = await Task.Run(() => SshAgentManager.AddKey(
                SshAgentRegistry.CreateDefault(preference),
                keyContent,
                passphrase: null,
                comment,
                lifetimeSeconds)).ConfigureAwait(true);

            if (!result.Success && dialogService is not null && result.ErrorCode == SshAgentOperationErrorCode.Exception)
            {
                var passphrase = await dialogService.ShowPasswordInputAsync(
                    L("ToolSshAuditAgentLoadPassphraseTitle"),
                    string.Format(CultureInfo.CurrentCulture, L("ToolSshAuditAgentLoadPassphrasePrompt"), comment))
                    .ConfigureAwait(true);

                if (passphrase is null)
                {
                    return;
                }

                result = await Task.Run(() => SshAgentManager.AddKey(
                    SshAgentRegistry.CreateDefault(preference),
                    keyContent,
                    passphrase,
                    comment,
                    lifetimeSeconds)).ConfigureAwait(true);
            }

            _vm.ShowStatus(
                result.Success
                    ? string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentAddSuccess"), result.AgentName ?? string.Empty)
                    : string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentAddFailed"), SshAgentErrorLocalizer.Format(result, _localizer)),
                isError: !result.Success);
            await RefreshAgentInspectorAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SSH agent load key file failed: {ex.Message}");
            _vm.ShowStatus(string.Format(CultureInfo.CurrentCulture, L("ToolSshAgentAddFailed"), ex.Message), isError: true);
        }
        finally
        {
            BtnLoadKeyFileToAgent.IsEnabled = true;
        }
    }

    private static async Task<Core.Ssh.SshAgentPreference> ResolveAgentPreferenceAsync(IConfigManager? configManager)
    {
        if (configManager is null)
        {
            return Core.Ssh.SshAgentPreference.AutoOpenSshFirst;
        }

        try
        {
            var settings = await configManager.LoadSettingsAsync().ConfigureAwait(false);
            return settings.SshAgentPreference;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SSH agent inspector: settings load failed: {ex.Message}");
            return Core.Ssh.SshAgentPreference.AutoOpenSshFirst;
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpSSHAUDIT").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnCopyFingerprintClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_vm.Fingerprint))
        {
            try
            {
                Clipboard.SetText(_vm.Fingerprint);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return;
            }

            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
    }

    private Brush GetAlgorithmBrush(string algorithm) => algorithm switch
    {
        "Ed25519" => (Brush)FindResource("SuccessTextBrush"),
        "RSA" => (Brush)FindResource("InfoBrush"),
        "ECDSA" => (Brush)FindResource("AccentBrush"),
        "DSA" => (Brush)FindResource("ErrorTextBrush"),
        _ => (Brush)FindResource("TextSecondaryBrush")
    };

    private Brush GetRatingBrush(SecurityRating rating) => rating switch
    {
        SecurityRating.Strong => (Brush)FindResource("SuccessTextBrush"),
        SecurityRating.Acceptable => (Brush)FindResource("AccentBrush"),
        SecurityRating.Weak => (Brush)FindResource("WarningTextBrush"),
        SecurityRating.Deprecated => (Brush)FindResource("ErrorTextBrush"),
        _ => (Brush)FindResource("AccentBrush")
    };

    private string GetRatingDisplayText(SecurityRating rating) => rating switch
    {
        SecurityRating.Strong => L("ToolSshAuditStrong"),
        SecurityRating.Acceptable => L("ToolSshAuditAcceptable"),
        SecurityRating.Weak => L("ToolSshAuditWeak"),
        SecurityRating.Deprecated => L("ToolSshAuditDeprecated"),
        _ => string.Empty
    };

    private string L(string key) => _localizer?[key] ?? key;

    private async void OnScanFleetClick(object sender, RoutedEventArgs e)
    {
        BtnScanFleet.IsEnabled = false;
        try
        {
            var services = (Application.Current as App)?.Services;
            var configManager = services?.GetService<IConfigManager>();
            var hostKeyStore = services?.GetService<HostKeyStore>();
            if (configManager is null)
            {
                FleetComplianceStatusText.Text = L("ToolSshAuditFleetEmpty");
                return;
            }

            var servers = await configManager.LoadServersAsync().ConfigureAwait(true);
            var settings = await configManager.LoadSettingsAsync().ConfigureAwait(true);
            var hostKeys = hostKeyStore is null
                ? []
                : hostKeyStore.GetAllAlgorithmEntries()
                    .Select(item =>
                    {
                        (string host, int port) = ParseHostPortKey(item.HostPort);
                        return new SshFleetHostKeySnapshot(
                            host,
                            port,
                            item.Entry.Algorithm,
                            item.Entry.Fingerprint,
                            hostKeyStore.IsRevoked(host, port, item.Entry.Fingerprint));
                    })
                    .ToList();

            var report = await Task.Run(() => SshFleetComplianceAnalyzer.Analyze(
                servers,
                settings.SshGateways,
                hostKeys,
                settings.SshCryptoPolicy)).ConfigureAwait(true);

            _lastFleetReport = report;
            FleetComplianceSummary.Text = string.Format(
                CultureInfo.CurrentCulture,
                L("ToolSshAuditFleetSummary"),
                report.ScorePercent,
                report.ProfilesAudited,
                report.GatewaysAudited,
                report.HostKeysAudited,
                report.Findings.Count);
            BtnExportFleetCsv.Visibility = Visibility.Visible;

            if (report.Findings.Count == 0)
            {
                FleetComplianceStatusText.Text = L("ToolSshAuditFleetClean");
                FleetFindingsList.ItemsSource = null;
            }
            else
            {
                FleetComplianceStatusText.Text = string.Empty;
                FleetFindingsList.ItemsSource = report.Findings.Select(f => new FindingDisplayItem
                {
                    Icon = f.Severity switch
                    {
                        SshFleetFindingSeverity.Critical => "\u2716",
                        SshFleetFindingSeverity.Warning => "\u26A0",
                        _ => "\u2139"
                    },
                    Text = $"[{f.TargetKind}] {f.TargetName}: {f.Summary}",
                    IconBrush = (Brush)FindResource(f.Severity switch
                    {
                        SshFleetFindingSeverity.Critical => "ErrorTextBrush",
                        SshFleetFindingSeverity.Warning => "WarningTextBrush",
                        _ => "TextSecondaryBrush"
                    })
                }).ToList();
            }
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[SshKeyAudit] Fleet compliance scan failed: {ex.Message}");
            FleetComplianceStatusText.Text = string.Format(
                CultureInfo.CurrentCulture,
                L("ToolSshAuditFleetError"),
                ex.Message);
        }
        finally
        {
            BtnScanFleet.IsEnabled = true;
        }
    }

    private static (string Host, int Port) ParseHostPortKey(string hostPort)
    {
        if (string.IsNullOrWhiteSpace(hostPort))
        {
            return (string.Empty, DefaultPorts.Ssh);
        }

        int lastColon = hostPort.LastIndexOf(':');
        if (lastColon > 0
            && int.TryParse(hostPort[(lastColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is >= 1 and <= 65535)
        {
            return (hostPort[..lastColon].Trim('[', ']'), port);
        }

        return (hostPort.Trim('[', ']'), DefaultPorts.Ssh);
    }

    private void OnExportFleetCsvClick(object sender, RoutedEventArgs e)
    {
        if (_lastFleetReport is null)
        {
            return;
        }

        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv",
                FileName = $"ssh-fleet-compliance-{DateTime.UtcNow:yyyyMMdd}.csv"
            };
            if (dlg.ShowDialog() == true)
            {
                System.IO.File.WriteAllText(
                    dlg.FileName,
                    SshFleetComplianceAnalyzer.FormatCsvReport(_lastFleetReport),
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                CopyFeedbackHelper.ShowCopyFeedback(BtnExportFleetCsv);
            }
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[SshKeyAudit] Fleet CSV export failed: {ex.Message}");
            FleetComplianceStatusText.Text = string.Format(
                CultureInfo.CurrentCulture,
                L("ToolSshAuditFleetError"),
                ex.Message);
        }
    }

    private void OnLocaleChanged(string _)
    {
        ApplyLocalization();
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            TxtHelpContent.Text = L("ToolHelpSSHAUDIT").Replace("\\n", "\n");
        }
        if (_vm.ShowResults)
        {
            UpdateBadgeBrushes();
            UpdateResultDisplay();
            ProjectFindings();
        }
    }

    private void OnThemeChanged(string _)
    {
        if (_vm.ShowResults)
        {
            UpdateBadgeBrushes();
            ProjectFindings();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_localizer is not null) _localizer.LocaleChanged -= OnLocaleChanged;
        if (_themeService is not null) _themeService.ThemeChanged -= OnThemeChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;

        if (_debounceTimer is not null)
        {
            _debounceTimer.Stop();
            _debounceTimer = null;
        }

        GC.SuppressFinalize(this);
    }
}
