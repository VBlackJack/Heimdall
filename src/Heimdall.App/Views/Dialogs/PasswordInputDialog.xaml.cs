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
using System.Windows.Input;
using Heimdall.App.Theming;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Password input dialog used when an SSH password is required for an external tool.
/// </summary>
public partial class PasswordInputDialog : Window
{
    public PasswordInputDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        Loaded += (_, _) => PasswordBox.Focus();
        Closed += (_, _) => PasswordBox.Clear();
    }

    public string Prompt
    {
        get => PromptText.Text;
        set => PromptText.Text = value;
    }

    public string? ResultPassword { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        SubmitPassword();
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitPassword();
            e.Handled = true;
        }
    }

    private void SubmitPassword()
    {
        ResultPassword = PasswordBox.Password;
        PasswordBox.Clear();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ResultPassword = null;
        PasswordBox.Clear();
        DialogResult = false;
    }
}
