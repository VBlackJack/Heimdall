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
/// Runbook editor. Everything is bound in XAML; the code-behind only closes the window when
/// <see cref="RunbookDialogViewModel.Validate"/> accepts the runbook.
/// </summary>
public partial class RunbookDialog : Window
{
    public RunbookDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        Loaded += (_, _) => TxtName.Focus();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is RunbookDialogViewModel vm && vm.Validate())
        {
            DialogResult = true;
        }
    }
}
