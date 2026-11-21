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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views;
using Heimdall.Core.Models;

namespace Heimdall.App.UiTests.Views;

[Collection(DesktopUiCollection.Name)]
public sealed class SplitContainerControlSmokeTests
{
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void ReloadAfterUnload_RebindsLifecycleHandlersAndModel()
    {
        WpfTestHost.Invoke(() =>
        {
            var model = new SplitContainerModel
            {
                First = new SessionPaneModel { PaneId = "left" },
                Second = new SessionPaneModel { PaneId = "right" },
                Orientation = SplitOrientation.Vertical
            };
            var control = new SplitContainerControl
            {
                DataContext = model,
                Width = 400,
                Height = 240
            };
            var host = new Grid();
            var window = new Window
            {
                Content = host,
                Width = 480,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 120,
                Top = 120,
                ShowInTaskbar = false
            };

            try
            {
                host.Children.Add(control);
                window.Show();
                window.UpdateLayout();

                var rootGrid = Assert.IsType<Grid>(control.FindName("RootGrid"));
                AssertVerticalLayout(rootGrid);

                host.Children.Remove(control);
                window.UpdateLayout();
                FlushDispatcher();
                Assert.False(control.IsLoaded);

                model.Orientation = SplitOrientation.Horizontal;

                host.Children.Add(control);
                window.UpdateLayout();
                FlushDispatcher();

                Assert.True(control.IsLoaded);
                AssertHorizontalLayout(rootGrid);

                model.Orientation = SplitOrientation.Vertical;
                FlushDispatcher();

                AssertVerticalLayout(rootGrid);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// GridSplitter (ShowsPreview) applies a drag to the column lengths on release, before any
    /// layout pass: the model must record the dragged split, not the one from before the drag.
    /// </summary>
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void SplitterDrag_RecordsTheDraggedRatio()
    {
        WithVerticalSplit(InitialRatio, (model, rootGrid, splitter) =>
        {
            var expected = DragSplitter(rootGrid, splitter, DragOffset);

            Assert.Equal(expected, model.SplitRatio, 3);
            Assert.Equal(expected, StarRatio(rootGrid), 3);
        });
    }

    /// <summary>
    /// After a drag, a double-click on the splitter moves the panes back. The second press of the
    /// double-click starts a splitter drag of its own, released after the reset was laid out: its
    /// release must neither undo the reset nor fault on the rebuilt layout.
    /// </summary>
    [StaTheory]
    [Trait("Category", "RequiresDesktop")]
    [InlineData(true)]
    [InlineData(false)]
    public void DoubleClickAfterDrag_MovesThePanesBack(bool doubleClickBeforeDragStart)
    {
        WithVerticalSplit(InitialRatio, (model, rootGrid, splitter) =>
        {
            var dragged = DragSplitter(rootGrid, splitter, DragOffset);
            Assert.Equal(dragged, model.SplitRatio, 3);

            var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent
            };
            if (doubleClickBeforeDragStart)
            {
                splitter.RaiseEvent(doubleClick);
                splitter.RaiseEvent(new DragStartedEventArgs(0, 0));
            }
            else
            {
                splitter.RaiseEvent(new DragStartedEventArgs(0, 0));
                splitter.RaiseEvent(doubleClick);
            }

            var resetRatio = model.SplitRatio;
            Assert.NotEqual(dragged, resetRatio, 2);

            // The deferred layout pass applies the reset before the button is released.
            FlushDispatcher();
            splitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            FlushDispatcher();

            Assert.Equal(resetRatio, model.SplitRatio, 3);
            Assert.Equal(resetRatio, StarRatio(rootGrid), 3);
            Assert.Equal(resetRatio, ActualRatio(rootGrid), 2);
        });
    }

    private const double InitialRatio = 0.7;
    private const double DragOffset = -150;

    private static void WithVerticalSplit(
        double ratio, Action<SplitContainerModel, Grid, GridSplitter> scenario)
    {
        WpfTestHost.Invoke(() =>
        {
            var model = new SplitContainerModel
            {
                First = new SessionPaneModel { PaneId = "left" },
                Second = new SessionPaneModel { PaneId = "right" },
                Orientation = SplitOrientation.Vertical,
                SplitRatio = ratio
            };
            var control = new SplitContainerControl
            {
                DataContext = model,
                Width = 600,
                Height = 240
            };
            var window = new Window
            {
                Content = control,
                Width = 680,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 120,
                Top = 120,
                ShowInTaskbar = false
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                FlushDispatcher();

                var rootGrid = Assert.IsType<Grid>(control.FindName("RootGrid"));
                var splitter = Assert.IsType<GridSplitter>(control.FindName("Splitter"));
                Assert.Equal(ratio, ActualRatio(rootGrid), 2);

                scenario(model, rootGrid, splitter);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Drives the splitter the way a mouse drag does; returns the ratio it should leave.</summary>
    private static double DragSplitter(Grid rootGrid, GridSplitter splitter, double offset)
    {
        var first = rootGrid.ColumnDefinitions[0].ActualWidth;
        var second = rootGrid.ColumnDefinitions[2].ActualWidth;

        splitter.RaiseEvent(new DragStartedEventArgs(0, 0));
        splitter.RaiseEvent(new DragDeltaEventArgs(offset, 0));
        splitter.RaiseEvent(new DragCompletedEventArgs(offset, 0, false));
        FlushDispatcher();

        return (first + offset) / (first + second);
    }

    private static double StarRatio(Grid rootGrid)
    {
        Assert.Equal(3, rootGrid.ColumnDefinitions.Count);
        var first = rootGrid.ColumnDefinitions[0].Width;
        var second = rootGrid.ColumnDefinitions[2].Width;
        Assert.True(first.IsStar && second.IsStar);
        return first.Value / (first.Value + second.Value);
    }

    private static double ActualRatio(Grid rootGrid)
    {
        Assert.Equal(3, rootGrid.ColumnDefinitions.Count);
        var first = rootGrid.ColumnDefinitions[0].ActualWidth;
        var second = rootGrid.ColumnDefinitions[2].ActualWidth;
        return first / (first + second);
    }

    private static void AssertVerticalLayout(Grid rootGrid)
    {
        Assert.Single(rootGrid.RowDefinitions);
        Assert.Equal(3, rootGrid.ColumnDefinitions.Count);
    }

    private static void AssertHorizontalLayout(Grid rootGrid)
    {
        Assert.Equal(3, rootGrid.RowDefinitions.Count);
        Assert.Single(rootGrid.ColumnDefinitions);
    }

    private static void FlushDispatcher()
        => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
