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
using Heimdall.App.Services;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// "Enable master password" dialog. Reads the password fields via
/// <c>SecurePassword</c> and the <see cref="SecurePasswordHelper"/>, never as a
/// managed string; the ViewModel zeroes the buffers.
/// </summary>
public partial class VaultEnableDialog : Window
{
    public VaultEnableDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        Loaded += (_, _) => NewPasswordBox.Focus();
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
        if (e.PropertyName == nameof(VaultEnableDialogViewModel.IsCompleted)
            && DataContext is VaultEnableDialogViewModel { IsCompleted: true })
        {
            ClearBoxes();
            DialogResult = true;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VaultEnableDialogViewModel vm)
        {
            return;
        }

        using var newSecure = NewPasswordBox.SecurePassword;
        using var confirmSecure = ConfirmPasswordBox.SecurePassword;
        var newChars = SecurePasswordHelper.ToChars(newSecure);
        var confirmChars = SecurePasswordHelper.ToChars(confirmSecure);
        try
        {
            vm.Evaluate(newChars, confirmChars);
        }
        finally
        {
            Array.Clear(newChars);
            Array.Clear(confirmChars);
        }
    }

    private void OnEnableClick(object sender, RoutedEventArgs e) => Submit();

    private void OnConfirmKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Submit();
            e.Handled = true;
        }
    }

    private void Submit()
    {
        if (DataContext is not VaultEnableDialogViewModel vm)
        {
            return;
        }

        using var secure = NewPasswordBox.SecurePassword;
        var password = SecurePasswordHelper.ToChars(secure);
        vm.EnableCommand.Execute(password); // ViewModel owns and zeroes the buffer.
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ClearBoxes();
        DialogResult = false;
    }

    private void ClearBoxes()
    {
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
    }
}
