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
using System.Windows.Controls;
using System.Windows.Media;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Models;

namespace Heimdall.App.UiTests.Dialogs;

/// <summary>
/// The only oracle here that measures what the user sees. The XAML guards in
/// Heimdall.App.Tests read the template's constants; a layout pass is what settles whether the
/// star column actually resolves to a usable width at the dialog's declared size.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class ServerDialogPostConnectLayoutTests
{
    /// <summary>
    /// A command field narrower than this cannot show a typical post-connect line. Before the
    /// two-row template the same measurement came out under 30 px.
    /// </summary>
    private const double MinimumUsableCommandWidth = 240;

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheCommandFieldIsUsableAtTheDialogsDefaultWidth()
    {
        WpfTestHost.ResetLocale();

        WpfTestHost.Invoke(() =>
        {
            ServerDialog? dialog = null;

            try
            {
                ServerDialogViewModel viewModel = new()
                {
                    DisplayName = "Session",
                    RemoteServer = "host.example.com",
                    ConnectionType = "SSH",
                    IsProtocolSelected = true
                };
                viewModel.LoadPostConnectSteps(
                [
                    new PostConnectStep { Input = "sudo -i", DelayMs = 500 }
                ]);

                // Seeding a view model by hand marks it dirty, and this test bypasses the
                // dialog service that would clear the flag after seeding. Without this line
                // the Close() below raises the unsaved-changes prompt - a modal that no one
                // is going to answer, so the whole suite hangs on a window nobody sees
                // waiting for a click. Measured: the run went from two minutes to blocked.
                viewModel.IsDirty = false;

                dialog = new ServerDialog(WpfTestHost.Localizer)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 120,
                    Top = 120,
                    Width = 650,
                    DataContext = viewModel
                };

                dialog.Show();

                TabControl tabs = Assert.IsType<TabControl>(dialog.FindName("MainTabControl"));
                tabs.SelectedItem = Assert.IsType<TabItem>(dialog.FindName("DlgSrv_TabOptions"));
                dialog.UpdateLayout();

                ListView steps = Assert.IsType<ListView>(dialog.FindName("DlgSrv_PostConnectStepsList"));
                steps.UpdateLayout();

                // Null here means the row was never realized, which measures nothing at all.
                ListViewItem container = Assert.IsType<ListViewItem>(
                    steps.ItemContainerGenerator.ContainerFromIndex(0));

                TextBox commandBox = Assert.Single(
                    Descendants(container).OfType<TextBox>(),
                    box => IsBoundTo(box, "Input"));

                Assert.True(
                    commandBox.ActualWidth >= MinimumUsableCommandWidth,
                    $"The post-connect command field measured {commandBox.ActualWidth} px at the "
                    + $"dialog's declared width, below the {MinimumUsableCommandWidth} px this "
                    + "feature needs to be usable without resizing the window.");
            }
            finally
            {
                dialog?.Close();
            }
        });
    }

    private static bool IsBoundTo(TextBox box, string path)
        => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == path;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;

            foreach (DependencyObject descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
