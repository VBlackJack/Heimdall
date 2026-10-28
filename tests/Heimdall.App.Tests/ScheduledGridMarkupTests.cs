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
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Heimdall.App.Localization;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The Scheduled grid, as MainWindow.xaml declares it, loads and describes a task in the
/// language of the session.
/// </summary>
/// <remarks>
/// <para>
/// A first version of the grid nested one <c>MultiBinding</c> inside another to name its rows.
/// WPF's binding collection accepts only <c>Binding</c> items, so loading the window threw a
/// <c>XamlParseException</c> at line 2647 and the product never showed a window: every test that
/// launches it timed out waiting for the session tree. Nothing that did not launch the product
/// could see it, because the markup is only read when the window is created.
/// </para>
/// <para>
/// This test reads the real markup of the grid out of MainWindow.xaml and loads it with the
/// framework's own parser, which is the step that failed. Only what the application resources
/// would supply is replaced: the shared styles, the event handler, and the converters.
/// </para>
/// </remarks>
public sealed class ScheduledGridMarkupTests
{
    private const string MainWindowRelativePath = "src/Heimdall.App/MainWindow.xaml";
    private const string ScheduledItemsSource = "{Binding Scheduled.Tasks}";
    private const string ScheduleColumnName = "Mw_ScheduledColSchedule";

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] AttributesThatNeedTheApplication =
        ["Style", "ColumnHeaderStyle", "CellStyle", "MouseDoubleClick", "ItemsSource", "SelectedItem"];

    [Theory]
    [InlineData("en", "Scheduled task on srv-a, type SSH, schedule Every 30 min", "Every 30 min")]
    [InlineData("fr", "Tâche planifiée sur srv-a, type SSH, planification Toutes les 30 min", "Toutes les 30 min")]
    [InlineData("es", null, "Cada 30 min")]
    public void TheGridLoadsAndDescribesATaskInTheLocale(string locale, string? expectedRowName, string expectedSchedule)
    {
        RunOnStaThread(() =>
        {
            LocalizationManager localizer = new();
            localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale).GetAwaiter().GetResult();
            LocalizationSource.Instance.Initialize(localizer);

            DataGrid grid = LoadScheduledGrid();
            ScheduledTaskDto task = new()
            {
                ServerName = "srv-a",
                ConnectionType = "SSH",
                ScheduleType = "Interval",
                IntervalMinutes = 30,
            };
            grid.ItemsSource = new[] { task };
            grid.Measure(new Size(800, 400));
            grid.Arrange(new Rect(0, 0, 800, 400));
            grid.UpdateLayout();

            var column = (DataGridColumn)grid.FindName(ScheduleColumnName);
            var cell = (TextBlock)column.GetCellContent(task);
            Assert.Equal(expectedSchedule, cell.Text);

            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(task);
            string name = AutomationProperties.GetName(row);
            if (expectedRowName is not null)
            {
                Assert.Equal(expectedRowName, name);
            }

            Assert.EndsWith(expectedSchedule, name, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A language change re-describes a task already on screen, without reloading the tasks.
    /// </summary>
    [Fact]
    public void ALanguageChangeRedescribesTheTaskOnScreen()
    {
        RunOnStaThread(() =>
        {
            LocalizationManager localizer = new();
            string locales = Path.Combine(AppContext.BaseDirectory, "locales");
            localizer.LoadAsync(locales, "en").GetAwaiter().GetResult();
            LocalizationSource.Instance.Initialize(localizer);

            DataGrid grid = LoadScheduledGrid();
            ScheduledTaskDto task = new() { ServerName = "srv-a", ScheduleType = "Daily", TimeOfDay = "07:45" };
            grid.ItemsSource = new[] { task };
            grid.Measure(new Size(800, 400));
            grid.Arrange(new Rect(0, 0, 800, 400));
            grid.UpdateLayout();
            var column = (DataGridColumn)grid.FindName(ScheduleColumnName);
            Assert.Equal("Daily at 07:45", ((TextBlock)column.GetCellContent(task)).Text);

            localizer.SwitchLocaleAsync("fr").GetAwaiter().GetResult();

            // The binding engine applies the change through the dispatcher.
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            grid.UpdateLayout();

            Assert.Equal("Tous les jours à 07:45", ((TextBlock)column.GetCellContent(task)).Text);
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(task);
            Assert.EndsWith("Tous les jours à 07:45", AutomationProperties.GetName(row), StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A MultiBinding holds Binding items only. Nesting one is a load-time failure of the whole
    /// window, whichever grid or panel it sits in.
    /// </summary>
    [Fact]
    public void NoXamlFileNestsAMultiBindingInAMultiBinding()
    {
        string root = FindRepoRoot();
        List<string> nested = [];
        int multiBindings = 0;
        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(root, "src"), "*.xaml", SearchOption.AllDirectories))
        {
            foreach (XElement multi in XDocument.Load(file).Descendants(Presentation + "MultiBinding"))
            {
                multiBindings++;
                if (multi.Elements(Presentation + "MultiBinding").Any())
                {
                    nested.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        Assert.True(multiBindings >= 50, $"only {multiBindings} MultiBinding elements were found");
        Assert.True(nested.Count == 0, "a MultiBinding holds Binding items only: " + string.Join(", ", nested.Distinct()));
    }

    private static DataGrid LoadScheduledGrid()
    {
        string path = Path.Combine(FindRepoRoot(), MainWindowRelativePath.Replace('/', Path.DirectorySeparatorChar));
        XElement source = XDocument.Load(path)
            .Descendants(Presentation + "DataGrid")
            .Single(grid => string.Equals((string?)grid.Attribute("ItemsSource"), ScheduledItemsSource, StringComparison.Ordinal));

        XElement grid = new(source);
        foreach (string attribute in AttributesThatNeedTheApplication)
        {
            grid.Attribute(attribute)?.Remove();
        }

        // The row style is based on a shared style the application resources define.
        foreach (XElement style in grid.Descendants(Presentation + "Style"))
        {
            style.Attribute("BasedOn")?.Remove();
        }

        // Clones keep the namespace declarations of the document root only when they are
        // declared again, and the markup uses the loc and converters prefixes.
        XNamespace loc = "clr-namespace:Heimdall.App.Localization;assembly=Heimdall";
        XNamespace converters = "clr-namespace:Heimdall.App.Converters;assembly=Heimdall";
        grid.SetAttributeValue(XNamespace.Xmlns + "loc", loc.NamespaceName);
        grid.SetAttributeValue(XNamespace.Xmlns + "converters", converters.NamespaceName);
        grid.SetAttributeValue(XNamespace.Xmlns + "x", Xaml.NamespaceName);

        grid.AddFirst(new XElement(
            Presentation + "DataGrid.Resources",
            new XElement(converters + "AccessibleRowNameConverter", new XAttribute(Xaml + "Key", "AccessibleRowNameConverter")),
            new XElement(converters + "ScheduledTaskScheduleConverter", new XAttribute(Xaml + "Key", "ScheduledTaskScheduleConverter")),
            new XElement(converters + "ScheduledTaskAccessibleRowNameConverter", new XAttribute(Xaml + "Key", "ScheduledTaskAccessibleRowNameConverter"))));

        return (DataGrid)XamlReader.Parse(grid.ToString());
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }
}
