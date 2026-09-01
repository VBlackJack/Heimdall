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
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Models;

namespace Heimdall.App.UiTests.Dialogs;

/// <summary>
/// Guidance the dialog gives before anything has been typed. Both claims here fail silently in
/// markup: a hint whose text is never assigned is an empty TextBlock that reserves no space, and
/// a placeholder set on a control whose template has no watermark part is a property nobody
/// reads. Neither shows up in a XAML guard, so both are measured on a built control.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class ServerDialogFieldGuidanceTests
{
    /// <summary>
    /// The folder field is free text, and the separator is the only thing that turns a flat list
    /// of names into a tree. The hint is assigned in code-behind, next to every other label on
    /// that tab, so the failure to guard against is the assignment simply not being there.
    /// </summary>
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheFolderFieldTeachesItsNestingConventionBeforeAnythingIsTyped()
    {
        WpfTestHost.ResetLocale();

        WpfTestHost.Invoke(() =>
        {
            ServerDialogViewModel viewModel = new()
            {
                DisplayName = "Session",
                RemoteServer = "host.example.com",
                ConnectionType = "SSH",
                IsProtocolSelected = true
            };

            RunOnDialog(viewModel, "DlgSrv_TabInfo", dialog =>
            {
                // Focus decides whether the watermark is drawn, so it is placed deliberately
                // rather than left to whatever the window hands focus to on Show.
                dialog.DlgSrv_DisplayNameBox.Focus();
                dialog.UpdateLayout();

                Assert.False(
                    string.IsNullOrWhiteSpace(dialog.DlgSrv_FolderHint.Text),
                    "The folder hint is empty, so nothing on the tab says that a separator nests "
                    + "one folder inside another.");

                Assert.True(dialog.DlgSrv_FolderHint.IsVisible);
                Assert.Equal(string.Empty, dialog.DlgSrv_FolderBox.Text);

                dialog.DlgSrv_FolderBox.ApplyTemplate();
                TextBlock watermark = Assert.IsType<TextBlock>(
                    dialog.DlgSrv_FolderBox.Template.FindName("Watermark", dialog.DlgSrv_FolderBox));

                Assert.False(
                    string.IsNullOrWhiteSpace(watermark.Text),
                    "The folder field has a watermark slot but nothing to put in it.");
                Assert.Equal(Visibility.Visible, watermark.Visibility);
            });
        });
    }

    /// <summary>
    /// Both halves matter. An empty list showing the sentence is satisfied by a broken binding
    /// too, because a binding that resolves to nothing leaves the default visibility in place;
    /// only the populated half tells the two apart.
    /// </summary>
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheEmptyPostConnectListExplainsItselfAndStopsWhenItFills()
    {
        WpfTestHost.ResetLocale();

        WpfTestHost.Invoke(() =>
        {
            ServerDialogViewModel viewModel = new()
            {
                DisplayName = "Session",
                RemoteServer = "host.example.com",
                ConnectionType = "SSH",
                IsProtocolSelected = true
            };

            RunOnDialog(viewModel, "DlgSrv_TabOptions", dialog =>
            {
                Assert.True(
                    dialog.DlgSrv_PostConnectEmptyState.IsVisible,
                    "A session with no post-connect steps shows an empty card and says nothing "
                    + "about what the list is for.");

                viewModel.LoadPostConnectSteps([new PostConnectStep { Input = "sudo -i", DelayMs = 500 }]);
                dialog.UpdateLayout();

                Assert.False(
                    dialog.DlgSrv_PostConnectEmptyState.IsVisible,
                    "The empty-state sentence is still printed over a list that has a step in it.");
            });
        });
    }

    private static void RunOnDialog(
        ServerDialogViewModel viewModel,
        string tabName,
        Action<ServerDialog> assertions)
    {
        // Seeding a view model by hand marks it dirty, and this test bypasses the dialog service
        // that would clear the flag after seeding. Left set, the Close() below raises the
        // unsaved-changes prompt: a modal nobody is there to answer, which blocks the run.
        viewModel.IsDirty = false;

        ServerDialog? dialog = null;

        try
        {
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
            tabs.SelectedItem = Assert.IsType<TabItem>(dialog.FindName(tabName));
            dialog.UpdateLayout();

            assertions(dialog);
        }
        finally
        {
            viewModel.IsDirty = false;
            dialog?.Close();
        }
    }
}
