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
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.Views.Tools;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace Heimdall.App.UiTests;

/// <summary>
/// The Runbooks panel opens from a header button. This finds that button on the rendered view by
/// its accessible name, fires the command it actually carries, and reads the panel's visibility.
/// </summary>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class CommandLibraryRunbooksPanelTests
{
    [StaFact]
    public void TheHeaderButton_OpensAndClosesTheRunbooksPanel()
    {
        Window? window = null;
        Visibility before = Visibility.Visible;
        Visibility opened = Visibility.Collapsed;
        Visibility closed = Visibility.Visible;

        WpfTestHost.Invoke(() =>
        {
            var services = new ServiceCollection();
            services.AddScoped<IBatchService, EmptyBatchService>();

            var viewModel = new CommandLibraryViewModel(
                services.BuildServiceProvider(),
                configManager: null!,
                WpfTestHost.Localizer,
                dialogService: null!,
                gitSyncService: null!,
                transferService: null!);

            var view = new CommandLibraryView { DataContext = viewModel };
            window = new Window
            {
                Width = 800,
                Height = 700,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            window.Show();
            view.UpdateLayout();

            var panel = (FrameworkElement)view.FindName("RunbooksPanel");
            var button = FindButton(view, WpfTestHost.Translate("ToolCmdLibBtnRunbooks"))
                ?? throw new InvalidOperationException("No header button named after the Runbooks panel.");

            before = panel.Visibility;

            Press(button);
            view.UpdateLayout();
            opened = panel.Visibility;

            Press(button);
            view.UpdateLayout();
            closed = panel.Visibility;
        });

        try
        {
            Assert.Equal(Visibility.Collapsed, before);
            Assert.Equal(Visibility.Visible, opened);
            Assert.Equal(Visibility.Collapsed, closed);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    /// <summary>
    /// Fires the button's own command the way a click does, refusing a button the operator could
    /// not press.
    /// </summary>
    private static void Press(Button button)
    {
        Assert.True(button.IsVisible, "The Runbooks button is not on screen.");
        var command = button.Command
            ?? throw new InvalidOperationException("The Runbooks button carries no command.");
        Assert.True(command.CanExecute(button.CommandParameter), "The Runbooks button is disabled.");
        command.Execute(button.CommandParameter);
    }

    private static Button? FindButton(DependencyObject root, string automationName)
        => Descendants(root)
            .OfType<Button>()
            .FirstOrDefault(button => AutomationProperties.GetName(button) == automationName);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>A runbook store with nothing in it: the panel opens on its empty state.</summary>
    private sealed class EmptyBatchService : IBatchService
    {
        public Task<IEnumerable<CommandBatch>> GetAllBatchesAsync()
            => Task.FromResult<IEnumerable<CommandBatch>>([]);

        public Task<CommandBatch?> GetBatchByIdAsync(string id) => Task.FromResult<CommandBatch?>(null);

        public Task<CommandBatch?> GetBatchByPublicIdAsync(Guid publicId) => Task.FromResult<CommandBatch?>(null);

        public Task<CommandBatch> CreateBatchAsync(CommandBatch batch) => Task.FromResult(batch);

        public Task UpdateBatchAsync(CommandBatch batch) => Task.CompletedTask;

        public Task MarkExecutedAsync(string id, DateTime executedAtUtc) => Task.CompletedTask;

        public Task DeleteBatchAsync(string id) => Task.CompletedTask;

        public Task<IEnumerable<CommandBatch>> SearchBatchesAsync(string query) => GetAllBatchesAsync();
    }
}
