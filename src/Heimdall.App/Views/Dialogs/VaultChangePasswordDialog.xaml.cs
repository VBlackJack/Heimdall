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
/// "Change master password" dialog. All three fields are read via
/// <c>SecurePassword</c>; the ViewModel zeroes the buffers.
/// </summary>
public partial class VaultChangePasswordDialog : Window
{
    public VaultChangePasswordDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        Loaded += (_, _) => CurrentPasswordBox.Focus();
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
        if (e.PropertyName == nameof(VaultChangePasswordDialogViewModel.IsCompleted)
            && DataContext is VaultChangePasswordDialogViewModel { IsCompleted: true })
        {
            ClearBoxes();
            DialogResult = true;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VaultChangePasswordDialogViewModel vm)
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

    private void OnChangeClick(object sender, RoutedEventArgs e) => Submit();

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
        if (DataContext is not VaultChangePasswordDialogViewModel vm)
        {
            return;
        }

        using var currentSecure = CurrentPasswordBox.SecurePassword;
        using var newSecure = NewPasswordBox.SecurePassword;
        var input = new VaultChangePasswordInput(
            SecurePasswordHelper.ToChars(currentSecure),
            SecurePasswordHelper.ToChars(newSecure));
        vm.ChangeCommand.Execute(input); // ViewModel owns and zeroes the buffers.
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ClearBoxes();
        DialogResult = false;
    }

    private void ClearBoxes()
    {
        CurrentPasswordBox.Clear();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
    }
}
