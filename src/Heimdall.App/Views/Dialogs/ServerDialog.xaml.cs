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

using System.Windows;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Microsoft.Win32;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Server add/edit dialog. Code-behind is limited to PasswordBox handling,
/// file browse, DialogResult assignment, localization, and advanced mode persistence.
/// </summary>
public partial class ServerDialog : Window
{
    private readonly LocalizationManager? _localizer;
    private readonly IConfigManager? _configManager;

    public ServerDialog(LocalizationManager localizer, IConfigManager configManager)
    {
        _localizer = localizer;
        _configManager = configManager;
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        LoadAdvancedModePreference();
        ApplyLocalization();
    }

    public ServerDialog(LocalizationManager localizer)
    {
        _localizer = localizer;
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        ApplyLocalization();
    }

    public ServerDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
    }

    // ------------------------------------------------------------------
    // Advanced mode persistence
    // ------------------------------------------------------------------

    private void LoadAdvancedModePreference()
    {
        // Always defer to Loaded so DataContext and the config service are available.
        Loaded += OnLoadedApplyAdvancedMode;
    }

    private async void OnLoadedApplyAdvancedMode(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedApplyAdvancedMode;

        if (_configManager is null || DataContext is not ServerDialogViewModel vm) return;

        var settings = await _configManager.LoadSettingsAsync();
        vm.ApplyRdpDialogAdvancedDefault(settings.RdpDialogAdvancedDefault);
        vm.PropertyChanged += OnViewModelPropertyChanged;

        // Set initial focus: edit mode → display name field, add mode → first protocol card
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (vm.IsEditMode)
                DlgSrv_DisplayNameBox.Focus();
            else
                ProtocolCard_Rdp?.Focus();
        });
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DataContext is not ServerDialogViewModel vm) return;

        if (e.PropertyName == nameof(ServerDialogViewModel.HasSshKeyPath) && !vm.HasSshKeyPath)
        {
            (FindName("SshKeyPassphraseBox") as System.Windows.Controls.PasswordBox)?.Clear();
            return;
        }

        if (_configManager is null) return;

        if (e.PropertyName == nameof(ServerDialogViewModel.IsAdvancedMode))
        {
            if (!ServerDialogAdvancedModePolicy.ShouldPersistRdpDefault(
                    vm.ConnectionType,
                    vm.IsEditMode,
                    vm.IsProtocolSelected,
                    vm.IsApplyingRdpDialogAdvancedDefault))
            {
                return;
            }

            _ = _configManager.MergeSettingAsync(s => s.RdpDialogAdvancedDefault = vm.IsAdvancedMode);
        }
    }

    // ------------------------------------------------------------------
    // Resolution presets (RDP)
    // ------------------------------------------------------------------

    private void OnRdpResolutionPresetSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo
            || combo.SelectedItem is not System.Windows.Controls.ComboBoxItem item
            || item.Tag is not string tag
            || string.IsNullOrEmpty(tag))
        {
            return;
        }

        if (DataContext is ServerDialogViewModel vm
            && vm.ApplyResolutionPresetCommand.CanExecute(tag))
        {
            vm.ApplyResolutionPresetCommand.Execute(tag);
        }
    }

    // ------------------------------------------------------------------
    // Save
    // ------------------------------------------------------------------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ServerDialogViewModel vm)
        {
            return;
        }

        // Block save while still on the protocol selection step
        if (!vm.ShowFormFields)
        {
            return;
        }

        // Transfer PasswordBox values if they exist in the visual tree
        var sshPwBox = FindName("SshPasswordBox") as System.Windows.Controls.PasswordBox;
        var sshKeyPassphraseBox = FindName("SshKeyPassphraseBox") as System.Windows.Controls.PasswordBox;
        var rdpPwBox = FindName("RdpPasswordBox") as System.Windows.Controls.PasswordBox;
        var vncPwBox = FindName("VncPasswordBox") as System.Windows.Controls.PasswordBox;
        var ftpPwBox = FindName("FtpPasswordBox") as System.Windows.Controls.PasswordBox;
        var telnetPwBox = FindName("TelnetPasswordBox") as System.Windows.Controls.PasswordBox;
        System.Windows.Controls.PasswordBox? winRmPwBox =
            FindName("WinRmPasswordBox") as System.Windows.Controls.PasswordBox;

        if (sshPwBox is not null) vm.SshPassword = sshPwBox.Password;
        if (sshKeyPassphraseBox is not null)
        {
            vm.SshKeyPassphrase = vm.HasSshKeyPath ? sshKeyPassphraseBox.Password : "";
        }

        if (rdpPwBox is not null) vm.RdpPassword = rdpPwBox.Password;
        if (vncPwBox is not null) vm.VncPassword = vncPwBox.Password;
        if (ftpPwBox is not null) vm.FtpPassword = ftpPwBox.Password;
        if (telnetPwBox is not null) vm.TelnetPassword = telnetPwBox.Password;
        if (winRmPwBox is not null) vm.WinRmPassword = winRmPwBox.Password;

        vm.ValidateCommand.Execute(null);

        if (vm.ValidationError is null)
        {
            DialogResult = true;

            // Clear passwords from UI memory (CWE-316)
            sshPwBox?.Clear();
            sshKeyPassphraseBox?.Clear();
            rdpPwBox?.Clear();
            vncPwBox?.Clear();
            ftpPwBox?.Clear();
            telnetPwBox?.Clear();
            winRmPwBox?.Clear();
        }
        else
        {
            FocusFirstInvalidField(vm);
        }
    }

    private void FocusFirstInvalidField(ServerDialogViewModel vm)
    {
        if (vm.FirstInvalidField is null) return;

        System.Windows.UIElement? target = null;

        // Temporarily suppress dialog-state persistence during focus management.
        vm.PropertyChanged -= OnViewModelPropertyChanged;

        switch (vm.FirstInvalidField)
        {
            case nameof(ServerDialogViewModel.DisplayName):
                MainTabControl.SelectedItem = DlgSrv_TabGeneral;
                target = DlgSrv_DisplayNameBox;
                break;
            case nameof(ServerDialogViewModel.RemoteServer):
                MainTabControl.SelectedItem = DlgSrv_TabGeneral;
                target = DlgSrv_RemoteServerBox;
                break;
            case "EndpointPort":
                MainTabControl.SelectedItem = DlgSrv_TabGeneral;
                target = DlgSrv_EndpointPortBox;
                break;
            case nameof(ServerDialogViewModel.LocalPort):
                MainTabControl.SelectedItem = DlgSrv_TabNetwork;
                target = DlgSrv_LocalPortBox;
                break;
            case nameof(ServerDialogViewModel.RdpAudioMode):
                MainTabControl.SelectedItem = DlgSrv_TabOptions;
                target = DlgSrv_RdpAudioModeCombo;
                break;
            case nameof(ServerDialogViewModel.RdpColorDepth):
                MainTabControl.SelectedItem = DlgSrv_TabOptions;
                target = DlgSrv_RdpColorDepthCombo;
                break;
            case nameof(ServerDialogViewModel.RdpFixedWidth):
                MainTabControl.SelectedItem = DlgSrv_TabOptions;
                target = DlgSrv_RdpFixedWidthBox;
                break;
            case nameof(ServerDialogViewModel.RdpFixedHeight):
                MainTabControl.SelectedItem = DlgSrv_TabOptions;
                target = DlgSrv_RdpFixedHeightBox;
                break;
            case nameof(ServerDialogViewModel.RdpResizeEnableDelayMs):
                MainTabControl.SelectedItem = DlgSrv_TabOptions;
                target = DlgSrv_RdpResizeDelayBox;
                break;
        }

        vm.PropertyChanged += OnViewModelPropertyChanged;

        if (target is not null)
        {
            // Deferred focus: let layout complete after tab/panel changes
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    SelectEnclosingTabItems(target);
                    target.Focus();
                }));
        }
    }

    private static void SelectEnclosingTabItems(System.Windows.DependencyObject target)
    {
        System.Windows.DependencyObject? current = target;

        while (current is not null)
        {
            if (current is System.Windows.Controls.TabItem tabItem)
            {
                System.Windows.Controls.TabControl? tabControl = FindParentTabControl(tabItem);
                if (tabControl is not null)
                {
                    tabControl.SelectedItem = tabItem;
                }
            }

            current = GetParentObject(current);
        }
    }

    private static System.Windows.Controls.TabControl? FindParentTabControl(
        System.Windows.DependencyObject target)
    {
        System.Windows.DependencyObject? current = GetParentObject(target);

        while (current is not null)
        {
            if (current is System.Windows.Controls.TabControl tabControl)
            {
                return tabControl;
            }

            current = GetParentObject(current);
        }

        return null;
    }

    private static System.Windows.DependencyObject? GetParentObject(System.Windows.DependencyObject target)
    {
        System.Windows.DependencyObject? visualParent = null;

        if (target is System.Windows.Media.Visual || target is System.Windows.Media.Media3D.Visual3D)
        {
            visualParent = System.Windows.Media.VisualTreeHelper.GetParent(target);
        }

        return visualParent ?? System.Windows.LogicalTreeHelper.GetParent(target);
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Dirty guard: warn when closing with unsaved changes (skip when Save was clicked)
        if (DialogResult != true
            && DataContext is ServerDialogViewModel { IsDirty: true } dirtyVm)
        {
            var title = dirtyVm.Localizer?["DialogUnsavedWarningTitle"]
                ?? _localizer?["DialogUnsavedWarningTitle"]
                ?? "Unsaved Changes";
            var message = dirtyVm.Localizer?["DialogUnsavedWarning"]
                ?? _localizer?["DialogUnsavedWarning"]
                ?? "You have unsaved changes. Discard them and close?";
            var yes = dirtyVm.Localizer?["BtnYes"] ?? _localizer?["BtnYes"] ?? "Yes";
            var no = dirtyVm.Localizer?["BtnNo"] ?? _localizer?["BtnNo"] ?? "No";

            var discard = MessageDialog.ShowConfirm(this, title, message, "warning", yes, no);
            if (!discard)
            {
                e.Cancel = true;
                return;
            }
        }

        // Detach property changed handler
        if (DataContext is ServerDialogViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        // Clear all password boxes from UI memory on close (CWE-316)
        (FindName("SshPasswordBox") as System.Windows.Controls.PasswordBox)?.Clear();
        (FindName("SshKeyPassphraseBox") as System.Windows.Controls.PasswordBox)?.Clear();
        (FindName("RdpPasswordBox") as System.Windows.Controls.PasswordBox)?.Clear();
        (FindName("VncPasswordBox") as System.Windows.Controls.PasswordBox)?.Clear();
        (FindName("FtpPasswordBox") as System.Windows.Controls.PasswordBox)?.Clear();
        (FindName("TelnetPasswordBox") as System.Windows.Controls.PasswordBox)?.Clear();
        (FindName("WinRmPasswordBox") as System.Windows.Controls.PasswordBox)?.Clear();
    }

    private void OnBrowseSshKeyClick(object sender, RoutedEventArgs e)
    {
        if (_localizer is null)
        {
            ShowBrowseKeyDialog("Select SSH Key",
                "PPK Files (*.ppk)|*.ppk|PEM Files (*.pem)|*.pem|All Files (*.*)|*.*");
            return;
        }

        ShowBrowseKeyDialog(
            _localizer["ServerDialogBrowseKeyTitle"],
            _localizer["ServerDialogBrowseKeyFilter"]);
    }

    private void ShowBrowseKeyDialog(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true && DataContext is ServerDialogViewModel vm)
        {
            vm.SshKeyPath = dialog.FileName;
        }
    }

    private async void OnEditGatewayClick(object sender, RoutedEventArgs e)
    {
        if (_configManager is null || _localizer is null) return;
        if (DataContext is not ServerDialogViewModel vm) return;
        if (string.IsNullOrWhiteSpace(vm.SelectedGatewayId)) return;

        var settings = await _configManager.LoadSettingsAsync();
        var gwDto = settings.SshGateways.FirstOrDefault(
            g => string.Equals(g.Id, vm.SelectedGatewayId, StringComparison.OrdinalIgnoreCase));
        if (gwDto is null) return;

        var gwVm = GatewayDialogViewModel.FromDto(gwDto);
        gwVm.Localizer = _localizer;
        gwVm.AvailableParents = new System.Collections.ObjectModel.ObservableCollection<GatewayOption>(
            settings.SshGateways
                .Where(g => g.Id != gwDto.Id)
                .Select(g => new GatewayOption(g.Id, $"{g.Name} ({g.Host})")));

        var gwDialog = new GatewayDialog
        {
            DataContext = gwVm,
            Owner = this
        };

        if (gwDialog.ShowDialog() == true)
        {
            var updated = gwVm.ToDto();
            updated.Id = gwDto.Id;
            var idx = settings.SshGateways.FindIndex(
                g => string.Equals(g.Id, gwDto.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                settings.SshGateways[idx] = updated;
                await _configManager.SaveSettingsAsync(settings);
            }
        }
    }

    // ------------------------------------------------------------------
    // Localization
    // ------------------------------------------------------------------

    private void ApplyLocalization()
    {
        if (_localizer is null)
        {
            return;
        }

        // Tab headers
        DlgSrv_TabGeneralText.Text = _localizer["ServerDialogTabGeneral"];
        DlgSrv_TabOptionsText.Text = _localizer["ServerDialogTabOptions"];
        System.Windows.Automation.AutomationProperties.SetName(
            DlgSrv_TabOptions, _localizer["ServerDialogTabOptions"]);
        DlgSrv_TabNetworkText.Text = _localizer["ServerDialogTabNetwork"];
        DlgSrv_TabInfo.Header = _localizer["ServerDialogTabInfo"];

        // Protocol selector (Step 1)
        DlgSrv_ProtocolSelectorTitle.Text = _localizer["ServerDialogProtocolSelectorTitle"];
        DlgSrv_ProtocolSelectorDesc.Text = _localizer["ServerDialogProtocolSelectorDesc"];
        DlgSrv_ProtoRdpName.Text = _localizer["ServerDialogProtocolRdpName"];
        DlgSrv_ProtoRdpDesc.Text = _localizer["ServerDialogProtocolRdpDesc"];
        DlgSrv_ProtoSshName.Text = _localizer["ServerDialogProtocolSshName"];
        DlgSrv_ProtoSshDesc.Text = _localizer["ServerDialogProtocolSshDesc"];
        DlgSrv_ProtoWinRmName.Text = _localizer["ServerDialogProtocolWinRmName"];
        DlgSrv_ProtoWinRmDesc.Text = _localizer["ServerDialogProtocolWinRmDesc"];
        DlgSrv_ProtoSftpName.Text = _localizer["ServerDialogProtocolSftpName"];
        DlgSrv_ProtoSftpDesc.Text = _localizer["ServerDialogProtocolSftpDesc"];
        DlgSrv_ProtoVncName.Text = _localizer["ServerDialogProtocolVncName"];
        DlgSrv_ProtoVncDesc.Text = _localizer["ServerDialogProtocolVncDesc"];
        DlgSrv_ProtoTelnetName.Text = _localizer["ServerDialogProtocolTelnetName"];
        DlgSrv_ProtoTelnetDesc.Text = _localizer["ServerDialogProtocolTelnetDesc"];
        DlgSrv_ProtoFtpName.Text = _localizer["ServerDialogProtocolFtpName"];
        DlgSrv_ProtoFtpDesc.Text = _localizer["ServerDialogProtocolFtpDesc"];
        DlgSrv_ProtoCitrixName.Text = _localizer["ServerDialogProtocolCitrixName"];
        DlgSrv_ProtoCitrixDesc.Text = _localizer["ServerDialogProtocolCitrixDesc"];
        DlgSrv_ProtoLocalName.Text = _localizer["ServerDialogProtocolLocalName"];
        DlgSrv_ProtoLocalDesc.Text = _localizer["ServerDialogProtocolLocalDesc"];

        // Protocol badge (clickable chip — returns to protocol selector in add mode)
        DlgSrv_ProtocolBadgeLabel.Text = _localizer["ServerDialogProtocolBadge"];

        // Connection basics (essential section)
        DlgSrv_ConnectionBasicsTitle.Text = _localizer["ServerDialogConnectionBasics"];
        DlgSrv_ConnectionBasicsDesc.Text = _localizer["ServerDialogConnectionBasicsDesc"];
        DlgSrv_DisplayNameLabel.Text = _localizer["ServerDialogLabelDisplayName"] + " *";
        DlgSrv_ServerLabel.Text = _localizer["ServerDialogLabelServer"] + " *";

        // Project + Gateway routing (essential section)
        DlgSrv_ProjectLabel.Text = _localizer["ServerDialogLabelProject"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_ProjectCmb, DlgSrv_ProjectLabel);
        DlgSrv_GatewayRoutingTitle.Text = _localizer["ServerDialogGatewayRouting"];
        DlgSrv_GatewayRoutingDesc.Text = _localizer["ServerDialogGatewayRoutingDesc"];
        DlgSrv_DirectConnectCb.Content = _localizer["ServerDialogDirectConnect"];
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_GatewayCmb, _localizer["ServerDialogGatewayRouting"]);

        // Connection path diagram
        DlgSrv_ConnectionPathTitle.Text = _localizer["ServerDialogConnectionPath"];
        DlgSrv_DirectYourPc.Text = _localizer["ServerDialogNodeYourPc"];
        DlgSrv_DirectDestination.Text = _localizer["ServerDialogNodeDestination"];
        DlgSrv_GatewayYourPc.Text = _localizer["ServerDialogNodeYourPc"];
        DlgSrv_GatewayNode.Text = _localizer["ServerDialogNodeGateway"];
        DlgSrv_GatewayDestination.Text = _localizer["ServerDialogNodeDestination"];

        // Tunneling tab
        DlgSrv_TunnelingTitle.Text = _localizer["ServerDialogTunnelingTitle"];
        DlgSrv_TunnelingDesc.Text = _localizer["ServerDialogTunnelingDesc"];
        DlgSrv_TunnelingHint.Text = _localizer["ServerDialogTunnelingHint"];
        DlgSrv_DirectConnTitle.Text = _localizer["ServerDialogDirectConnectionTitle"];
        DlgSrv_DirectConnDesc.Text = _localizer["ServerDialogDirectConnectionDesc"];
        DlgSrv_LocalTunnelPortTitle.Text = _localizer["ServerDialogLocalTunnelPort"];
        DlgSrv_LocalTunnelPortDesc.Text = _localizer["ServerDialogLocalTunnelPortDesc"];
        DlgSrv_AutoTunnelPortCb.Content = _localizer["ServerDialogAutoTunnelPort"];
        DlgSrv_ManualPortLabel.Text = _localizer["ServerDialogManualLocalPort"];
        DlgSrv_SocksSectionTitle.Text = _localizer["TunnelingSocksSectionTitle"];
        DlgSrv_SocksDesc.Text = _localizer["TunnelingSocksDesc"];
        DlgSrv_SocksPortLabel.Text = _localizer["TunnelingSocksPortLabel"];
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_SocksPortBox, _localizer["TunnelingSocksPortLabel"]);
        DlgSrv_RemoteFwdSectionTitle.Text = _localizer["TunnelingRemoteSectionTitle"];
        DlgSrv_RemoteFwdDesc.Text = _localizer["TunnelingRemoteDesc"];
        DlgSrv_RemoteBindPortLabel.Text = _localizer["TunnelingRemoteBindPortLabel"];
        DlgSrv_RemoteLocalPortLabel.Text = _localizer["TunnelingRemoteLocalPortLabel"];
        DlgSrv_RemoteLocalPortHint.Text = _localizer["TunnelingRemoteLocalPortHint"];
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_RemoteBindPortBox, _localizer["TunnelingRemoteBindPortLabel"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_RemoteLocalPortBox, _localizer["TunnelingRemoteLocalPortLabel"]);
        DlgSrv_TunnelMappingTitle.Text = _localizer["ServerDialogTunnelMapping"];
        DlgSrv_ChainLabel.Text = _localizer["ServerDialogTunnelLabelChain"];
        DlgSrv_LocalEndpointLabel.Text = _localizer["ServerDialogTunnelLabelLocal"];
        DlgSrv_RemoteDestLabel.Text = _localizer["ServerDialogTunnelLabelRemote"];

        // Basic authentication section (always visible, per-protocol)
        DlgSrv_BasicRdpCredentialsTitle.Text = _localizer["ServerDialogRdpCredentials"];
        DlgSrv_BasicRdpCredentialsDesc.Text = _localizer["ServerDialogRdpCredentialsDesc"];
        DlgSrv_BasicRdpUsernameLabel.Text = _localizer["ServerDialogLabelUsername"];
        DlgSrv_BasicRdpDomainLabel.Text = _localizer["ServerDialogLabelDomain"];
        DlgSrv_BasicRdpPasswordLabel.Text = _localizer["ServerDialogLabelPassword"];
        DlgSrv_BasicWinRmCredentialsTitle.Text = _localizer["ServerDialogWinRmCredentials"];
        DlgSrv_BasicWinRmCredentialsDesc.Text = _localizer["ServerDialogWinRmCredentialsDesc"];
        DlgSrv_WinRmIdentityLabel.Text = _localizer["ServerDialogWinRmIdentityMode"];
        DlgSrv_WinRmIdentityCurrent.Content = _localizer["ServerDialogWinRmIdentityCurrentUser"];
        DlgSrv_WinRmIdentityCredential.Content = _localizer["ServerDialogWinRmIdentityCredential"];
        DlgSrv_WinRmSslCb.Content = _localizer["ServerDialogWinRmUseSsl"];
        DlgSrv_WinRmSkipCertCb.Content = _localizer["ServerDialogWinRmSkipCertCheck"];
        DlgSrv_BasicWinRmUsernameLabel.Text = _localizer["ServerDialogLabelUsername"];
        DlgSrv_BasicWinRmPasswordLabel.Text = _localizer["ServerDialogLabelPassword"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_WinRmIdentityCombo, DlgSrv_WinRmIdentityLabel);
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_WinRmUsernameBox, DlgSrv_BasicWinRmUsernameLabel);
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_SshKeyPathBox, DlgSrv_BasicSshKeyLabel);
        DlgSrv_BasicBrowseBtn.Content = _localizer["ServerDialogBtnBrowse"];
        // SSH auth hints are bound to computed ViewModel properties.
        DlgSrv_BasicVncCredentialsTitle.Text = _localizer["ServerDialogVncCredentials"];
        DlgSrv_BasicVncCredentialsDesc.Text = _localizer["ServerDialogVncCredentialsDesc"];
        DlgSrv_BasicVncPasswordLabel.Text = _localizer["ServerDialogVncPassword"];
        DlgSrv_BasicFtpCredentialsTitle.Text = _localizer["ServerDialogFtpCredentials"];
        DlgSrv_BasicFtpCredentialsDesc.Text = _localizer["ServerDialogFtpCredentialsDesc"];
        DlgSrv_BasicFtpUsernameLabel.Text = _localizer["ServerDialogFtpUsername"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_FtpUsernameBox, DlgSrv_BasicFtpUsernameLabel);
        DlgSrv_BasicFtpPasswordLabel.Text = _localizer["ServerDialogFtpPassword"];
        DlgSrv_BasicTelnetCredentialsTitle.Text = _localizer["ServerDialogTelnetCredentials"];
        DlgSrv_BasicTelnetCredentialsDesc.Text = _localizer["ServerDialogTelnetCredentialsDesc"];
        DlgSrv_BasicTelnetUsernameLabel.Text = _localizer["ServerDialogLabelUsername"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_TelnetUsernameBox, DlgSrv_BasicTelnetUsernameLabel);
        DlgSrv_BasicTelnetPasswordLabel.Text = _localizer["ServerDialogLabelPassword"];
        DlgSrv_BasicLocalShellTitle.Text = _localizer["ServerDialogLocalShell"];
        DlgSrv_BasicLocalShellDesc.Text = _localizer["ServerDialogLocalShellDesc"];
        DlgSrv_BasicExecutableLabel.Text = _localizer["ServerDialogLabelExecutable"];
        DlgSrv_BasicShellPowershell.Content = _localizer["LocalShellPowershell"];
        DlgSrv_BasicShellPwsh.Content = _localizer["LocalShellPwsh"];
        DlgSrv_BasicShellCmd.Content = _localizer["LocalShellCmd"];
        DlgSrv_BasicShellBash.Content = _localizer["LocalShellBash"];
        DlgSrv_BasicShellWsl.Content = _localizer["LocalShellWsl"];
        DlgSrv_BasicArgumentsLabel.Text = _localizer["ServerDialogLabelArguments"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_ShellArgsBox, DlgSrv_BasicArgumentsLabel);
        DlgSrv_BasicCitrixTitle.Text = _localizer["ServerDialogCitrixWorkspace"];
        DlgSrv_BasicCitrixDesc.Text = _localizer["ServerDialogCitrixWorkspaceDesc"];
        DlgSrv_BasicStoreFrontLabel.Text = _localizer["ServerDialogLabelStoreFrontUrl"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_StoreFrontBox, DlgSrv_BasicStoreFrontLabel);
        DlgSrv_BasicAppNameLabel.Text = _localizer["ServerDialogLabelAppName"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_CitrixAppNameBox, DlgSrv_BasicAppNameLabel);

        // Gateway authentication tab (advanced, only when gateway is active)
        DlgSrv_GatewayAuthTitle.Text = _localizer["ServerDialogGatewayAuth"];
        DlgSrv_GatewayAuthDesc.Text = _localizer["ServerDialogGatewayAuthDesc"];
        DlgSrv_EditGatewayBtn.Content = _localizer["ServerDialogEditGatewayBtn"];
        System.Windows.Automation.AutomationProperties.SetName(
            DlgSrv_EditGatewayBtn, _localizer["ServerDialogEditGatewayBtn"]);

        // RDP options
        DlgSrv_RdpOptionsTitle.Text = _localizer["ServerDialogRdpOptions"];
        DlgSrv_RdpOptionsDesc.Text = _localizer["ServerDialogRdpOptionsDesc"];
        DlgSrv_SessionModeLabel.Text = _localizer["ServerDialogLabelSessionMode"];
        DlgSrv_RdpModeEmbedded.Content = _localizer["ServerDialogModeEmbedded"];
        DlgSrv_RdpModeExternal.Content = _localizer["ServerDialogModeExternal"];
        DlgSrv_AspectStretch.Content = _localizer["RdpAspectStretch"];
        DlgSrv_AspectPreserve.Content = _localizer["RdpAspectPreserve"];
        DlgSrv_AspectAuto.Content = _localizer["RdpAspectAuto"];
        DlgSrv_AspectDynamic.Content = _localizer["RdpAspectDynamic"];
        DlgSrv_Aspect16x9.Content = _localizer["RdpAspect16x9"];
        DlgSrv_Aspect4x3.Content = _localizer["RdpAspect4x3"];
        DlgSrv_Aspect21x9.Content = _localizer["RdpAspect21x9"];
        DlgSrv_AspectRatioLabel.Text = _localizer["ServerDialogLabelAspectRatio"];
        DlgSrv_AudioModeLabel.Text = _localizer["ServerDialogLabelAudioMode"];
        DlgSrv_ColorDepthLabel.Text = _localizer["ServerDialogLabelColorDepth"];

        // Audio mode ComboBoxItems (Tag-based, Content is display text)
        DlgSrv_AudioDisabled.Content = _localizer["ServerDialogAudioDisabled"];
        DlgSrv_AudioLocal.Content = _localizer["ServerDialogAudioLocal"];
        DlgSrv_AudioRemote.Content = _localizer["ServerDialogAudioRemote"];

        // Color depth ComboBoxItems (Tag-based, Content is display text)
        DlgSrv_Color16.Content = _localizer["ServerDialogColor16"];
        DlgSrv_Color24.Content = _localizer["ServerDialogColor24"];
        DlgSrv_Color32.Content = _localizer["ServerDialogColor32"];

        // RDP checkboxes
        DlgSrv_NlaCb.Content = _localizer["ServerDialogNla"];
        DlgSrv_StrictServerAuthCb.Content = _localizer["ServerDialogRdpStrictServerAuth"];
        DlgSrv_DynamicResCb.Content = _localizer["ServerDialogDynamicResolution"];
        DlgSrv_AudioCaptureCb.Content = _localizer["ServerDialogAudioCapture"];

        // Device redirection
        DlgSrv_RedirClipboardCb.Content = _localizer["ServerDialogRedirectClipboard"];
        DlgSrv_RedirDrivesCb.Content = _localizer["ServerDialogRedirectDrives"];
        DlgSrv_RedirPrintersCb.Content = _localizer["ServerDialogRedirectPrinters"];
        DlgSrv_RedirComPortsCb.Content = _localizer["ServerDialogRedirectComPorts"];
        DlgSrv_RedirSmartCardsCb.Content = _localizer["ServerDialogRedirectSmartCards"];
        DlgSrv_RedirWebcamCb.Content = _localizer["ServerDialogRedirectWebcam"];
        DlgSrv_RedirUsbCb.Content = _localizer["ServerDialogRedirectUsb"];

        // Advanced RDP behavior
        DlgSrv_GlobalDefaultsCb.Content = _localizer["ServerDialogGlobalDefaults"];
        DlgSrv_AntiIdleCb.Content = _localizer["ServerDialogAntiIdle"];
        DlgSrv_BitmapCacheCb.Content = _localizer["ServerDialogBitmapCaching"];
        DlgSrv_RdpCompressionCb.Content = _localizer["ServerDialogRdpCompressionCb"];
        DlgSrv_AutoReconnectCb.Content = _localizer["ServerDialogAutoReconnect"];
        DlgSrv_RdpAdminModeCb.Content = _localizer["RdpAdminMode"];
        DlgSrv_RdpFullScreenCb.Content = _localizer["RdpFullScreen"];

        // RDP Experience
        DlgSrv_PerfDisableWallpaperCb.Content = _localizer["RdpPerfDisableWallpaper"];
        DlgSrv_PerfDisableThemesCb.Content = _localizer["RdpPerfDisableThemes"];
        DlgSrv_PerfDisableAnimationsCb.Content = _localizer["RdpPerfDisableAnimations"];
        DlgSrv_PerfDisableDragCb.Content = _localizer["RdpPerfDisableDrag"];
        DlgSrv_PerfDisableCursorShadowCb.Content = _localizer["RdpPerfDisableCursorShadow"];
        DlgSrv_PerfEnableFontSmoothingCb.Content = _localizer["RdpPerfEnableFontSmoothing"];
        DlgSrv_PerfEnableCompositionCb.Content = _localizer["RdpPerfEnableComposition"];
        DlgSrv_DisableUdpCb.Content = _localizer["RdpDisableUdp"];

        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_PerfDisableWallpaperCb, _localizer["RdpPerfDisableWallpaper"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_PerfDisableThemesCb, _localizer["RdpPerfDisableThemes"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_PerfDisableAnimationsCb, _localizer["RdpPerfDisableAnimations"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_PerfDisableDragCb, _localizer["RdpPerfDisableDrag"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_PerfDisableCursorShadowCb, _localizer["RdpPerfDisableCursorShadow"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_PerfEnableFontSmoothingCb, _localizer["RdpPerfEnableFontSmoothing"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_PerfEnableCompositionCb, _localizer["RdpPerfEnableComposition"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_DisableUdpCb, _localizer["RdpDisableUdp"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_RdpAdminModeCb, _localizer["RdpAdminMode"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_RdpFullScreenCb, _localizer["RdpFullScreen"]);

        // SSH options
        DlgSrv_SshOptionsTitle.Text = _localizer["ServerDialogSshOptions"];
        DlgSrv_SshOptionsDesc.Text = _localizer["ServerDialogSshOptionsDesc"];
        DlgSrv_SshModeLabel.Text = _localizer["ServerDialogLabelSshMode"];
        DlgSrv_SshModeEmbedded.Content = _localizer["ServerDialogModeEmbedded"];
        DlgSrv_SshModeExternal.Content = _localizer["ServerDialogModeExternal"];
        DlgSrv_SshCompressionCb.Content = _localizer["ServerDialogSshCompression"];
        DlgSrv_SshAgentFwdCb.Content = _localizer["ServerDialogSshAgentForward"];
        DlgSrv_SshX11Cb.Content = _localizer["ServerDialogSshX11"];

        // Local shell advanced options
        DlgSrv_LocalShellTitle.Text = _localizer["ServerDialogLocalShellAdvanced"];
        DlgSrv_LocalShellDesc.Text = _localizer["ServerDialogLocalShellAdvancedDesc"];
        DlgSrv_WorkingDirLabel.Text = _localizer["ServerDialogLabelWorkingDir"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_WorkingDirBox, DlgSrv_WorkingDirLabel);
        DlgSrv_ElevationModeLabel.Text = _localizer["ServerDialogElevationMode"];
        PopulateElevationModeCombo();

        // Citrix advanced options
        DlgSrv_CitrixTitle.Text = _localizer["ServerDialogCitrixAdvanced"];
        DlgSrv_CitrixDesc.Text = _localizer["ServerDialogCitrixAdvancedDesc"];
        DlgSrv_IcaFileLabel.Text = _localizer["ServerDialogLabelIcaFilePath"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_IcaFileBox, DlgSrv_IcaFileLabel);
        DlgSrv_CitrixHint.Text = _localizer["ServerDialogCitrixHint"];
        DlgSrv_SeamlessCb.Content = _localizer["ServerDialogCitrixSeamless"];
        DlgSrv_SsoCb.Content = _localizer["ServerDialogCitrixSso"];

        // (FTP, VNC, Telnet credentials moved to basic auth section above)

        // FTP options
        DlgSrv_FtpOptionsTitle.Text = _localizer["ServerDialogFtpOptions"];
        DlgSrv_FtpOptionsDesc.Text = _localizer["ServerDialogFtpOptionsDesc"];
        DlgSrv_FtpPassiveCb.Content = _localizer["ServerDialogFtpPassiveMode"];
        DlgSrv_FtpSslCb.Content = _localizer["ServerDialogFtpSsl"];

        // VNC options
        DlgSrv_VncOptionsTitle.Text = _localizer["ServerDialogVncOptions"];
        DlgSrv_VncOptionsDesc.Text = _localizer["ServerDialogVncOptionsDesc"];
        DlgSrv_VncViewOnlyCb.Content = _localizer["ServerDialogVncViewOnly"];

        // Telnet options
        DlgSrv_TelnetOptionsTitle.Text = _localizer["ServerDialogTelnetOptions"];
        DlgSrv_TelnetOptionsDesc.Text = _localizer["ServerDialogTelnetOptionsDesc"];
        DlgSrv_TelnetSecurityWarning.Text = _localizer["ServerDialogTelnetSecurityWarning"];

        // Organization
        DlgSrv_OrgTitle.Text = _localizer["ServerDialogOrganization"];
        DlgSrv_OrgDesc.Text = _localizer["ServerDialogOrganizationDesc"];
        DlgSrv_FolderLabel.Text = _localizer["ServerDialogLabelFolder"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_FolderBox, DlgSrv_FolderLabel);
        DlgSrv_EnvironmentLabel.Text = _localizer["ServerDialogLabelEnvironment"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_EnvironmentCmb, DlgSrv_EnvironmentLabel);
        DlgSrv_FavoriteLabel.Text = _localizer["ServerDialogLabelFavorite"];
        DlgSrv_MarkFavoriteCb.Content = _localizer["ServerDialogMarkFavorite"];
        DlgSrv_EnvNone.Content = _localizer["ServerDialogEnvNone"];
        DlgSrv_EnvProduction.Content = _localizer["ServerDialogEnvProduction"];
        DlgSrv_EnvStaging.Content = _localizer["ServerDialogEnvStaging"];
        DlgSrv_EnvLab.Content = _localizer["ServerDialogEnvLab"];
        DlgSrv_EnvPersonal.Content = _localizer["ServerDialogEnvPersonal"];

        // Metadata
        DlgSrv_MetadataTitle.Text = _localizer["ServerDialogMetadata"];
        DlgSrv_MetadataDesc.Text = _localizer["ServerDialogMetadataDesc"];
        DlgSrv_TagsLabel.Text = _localizer["ServerDialogLabelTags"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_TagsBox, DlgSrv_TagsLabel);
        DlgSrv_MacAddressLabel.Text = _localizer["ServerDialogLabelMacAddress"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_MacAddressBox, DlgSrv_MacAddressLabel);
        DlgSrv_VaultEntryNameLabel.Text = _localizer["ServerDialogLabelVaultEntryName"];
        DlgSrv_VaultEntryNameHint.Text = _localizer["ServerDialogVaultEntryNameHelp"];
        System.Windows.Automation.AutomationProperties.SetLabeledBy(DlgSrv_VaultEntryNameBox, DlgSrv_VaultEntryNameLabel);

        // Action buttons
        DlgSrv_CancelBtn.Content = _localizer["ServerDialogBtnCancel"];
        DlgSrv_SaveBtn.Content = _localizer["ServerDialogBtnSave"];
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_CancelBtn, _localizer["ServerDialogBtnCancel"]);
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_SaveBtn, _localizer["ServerDialogBtnSave"]);

        // Accessibility: protocol card automation names
        System.Windows.Automation.AutomationProperties.SetName(ProtocolCard_Rdp, _localizer["ServerDialogProtocolRdpName"]);
        System.Windows.Automation.AutomationProperties.SetName(ProtocolCard_Ssh, _localizer["ServerDialogProtocolSshName"]);
        System.Windows.Automation.AutomationProperties.SetName(ProtocolCard_WinRm, _localizer["ServerDialogProtocolWinRmName"]);
        System.Windows.Automation.AutomationProperties.SetName(ProtocolCard_Sftp, _localizer["ServerDialogProtocolSftpName"]);
        System.Windows.Automation.AutomationProperties.SetName(ProtocolCard_Vnc, _localizer["ServerDialogProtocolVncName"]);
        System.Windows.Automation.AutomationProperties.SetName(ProtocolCard_Telnet, _localizer["ServerDialogProtocolTelnetName"]);
        System.Windows.Automation.AutomationProperties.SetName(ProtocolCard_Ftp, _localizer["ServerDialogProtocolFtpName"]);
        System.Windows.Automation.AutomationProperties.SetName(ProtocolCard_Citrix, _localizer["ServerDialogProtocolCitrixName"]);
        System.Windows.Automation.AutomationProperties.SetName(ProtocolCard_Local, _localizer["ServerDialogProtocolLocalName"]);

        // Accessibility: automation names for PasswordBox controls (basic auth section)
        System.Windows.Automation.AutomationProperties.SetName(RdpPasswordBox, _localizer["ServerDialogLabelPassword"]);
        System.Windows.Automation.AutomationProperties.SetName(SshPasswordBox, _localizer["ServerDialogLabelPassword"]);
        System.Windows.Automation.AutomationProperties.SetName(SshKeyPassphraseBox, _localizer["ServerDialogLabelKeyPassphrase"]);
        System.Windows.Automation.AutomationProperties.SetName(WinRmPasswordBox, _localizer["ServerDialogLabelPassword"]);
        System.Windows.Automation.AutomationProperties.SetName(FtpPasswordBox, _localizer["ServerDialogFtpPassword"]);
        System.Windows.Automation.AutomationProperties.SetName(VncPasswordBox, _localizer["ServerDialogVncPassword"]);
        System.Windows.Automation.AutomationProperties.SetName(TelnetPasswordBox, _localizer["ServerDialogLabelPassword"]);

        // Accessibility: automation names for basic auth browse button
        System.Windows.Automation.AutomationProperties.SetName(DlgSrv_BasicBrowseBtn, _localizer["ServerDialogBtnBrowse"]);
    }

    private void PopulateElevationModeCombo()
    {
        if (DataContext is not ServerDialogViewModel vm || _localizer is null) return;

        DlgSrv_ElevationModeCmb.Items.Clear();
        DlgSrv_ElevationModeCmb.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = _localizer["ElevationModeNone"],
            Tag = Core.Models.ElevationMode.None
        });
        DlgSrv_ElevationModeCmb.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = _localizer["ElevationModeAuto"],
            Tag = Core.Models.ElevationMode.Auto
        });
        DlgSrv_ElevationModeCmb.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = _localizer["ElevationModeGsudo"],
            Tag = Core.Models.ElevationMode.Gsudo
        });
        DlgSrv_ElevationModeCmb.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = _localizer["ElevationModeRunas"],
            Tag = Core.Models.ElevationMode.Runas
        });

        // Select current value
        for (int i = 0; i < DlgSrv_ElevationModeCmb.Items.Count; i++)
        {
            if (DlgSrv_ElevationModeCmb.Items[i] is System.Windows.Controls.ComboBoxItem item
                && item.Tag is Core.Models.ElevationMode mode && mode == vm.ElevationMode)
            {
                DlgSrv_ElevationModeCmb.SelectedIndex = i;
                break;
            }
        }

        DlgSrv_ElevationModeCmb.SelectionChanged += (_, _) =>
        {
            if (DlgSrv_ElevationModeCmb.SelectedItem is System.Windows.Controls.ComboBoxItem selected
                && selected.Tag is Core.Models.ElevationMode selectedMode)
            {
                vm.ElevationMode = selectedMode;
            }
        };

        System.Windows.Automation.AutomationProperties.SetName(
            DlgSrv_ElevationModeCmb, _localizer["ServerDialogElevationMode"]);
    }
}
