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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Modal dialog for listing, adding, and safely removing keys in a remote SSH server's
/// <c>~/.ssh/authorized_keys</c>.
/// </summary>
public partial class SshAuthorizedKeysDialog : Window
{
    public SshAuthorizedKeysDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SshAuthorizedKeysDialogViewModel vm)
        {
            await vm.RefreshAsync().ConfigureAwait(true);
        }
    }

    private void OnBrowsePublicKeyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SshAuthorizedKeysDialogViewModel vm)
        {
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = vm.PublicKeyFileFilter,
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > SshKeyAuditEngine.MaxKeyFileSize)
            {
                return;
            }

            vm.NewPublicKeyLine = File.ReadAllText(dlg.FileName, Encoding.UTF8).Trim();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnSearchFleetClick(object sender, RoutedEventArgs e)
    {
        var services = (Application.Current as App)?.Services;
        var configManager = services?.GetService<IConfigManager>();
        var localizer = services?.GetService<LocalizationManager>();
        if (configManager is null || localizer is null)
        {
            return;
        }

        string? query = (DataContext as SshAuthorizedKeysDialogViewModel)?.SelectedEntry?.Fingerprint;
        var fleetVm = new SshFleetAuthorizedKeysDialogViewModel(services, configManager, localizer, query);
        var fleetDialog = new SshFleetAuthorizedKeysDialog
        {
            DataContext = fleetVm,
            Owner = this
        };
        fleetDialog.ShowDialog();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SshAuthorizedKeysDialogViewModel)?.CancelActiveOperation();
        base.OnClosed(e);
    }
}
