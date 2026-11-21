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

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views;

/// <summary>
/// Recursive split container view. Hosts two children (each rendered via implicit
/// DataTemplate as either <see cref="SessionPaneControl"/> or another
/// <see cref="SplitContainerControl"/>) separated by a <see cref="GridSplitter"/>.
/// </summary>
public partial class SplitContainerControl : UserControl
{
    private SplitContainerModel? _model;
    private bool _layoutDirty;

    public SplitContainerControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Splitter.DragCompleted += OnSplitterDragCompleted;
        Splitter.MouseDoubleClick += OnSplitterDoubleClick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _model ??= DataContext as SplitContainerModel;
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.PropertyChanged += OnModelPropertyChanged;
        }

        SyncContent();
        ApplyLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
        }

        _model = e.NewValue as SplitContainerModel;

        if (_model is not null && IsLoaded)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
        }

        SyncContent();
        InvalidateLayout();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SplitContainerModel.Orientation):
            case nameof(SplitContainerModel.SplitRatio):
                InvalidateLayout();
                break;
            case nameof(SplitContainerModel.First):
            case nameof(SplitContainerModel.Second):
                SyncContent();
                InvalidateLayout();
                break;
        }
    }

    private void SyncContent()
    {
        if (!IsLoaded)
        {
            return;
        }

        var first = _model?.First;
        var second = _model?.Second;

        // Avoid unnecessary visual tree manipulations when content hasn't changed
        if (!ReferenceEquals(FirstPresenter.Content, first))
            FirstPresenter.Content = first;
        if (!ReferenceEquals(SecondPresenter.Content, second))
            SecondPresenter.Content = second;
    }

    /// <summary>
    /// Coalesces multiple property changes into a single layout pass.
    /// </summary>
    private void InvalidateLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (_layoutDirty) return;
        _layoutDirty = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _layoutDirty = false;
            if (!IsLoaded)
            {
                return;
            }

            ApplyLayout();
        });
    }

    private void ApplyLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        // Re-sync content on every layout pass (critical for TabControl tab switching)
        SyncContent();

        if (_model is null)
        {
            RootGrid.RowDefinitions.Clear();
            RootGrid.ColumnDefinitions.Clear();
            return;
        }

        // Model clamps SplitRatio to [MinRatio, MaxRatio] in its setter
        var ratio = _model.SplitRatio;

        if (_model.Orientation == SplitOrientation.Horizontal)
        {
            // Stacked: top/bottom
            EnsureDefinitions(rows: 3, columns: 1);
            RootGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            RootGrid.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
            RootGrid.RowDefinitions[1].Height = GridLength.Auto;
            RootGrid.RowDefinitions[2].Height = new GridLength(1 - ratio, GridUnitType.Star);

            Grid.SetRow(FirstPresenter, 0);
            Grid.SetColumn(FirstPresenter, 0);

            Grid.SetRow(Splitter, 1);
            Grid.SetColumn(Splitter, 0);
            Splitter.Height = SplitContainerModel.SplitterThickness;
            Splitter.Width = double.NaN;
            Splitter.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            Splitter.VerticalAlignment = VerticalAlignment.Center;
            Splitter.ResizeDirection = GridResizeDirection.Rows;
            Splitter.Cursor = System.Windows.Input.Cursors.SizeNS;

            Grid.SetRow(SecondPresenter, 2);
            Grid.SetColumn(SecondPresenter, 0);
        }
        else
        {
            // Side-by-side: left/right
            EnsureDefinitions(rows: 1, columns: 3);
            RootGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            RootGrid.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
            RootGrid.ColumnDefinitions[1].Width = GridLength.Auto;
            RootGrid.ColumnDefinitions[2].Width = new GridLength(1 - ratio, GridUnitType.Star);

            Grid.SetRow(FirstPresenter, 0);
            Grid.SetColumn(FirstPresenter, 0);

            Grid.SetRow(Splitter, 0);
            Grid.SetColumn(Splitter, 1);
            Splitter.Width = SplitContainerModel.SplitterThickness;
            Splitter.Height = double.NaN;
            Splitter.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            Splitter.VerticalAlignment = VerticalAlignment.Stretch;
            Splitter.ResizeDirection = GridResizeDirection.Columns;
            Splitter.Cursor = System.Windows.Input.Cursors.SizeWE;

            Grid.SetRow(SecondPresenter, 0);
            Grid.SetColumn(SecondPresenter, 2);
        }
    }

    /// <summary>
    /// Keeps the existing row and column definitions when their shape already matches, so a
    /// ratio change only updates their lengths. The splitter holds the definitions it saw when a
    /// drag started (the second press of a double-click starts one) and, on release, writes
    /// lengths by index into the grid's current ones: replacing them under it would write the
    /// lengths from before the reset over the new definitions, or fault when it measures the
    /// detached ones.
    /// </summary>
    private void EnsureDefinitions(int rows, int columns)
    {
        if (RootGrid.RowDefinitions.Count != rows)
        {
            RootGrid.RowDefinitions.Clear();
            for (var i = 0; i < rows; i++)
                RootGrid.RowDefinitions.Add(new RowDefinition());
        }

        if (RootGrid.ColumnDefinitions.Count != columns)
        {
            RootGrid.ColumnDefinitions.Clear();
            for (var i = 0; i < columns; i++)
                RootGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
    }

    /// <summary>
    /// Restores the split ratio the container opened with when the splitter is double-clicked.
    /// </summary>
    private void OnSplitterDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!IsLoaded || _model is null) return;
        _model.SplitRatio = _model.ResetRatio;
        MainViewModelLocator.FindCurrent()?.Split.RememberSplitRatio(_model);
        e.Handled = true;
    }

    /// <summary>
    /// Captures the splitter position after user drag and writes it back
    /// to the model (preserved across tab switches).
    /// </summary>
    private void OnSplitterDragCompleted(
        object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!IsLoaded || _model is null) return;

        // A click, or the second press of a double-click, completes a drag with no movement: it
        // is not a drag and must not overwrite the ratio (the double-click handler sets it).
        if (e.Canceled || (e.HorizontalChange == 0 && e.VerticalChange == 0)) return;

        // The splitter shows a preview while dragging and applies the move on release, just
        // before this handler, as star lengths. No layout pass has run since, so ActualWidth and
        // ActualHeight still describe the split from before the drag: read the lengths instead.
        double ratio;
        if (_model.Orientation == SplitOrientation.Horizontal && RootGrid.RowDefinitions.Count >= 3)
        {
            ratio = FirstShare(RootGrid.RowDefinitions[0].Height, RootGrid.RowDefinitions[2].Height);
        }
        else if (_model.Orientation == SplitOrientation.Vertical && RootGrid.ColumnDefinitions.Count >= 3)
        {
            ratio = FirstShare(RootGrid.ColumnDefinitions[0].Width, RootGrid.ColumnDefinitions[2].Width);
        }
        else
        {
            return;
        }

        // Guard against NaN/Infinity from collapsed panes
        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
            ratio = SplitContainerModel.DefaultRatio;

        // Model setter clamps to [MinRatio, MaxRatio]
        _model.SplitRatio = ratio;
        MainViewModelLocator.FindCurrent()?.Split.RememberSplitRatio(_model);
    }

    private static double FirstShare(GridLength first, GridLength second)
    {
        if (!first.IsStar || !second.IsStar) return SplitContainerModel.DefaultRatio;
        var total = first.Value + second.Value;
        return total > 0 ? first.Value / total : SplitContainerModel.DefaultRatio;
    }
}
