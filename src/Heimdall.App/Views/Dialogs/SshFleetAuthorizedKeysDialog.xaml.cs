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
using System.Text;
using System.Windows;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Modal dialog for searching SSH <c>authorized_keys</c> across the server inventory
/// and revoking selected keys with audit export.
/// </summary>
public partial class SshFleetAuthorizedKeysDialog : Window
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public SshFleetAuthorizedKeysDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SshFleetAuthorizedKeysDialogViewModel vm
            && !string.IsNullOrWhiteSpace(vm.SearchQuery))
        {
            await vm.ScanFleetCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    private void OnRowSelectionChanged(object sender, RoutedEventArgs e)
    {
        (DataContext as SshFleetAuthorizedKeysDialogViewModel)?.NotifySelectionChanged();
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SshFleetAuthorizedKeysDialogViewModel vm)
        {
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"ssh-authorized-keys-audit-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv",
            DefaultExt = ".csv"
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dlg.FileName, vm.BuildCsvReport(), Utf8NoBom);
    }

    private void OnExportMarkdownClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SshFleetAuthorizedKeysDialogViewModel vm)
        {
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = $"ssh-authorized-keys-audit-{DateTime.UtcNow:yyyyMMdd-HHmm}.md",
            DefaultExt = ".md"
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dlg.FileName, vm.BuildMarkdownReport(), Utf8NoBom);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SshFleetAuthorizedKeysDialogViewModel)?.CancelActiveOperation();
        base.OnClosed(e);
    }
}
