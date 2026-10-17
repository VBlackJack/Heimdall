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
using Heimdall.App.Localization;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// PIN setup dialog. Code-behind handles PasswordBox interaction and maps
/// ViewModel error states to localized text.
/// </summary>
public partial class PinSetupDialog : Window
{
    public PinSetupDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PinSetupDialogViewModel { IsPinSet: true })
        {
            CurrentPinBox.Focus();
        }
        else
        {
            NewPinBox.Focus();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldViewModel)
        {
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (e.NewValue is PinSetupDialogViewModel viewModel)
        {
            ShowError(MapError(viewModel.Error));
        }
        else
        {
            ShowError(string.Empty);
        }
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;

        // Written from code, so no binding raises TargetUpdated for the live region.
        Behaviors.LiveRegionBehavior.Announce(ErrorText);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not PinSetupDialogViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(PinSetupDialogViewModel.IsCompleted) && viewModel.IsCompleted)
        {
            ClearAllPinBoxes();
            DialogResult = true;
            return;
        }

        if (e.PropertyName == nameof(PinSetupDialogViewModel.Error))
        {
            ShowError(MapError(viewModel.Error));
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PinSetupDialogViewModel viewModel)
        {
            return;
        }

        string? currentPin = viewModel.IsPinSet ? CurrentPinBox.Password : null;
        PinSetupInput input = new PinSetupInput(currentPin, NewPinBox.Password, ConfirmPinBox.Password);

        viewModel.SubmitCommand.Execute(input);

        // Clear on every attempt (CWE-316).
        ClearAllPinBoxes();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PinSetupDialogViewModel viewModel)
        {
            return;
        }

        viewModel.RemoveCommand.Execute(CurrentPinBox.Password);

        // Clear on every attempt (CWE-316).
        CurrentPinBox.Clear();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ClearAllPinBoxes();
        DialogResult = false;
    }

    private void ClearAllPinBoxes()
    {
        CurrentPinBox.Clear();
        NewPinBox.Clear();
        ConfirmPinBox.Clear();
    }

    private static string MapError(PinSetupError? error)
    {
        return error switch
        {
            PinSetupError.WrongCurrentPin => LocalizationSource.Instance["PinSetupErrorWrongCurrent"],
            PinSetupError.PinTooShort => LocalizationSource.Instance["PinSetupErrorTooShort"],
            PinSetupError.PinTooLong => LocalizationSource.Instance["PinSetupErrorTooLong"],
            PinSetupError.PinInvalidChars => LocalizationSource.Instance["PinSetupErrorInvalidChars"],
            PinSetupError.ConfirmMismatch => LocalizationSource.Instance["PinSetupErrorConfirmMismatch"],
            null => string.Empty,
            _ => string.Empty
        };
    }
}
