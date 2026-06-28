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
using Heimdall.Core.Localization;

namespace Heimdall.App.Views.Dialogs;

public partial class MacroEditorDialog : Window
{
    private readonly LocalizationManager? _localizer;

    public MacroEditorDialog(LocalizationManager? localizer = null)
    {
        _localizer = localizer;
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        Loaded += (_, _) => MacroNameTextBox.Focus();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MacroEditorDialogViewModel vm && vm.TrySave())
        {
            DialogResult = true;
        }
    }

    private void OnDeleteMacroClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MacroEditorDialogViewModel vm)
        {
            return;
        }

        var title = _localizer?["MacroEditorDeleteConfirmTitle"] ?? "Delete macro";
        var message = _localizer?["MacroEditorDeleteConfirmMessage"]
            ?? "Delete this macro? This cannot be undone.";
        var yes = _localizer?["BtnDelete"] ?? "Delete";
        var no = _localizer?["BtnCancel"] ?? "Cancel";
        if (!MessageDialog.ShowConfirm(this, title, message, "warning", yes, no))
        {
            return;
        }

        vm.RequestDelete();
        DialogResult = true;
    }
}
