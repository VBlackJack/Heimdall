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
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// The local and the remote file panes scroll sideways only when their columns really exceed them.
/// </summary>
/// <remarks>
/// <para>Both panes stretch their name column to fill the list. The stretch counted ten pixels of
/// row chrome where the shared row style draws twelve, so the rows came out two pixels wider than
/// the viewport; and below its minimum the stretch stopped applying at all, leaving the name column
/// at whatever width it last had, so a narrowed pane kept a scrollbar over columns that would have
/// fitted at the minimum.</para>
/// <para>The list is built from the shared row style the two panes use, not from the panes
/// themselves: building a pane needs the application resources, and an application instance that
/// lays out templated rows leaves state behind that crashes the next one in the same process.</para>
/// </remarks>
public sealed class FileBrowserPaneHorizontalFitTests
{
    /// <summary>Enough rows to bring up the vertical scrollbar at <see cref="PaneHeight"/>.</summary>
    private const int RowCount = 80;

    private const double PaneHeight = 400;

    private const string SizeChangedMember = "private void OnFileListSizeChanged(object sender, SizeChangedEventArgs e)";

    /// <summary>The fixed columns of the local pane: size and modification date.</summary>
    private static readonly double[] OtherColumns = [80, 130];

    [Theory]
    [InlineData(480)]
    [InlineData(700)]
    [InlineData(1000)]
    public void AWideEnoughPaneHasNoHorizontalOverflow(double paneWidth)
    {
        RunOnStaThread(() =>
        {
            (_, ScrollViewer scroller) = FittedList(paneWidth);

            Assert.Equal(Visibility.Visible, scroller.ComputedVerticalScrollBarVisibility);
            Assert.True(
                scroller.ExtentWidth <= scroller.ViewportWidth,
                $"at {paneWidth} px the rows are {scroller.ExtentWidth} px wide in a {scroller.ViewportWidth} px viewport");
            Assert.Equal(Visibility.Collapsed, scroller.ComputedHorizontalScrollBarVisibility);
        });
    }

    [Fact]
    public void APaneNarrowerThanTheMinimumColumnsScrollsSideways()
    {
        RunOnStaThread(() =>
        {
            double tooNarrow = SftpViewMetrics.MinimumNameColumnWidth + OtherColumns.Sum();
            (ListView list, ScrollViewer scroller) = FittedList(tooNarrow);

            Assert.Equal(SftpViewMetrics.MinimumNameColumnWidth, ((GridView)list.View).Columns[0].Width);
            Assert.Equal(Visibility.Visible, scroller.ComputedHorizontalScrollBarVisibility);
        });
    }

    [Fact]
    public void ANarrowedPaneShrinksTheNameColumnToItsMinimum()
    {
        double justBelow = SftpViewMetrics.MinimumNameColumnWidth
            + OtherColumns.Sum()
            + SftpViewMetrics.FileListRowChromeWidth;

        Assert.Equal(
            SftpViewMetrics.MinimumNameColumnWidth,
            SftpViewMetrics.FitNameColumnWidth(justBelow, OtherColumns.Sum(), verticalScrollBarWidth: 17));
    }

    [Fact]
    public void TheRowChromeConstantIsWhatTheSharedRowStyleDraws()
    {
        RunOnStaThread(() =>
        {
            (ListView list, _) = FittedList(700);
            var row = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0);
            double columns = ((GridView)list.View).Columns.Sum(column => column.ActualWidth);

            Assert.Equal(SftpViewMetrics.FileListRowChromeWidth, row.ActualWidth - columns);
        });
    }

    [Theory]
    [InlineData("LocalFileBrowserView.xaml.cs", "gridView.Columns[0].Width = SftpViewMetrics.FitNameColumnWidth(")]
    [InlineData("EmbeddedSftpView.xaml.cs", "nameColumn.Width = SftpViewMetrics.FitNameColumnWidth(")]
    public void BothPanesStretchTheNameColumnThroughTheSharedFit(string file, string statement)
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(
                File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", file))),
            SizeChangedMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, statement),
            $"{file} does not stretch its name column through the shared fit");
    }

    private static (ListView List, ScrollViewer Scroller) FittedList(double paneWidth)
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Width = 250 });
        foreach (double width in OtherColumns)
        {
            view.Columns.Add(new GridViewColumn { Width = width });
        }

        var list = new ListView
        {
            View = view,
            BorderThickness = new Thickness(0),
            ItemContainerStyle = (Style)SharedRowStyles()["FileBrowserRowStyle"],
            ItemsSource = Enumerable.Range(0, RowCount).Select(i => $"entry-{i:D3}.txt").ToList(),
        };

        Layout(list, paneWidth);
        view.Columns[0].Width = SftpViewMetrics.FitNameColumnWidth(
            list.ActualWidth,
            view.Columns.Skip(1).Sum(column => column.ActualWidth),
            SystemParameters.VerticalScrollBarWidth);
        Layout(list, paneWidth);

        ScrollViewer? scroller = FindDescendant<ScrollViewer>(list);
        Assert.NotNull(scroller);
        return (list, scroller);
    }

    private static void Layout(UIElement element, double width)
    {
        element.Measure(new Size(width, PaneHeight));
        element.Arrange(new Rect(0, 0, width, PaneHeight));
        element.UpdateLayout();
    }

    /// <summary>
    /// The shared row style, parsed as loose markup: its <c>local</c> namespace names no assembly,
    /// which only compiled markup resolves on its own.
    /// </summary>
    private static ResourceDictionary SharedRowStyles()
    {
        string markup = File.ReadAllText(
            Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Themes", "FileBrowserStyles.xaml"));
        string loose = markup.Replace(
            "clr-namespace:Heimdall.App.Views\"",
            "clr-namespace:Heimdall.App.Views;assembly=" + typeof(SftpViewMetrics).Assembly.GetName().Name + "\"",
            StringComparison.Ordinal);
        Assert.NotEqual(markup, loose);
        return (ResourceDictionary)XamlReader.Parse(loose);
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
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
}
