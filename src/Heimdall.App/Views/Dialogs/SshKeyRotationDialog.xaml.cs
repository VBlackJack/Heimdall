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

using System.IO;
using System.Windows;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Guided modal wizard for safely rotating an SSH profile's private/public key pair.
/// </summary>
public partial class SshKeyRotationDialog : Window
{
    private SshKeyRotationDialogViewModel? _vm;

    public SshKeyRotationDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.CloseRequested -= OnCloseRequested;
        }

        _vm = e.NewValue as SshKeyRotationDialogViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += OnCloseRequested;
        }
    }

    private void OnPassphraseChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.NewKeyPassphrase = PassphraseBox.Password;
        }
    }

    private void OnBrowseOutputClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(_vm.NewPrivateKeyPath),
            InitialDirectory = Path.GetDirectoryName(_vm.NewPrivateKeyPath)
        };
        if (dlg.ShowDialog() == true)
        {
            _vm.NewPrivateKeyPath = dlg.FileName;
        }
    }

    private void OnCloseRequested(bool succeeded)
    {
        DialogResult = succeeded;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm?.CancelActiveRotation();
        if (_vm is not null)
        {
            _vm.CloseRequested -= OnCloseRequested;
        }

        DataContextChanged -= OnDataContextChanged;
        base.OnClosed(e);
    }
}
