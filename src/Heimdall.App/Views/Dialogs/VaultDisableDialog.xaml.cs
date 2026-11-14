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
/// "Disable master password" dialog. Reads the authorization password via
/// <c>SecurePassword</c>; the ViewModel zeroes the buffer.
/// </summary>
public partial class VaultDisableDialog : Window
{
    public VaultDisableDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        Loaded += (_, _) => PasswordBox.Focus();
        DataContextChanged += OnDataContextChanged;
        PasswordBoxRefocus.Attach(this, PasswordBox);
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
        if (e.PropertyName == nameof(VaultDisableDialogViewModel.IsCompleted)
            && DataContext is VaultDisableDialogViewModel { IsCompleted: true })
        {
            PasswordBox.Clear();
            DialogResult = true;
        }
    }

    private void OnDisableClick(object sender, RoutedEventArgs e) => Submit();

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is VaultDisableDialogViewModel vm)
        {
            vm.RetryRestoreCommand.Execute(null);
        }
    }

    private void OnDropUnreadableClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VaultDisableDialogViewModel vm)
        {
            return;
        }

        using var secure = PasswordBox.SecurePassword;
        var password = SecurePasswordHelper.ToChars(secure);
        vm.DropUnreadableCommand.Execute(password); // ViewModel owns and zeroes the buffer.
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Submit();
            e.Handled = true;
        }
    }

    private void Submit()
    {
        if (DataContext is not VaultDisableDialogViewModel vm)
        {
            return;
        }

        using var secure = PasswordBox.SecurePassword;
        var password = SecurePasswordHelper.ToChars(secure);
        vm.DisableCommand.Execute(password); // ViewModel owns and zeroes the buffer.
    }

    private bool IsRetryPending => DataContext is VaultDisableDialogViewModel { IsRetryPending: true };

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // Cancel and Escape do not dismiss the dialog while a retry is pending: the way out is the
        // explicit action below.
        if (IsRetryPending)
        {
            return;
        }

        PasswordBox.Clear();
        DialogResult = false;
    }

    private bool _closeAllowed;

    private void OnCloseRestoreLaterClick(object sender, RoutedEventArgs e)
    {
        _closeAllowed = true;
        PasswordBox.Clear();
        DialogResult = false;
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsRetryPending && !_closeAllowed)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
