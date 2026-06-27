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
using System.Windows;
using System.Windows.Input;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Master-password unlock dialog shown at startup when a vault is configured.
/// Code-behind handles PasswordBox interaction and closes on successful unlock.
/// Localization is declarative in XAML via <c>{loc:Translate}</c>.
/// </summary>
public partial class VaultUnlockDialog : Window
{
    public VaultUnlockDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        Loaded += (_, _) => PasswordBox.Focus();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultUnlockDialogViewModel.IsVerified)
            && DataContext is VaultUnlockDialogViewModel { IsVerified: true })
        {
            PasswordBox.Clear();
            DialogResult = true;
        }
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e) => SubmitPassword();

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
        if (DataContext is not VaultUnlockDialogViewModel vm)
        {
            return;
        }

        // The ViewModel owns and zeroes this buffer after the attempt.
        var password = PasswordBox.Password.ToCharArray();
        PasswordBox.Clear();
        vm.UnlockCommand.Execute(password);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        PasswordBox.Clear();
        DialogResult = false;
    }
}
