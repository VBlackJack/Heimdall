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
using Microsoft.Win32;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// SSH gateway add/edit dialog. Code-behind is limited to PasswordBox handling,
/// file browse, and DialogResult assignment.
/// </summary>
public partial class GatewayDialog : Window
{
    private bool _suppressCredentialDirtyTracking;

    public GatewayDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        Loaded += (_, _) =>
        {
            if (DataContext is GatewayDialogViewModel { Localizer: not null } vm)
            {
                CancelBtn.Content = vm.Localizer["BtnCancel"];
                SaveBtn.Content = vm.Localizer["BtnSave"];
                System.Windows.Automation.AutomationProperties.SetName(CancelBtn, vm.Localizer["BtnCancel"]);
                System.Windows.Automation.AutomationProperties.SetName(SaveBtn, vm.Localizer["BtnSave"]);

                // Form labels
                LblName.Text = vm.Localizer["GatewayDialogLabelName"];
                LblHost.Text = vm.Localizer["GatewayDialogLabelHost"];
                LblPort.Text = vm.Localizer["GatewayDialogLabelPort"];
                LblUsername.Text = vm.Localizer["GatewayDialogLabelUsername"];
                LblKeyPath.Text = vm.Localizer["GatewayDialogLabelKeyPath"];
                // LblPassword is bound to GatewayPasswordLabel on the ViewModel.
                LblKeyPassphrase.Text = vm.Localizer["GatewayDialogLabelPassphrase"];
                LblParentGateway.Text = vm.Localizer["GatewayDialogLabelParentGateway"];
                LblHostKeyFingerprint.Text = vm.Localizer["GatewayDialogLabelHostKeyFingerprint"];
                LblHostKeyFingerprintHint.Text = vm.Localizer["GatewayHostKeyFingerprintHint"];
                BtnBrowse.Content = vm.Localizer["GatewayDialogBtnBrowse"];
                BtnBrowse.ToolTip = vm.Localizer["TooltipBrowse"];
                System.Windows.Automation.AutomationProperties.SetName(BtnBrowse, vm.Localizer["GatewayDialogBtnBrowse"]);

                // Input field accessibility
                System.Windows.Automation.AutomationProperties.SetName(TxtName, vm.Localizer["GatewayDialogLabelName"]);
                System.Windows.Automation.AutomationProperties.SetName(TxtHost, vm.Localizer["GatewayDialogLabelHost"]);
                System.Windows.Automation.AutomationProperties.SetName(TxtPort, vm.Localizer["GatewayDialogLabelPort"]);
                System.Windows.Automation.AutomationProperties.SetName(TxtUsername, vm.Localizer["GatewayDialogLabelUsername"]);
                System.Windows.Automation.AutomationProperties.SetName(TxtKeyPath, vm.Localizer["GatewayDialogLabelKeyPath"]);
                System.Windows.Automation.AutomationProperties.SetName(PasswordBox, vm.GatewayPasswordLabel);
                System.Windows.Automation.AutomationProperties.SetName(KeyPassphraseBox, vm.Localizer["GatewayDialogLabelPassphrase"]);
                System.Windows.Automation.AutomationProperties.SetName(CmbParentGateway, vm.Localizer["GatewayDialogLabelParentGateway"]);
                System.Windows.Automation.AutomationProperties.SetName(TxtHostKeyFingerprint, vm.Localizer["GatewayDialogLabelHostKeyFingerprint"]);

                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(GatewayDialogViewModel.GatewayPasswordLabel))
                        System.Windows.Automation.AutomationProperties.SetName(PasswordBox, vm.GatewayPasswordLabel);
                    if (e.PropertyName == nameof(GatewayDialogViewModel.HasKeyPath) && !vm.HasKeyPath)
                        ClearKeyPassphraseInput();
                };
            }

            TxtName.Focus();
        };
    }

    private void OnCredentialPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressCredentialDirtyTracking
            && DataContext is GatewayDialogViewModel vm)
        {
            vm.IsDirty = true;
        }
    }

    private void ClearCredentialInputs()
    {
        RunWithoutCredentialDirtyTracking(() =>
        {
            PasswordBox.Clear();
            KeyPassphraseBox.Clear();
        });
    }

    private void ClearKeyPassphraseInput()
        => RunWithoutCredentialDirtyTracking(KeyPassphraseBox.Clear);

    private void RunWithoutCredentialDirtyTracking(Action action)
    {
        bool wasSuppressed = _suppressCredentialDirtyTracking;
        _suppressCredentialDirtyTracking = true;
        try
        {
            action();
        }
        finally
        {
            _suppressCredentialDirtyTracking = wasSuppressed;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GatewayDialogViewModel vm)
        {
            return;
        }

        // Transfer PasswordBox value (WPF PasswordBox cannot be databound)
        vm.Password = PasswordBox.Password;
        vm.KeyPassphrase = vm.HasKeyPath ? KeyPassphraseBox.Password : "";

        vm.ValidateCommand.Execute(null);

        if (vm.ValidationError is null)
        {
            DialogResult = true;

            // Clear password from UI memory (CWE-316)
            ClearCredentialInputs();
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Skip dirty check when the user clicked Save (DialogResult == true)
        if (DialogResult == true)
        {
            ClearCredentialInputs();
            return;
        }

        if (DataContext is not GatewayDialogViewModel { IsDirty: true } vm)
        {
            ClearCredentialInputs();
            return;
        }

        var title = vm.Localizer?["DialogUnsavedWarningTitle"] ?? "Unsaved Changes";
        var message = vm.Localizer?["DialogUnsavedWarning"]
            ?? "You have unsaved changes. Discard them and close?";
        var yes = vm.Localizer?["BtnYes"] ?? "Yes";
        var no = vm.Localizer?["BtnNo"] ?? "No";

        var discard = MessageDialog.ShowConfirm(this, title, message, "warning", yes, no);
        if (!discard)
        {
            e.Cancel = true;
            return;
        }

        ClearCredentialInputs();
    }

    private void OnBrowseKeyClick(object sender, RoutedEventArgs e)
    {
        var localizer = (DataContext as GatewayDialogViewModel)?.Localizer;
        var dialog = new OpenFileDialog
        {
            Title = localizer?["GatewayDialogSelectSshKey"] ?? "Select SSH Key",
            Filter = localizer?["ServerDialogBrowseKeyFilter"] ?? "PPK Files (*.ppk)|*.ppk|PEM Files (*.pem)|*.pem|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true && DataContext is GatewayDialogViewModel vm)
        {
            vm.KeyPath = dialog.FileName;
        }
    }
}
