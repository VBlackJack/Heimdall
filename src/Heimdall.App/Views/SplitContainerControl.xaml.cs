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

        ApplyLocalization();
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

        RootGrid.RowDefinitions.Clear();
        RootGrid.ColumnDefinitions.Clear();

        if (_model is null) return;

        // Model clamps SplitRatio to [MinRatio, MaxRatio] in its setter
        var ratio = _model.SplitRatio;

        if (_model.Orientation == SplitOrientation.Horizontal)
        {
            // Stacked: top/bottom
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ratio, GridUnitType.Star) });
            RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - ratio, GridUnitType.Star) });

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
            RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });

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

    private void ApplyLocalization()
    {
        System.Windows.Automation.AutomationProperties.SetName(Splitter, L("A11ySplitPaneResizer"));
    }

    private string L(string key)
    {
        var vm = MainViewModelLocator.FindCurrent();
        return vm?.GetLocalizer()[key] ?? key;
    }

    /// <summary>
    /// Resets the split ratio to 50/50 when the splitter is double-clicked.
    /// </summary>
    private void OnSplitterDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!IsLoaded || _model is null) return;
        _model.SplitRatio = SplitContainerModel.DefaultRatio;
        e.Handled = true;
    }

    /// <summary>
    /// Captures the actual splitter position after user drag and writes it back
    /// to the model (preserved across tab switches).
    /// </summary>
    private void OnSplitterDragCompleted(
        object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!IsLoaded || _model is null) return;

        double ratio;
        if (_model.Orientation == SplitOrientation.Horizontal && RootGrid.RowDefinitions.Count >= 3)
        {
            var first = RootGrid.RowDefinitions[0].ActualHeight;
            var second = RootGrid.RowDefinitions[2].ActualHeight;
            var total = first + second;
            ratio = total > 0 ? first / total : SplitContainerModel.DefaultRatio;
        }
        else if (_model.Orientation == SplitOrientation.Vertical && RootGrid.ColumnDefinitions.Count >= 3)
        {
            var first = RootGrid.ColumnDefinitions[0].ActualWidth;
            var second = RootGrid.ColumnDefinitions[2].ActualWidth;
            var total = first + second;
            ratio = total > 0 ? first / total : SplitContainerModel.DefaultRatio;
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
    }
}
