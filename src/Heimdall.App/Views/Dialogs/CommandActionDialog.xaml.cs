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

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Command action add/edit dialog. Static labels, accessibility names, and
/// section visibility are bound in XAML; code-behind handles validation
/// triggering, the unsaved-changes prompt, and DialogResult assignment.
/// </summary>
public partial class CommandActionDialog : Window
{
    public CommandActionDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        Loaded += (_, _) => TxtTitle.Focus();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CommandActionDialogViewModel vm) return;

        vm.ValidateCommand.Execute(null);

        if (vm.ValidationError is null)
        {
            DialogResult = true;
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DialogResult == true) return;
        if (DataContext is not CommandActionDialogViewModel { IsDirty: true } vm) return;

        var title = vm.Localizer?["DialogUnsavedWarningTitle"] ?? "Unsaved Changes";
        var message = vm.Localizer?["DialogUnsavedWarning"]
            ?? "You have unsaved changes. Discard them and close?";
        var yes = vm.Localizer?["BtnYes"] ?? "Yes";
        var no = vm.Localizer?["BtnNo"] ?? "No";

        var discard = MessageDialog.ShowConfirm(this, title, message, "warning", yes, no);
        if (!discard)
        {
            e.Cancel = true;
        }
    }
}
