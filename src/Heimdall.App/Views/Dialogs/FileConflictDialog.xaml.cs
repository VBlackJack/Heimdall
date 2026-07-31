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

/// <summary>Modal dialog for resolving a complete batch of file collisions.</summary>
public partial class FileConflictDialog : Window
{
    private FileConflictDialogViewModel? _viewModel;

    public FileConflictDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplyButton.Focus();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = e.NewValue as FileConflictDialogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(bool accepted)
    {
        DialogResult = accepted;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        DataContextChanged -= OnDataContextChanged;
        base.OnClosed(e);
    }
}

/// <summary>Production presenter for the WPF batch-conflict dialog.</summary>
internal sealed class WpfFileConflictDialogPresenter : IFileConflictDialogPresenter
{
    public Task<FileConflictDialogResult?> ShowAsync(FileConflictDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var dialog = new FileConflictDialog
        {
            DataContext = viewModel,
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                ?? Application.Current?.MainWindow,
        };

        bool? accepted = dialog.ShowDialog();
        return Task.FromResult(accepted == true ? viewModel.Result : null);
    }
}
