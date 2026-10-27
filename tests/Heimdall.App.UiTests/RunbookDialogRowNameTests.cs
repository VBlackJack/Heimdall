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
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.UiTests;

/// <summary>
/// Pins what the runbook editor's rows say to a screen reader.
/// </summary>
/// <remarks>
/// Measured by UIA on the running app on 2026-10-27: each variable row was announced as
/// <c>Heimdall.App.ViewModels.CommandLibrary.RunbookVariableEntry</c> and each step as
/// <c>Heimdall.App.ViewModels.Dialogs.RunbookStepEditorItem</c>, the defect already fixed on the
/// library's own lists. An ItemsControl container takes its name from <c>ToString()</c> of the
/// item unless its container style sets one; set inside the template, a name is inert.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class RunbookDialogRowNameTests
{
    [StaFact]
    public void EveryRowListNamesItsRowsByWhatTheOperatorReads()
    {
        var names = new Dictionary<string, string[]>();

        Render(dialog =>
        {
            names["variables"] = ContainerNames(dialog, "Variables");
            names["steps"] = ContainerNames(dialog, "Steps");
            names["parameters"] = ContainerNames(dialog, "Parameters");
        });

        Assert.Equal(["{{unit}}"], names["variables"]);
        Assert.Equal(["Restart a unit", "uptime"], names["steps"]);
        // The label the row shows, required marker included, as the library's own form names it.
        Assert.Equal(["Unit *"], names["parameters"]);
    }

    /// <summary>
    /// A literal step is named by its command, so the name follows an edit of that command.
    /// </summary>
    [StaFact]
    public void ALiteralStepNameFollowsItsCommand()
    {
        string? edited = null;

        Render(dialog =>
        {
            var viewModel = (RunbookDialogViewModel)dialog.DataContext;
            viewModel.Steps[1].Command = "uptime -p";
            Settle(dialog);
            edited = ContainerNames(dialog, "Steps")[1];
        });

        Assert.Equal("uptime -p", edited);
    }

    private static void Render(Action<RunbookDialog> read)
    {
        RunbookDialog? dialog = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                var viewModel = RunbookDialogViewModel.FromRunbook(
                    Runbook(),
                    WpfTestHost.Translate,
                    id => id == RestartAction.Id ? RestartAction : null);
                dialog = new RunbookDialog { DataContext = viewModel, ShowInTaskbar = false };
                dialog.Show();
                Settle(dialog);
                read(dialog);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => dialog?.Close());
        }
    }

    private static readonly ActionModel RestartAction = new()
    {
        Id = "restart",
        Title = "Restart a unit",
        Category = "Ops",
        Platform = Platform.Linux,
        LinuxCommandTemplate = new CommandTemplate
        {
            Id = "restart-linux",
            Name = "restart",
            Platform = Platform.Linux,
            CommandPattern = "systemctl restart {unit}",
            Parameters = [new TemplateParameter { Name = "unit", Label = "Unit", Type = "string", Required = true }]
        }
    };

    private static CommandBatch Runbook()
        => new()
        {
            Id = "rb-1",
            Name = "Restart a unit",
            Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = "nginx" },
            Commands =
            [
                new BatchCommandItem
                {
                    Order = 0,
                    ActionId = RestartAction.Id,
                    ActionTitle = RestartAction.Title,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = "{{unit}}" }
                },
                new BatchCommandItem { Order = 1, Command = "uptime", Platform = Platform.Linux }
            ]
        };

    private static string[] ContainerNames(DependencyObject root, string itemsSourcePath)
    {
        var items = Descendants(root).OfType<ItemsControl>()
            .First(control => BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)
                ?.Path?.Path == itemsSourcePath);
        return Enumerable.Range(0, items.Items.Count)
            .Select(index => items.ItemContainerGenerator.ContainerFromIndex(index))
            .Select(container => container is null ? "<no container>" : AutomationProperties.GetName(container))
            .ToArray();
    }

    private static void Settle(FrameworkElement element)
    {
        element.UpdateLayout();
        element.Dispatcher.Invoke(DispatcherPriority.Background, new System.Action(() => { }));
        element.UpdateLayout();
    }

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
}
