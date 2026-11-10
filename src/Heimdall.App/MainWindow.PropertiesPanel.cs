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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Sidebar;

namespace Heimdall.App;

/// <summary>
/// The optional properties panel docked under the session tree.
/// </summary>
/// <remarks>
/// The rows are sized here rather than bound: a <see cref="GridSplitter"/> drag writes the row
/// height as a local value, which would silently replace any binding after the first drag, and a
/// hidden panel must give its whole row back to the tree rather than leave the dragged height empty.
/// </remarks>
public partial class MainWindow
{
    private PropertyChangedEventHandler? _propertiesPanelServerListHandler;
    private PropertyChangedEventHandler? _propertiesPanelSidebarHandler;

    private void WirePropertiesPanel(MainViewModel viewModel)
    {
        _propertiesPanelServerListHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(ServerListViewModel.ShowPropertiesPanel)
                or nameof(ServerListViewModel.PropertiesPanelHeight))
            {
                Dispatcher.BeginInvoke(() => ApplyPropertiesPanelLayout(viewModel));
            }
        };
        viewModel.ServerList.PropertyChanged += _propertiesPanelServerListHandler;

        _propertiesPanelSidebarHandler = (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(SidebarViewModel.IsToolsTabActive), StringComparison.Ordinal))
            {
                Dispatcher.BeginInvoke(() => ApplyPropertiesPanelLayout(viewModel));
            }
        };
        viewModel.Sidebar.PropertyChanged += _propertiesPanelSidebarHandler;

        // The fit was computed only when the panel was toggled, so a panel dragged tall in a
        // maximised window kept its pixels when the window was restored and squeezed the tree.
        SidebarContentGrid.SizeChanged += OnSidebarContentGridSizeChanged;
        PropertiesPanelSplitter.KeyUp += OnPropertiesPanelSplitterKeyUp;
    }

    private void UnwirePropertiesPanel(MainViewModel viewModel)
    {
        if (_propertiesPanelServerListHandler is not null)
        {
            viewModel.ServerList.PropertyChanged -= _propertiesPanelServerListHandler;
        }

        if (_propertiesPanelSidebarHandler is not null)
        {
            viewModel.Sidebar.PropertyChanged -= _propertiesPanelSidebarHandler;
        }

        SidebarContentGrid.SizeChanged -= OnSidebarContentGridSizeChanged;
        PropertiesPanelSplitter.KeyUp -= OnPropertiesPanelSplitterKeyUp;
    }

    private void OnSidebarContentGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged && DataContext is MainViewModel vm)
        {
            ApplyPropertiesPanelLayout(vm);
        }
    }

    private void ApplyPropertiesPanelLayout(MainViewModel viewModel)
    {
        bool shown = SessionPropertiesPanelLayout.IsShown(
            viewModel.ServerList.ShowPropertiesPanel,
            viewModel.Sidebar.IsToolsTabActive);

        Visibility visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        PropertiesPanelSplitter.Visibility = visibility;
        PropertiesPanel.Visibility = visibility;

        if (!shown)
        {
            // The Tools tab hides the panel without turning the preference off: edits left open
            // are settled the same way as when the panel is closed.
            viewModel.ServerList.ResolvePropertiesEditOnPanelHidden();
            PropertiesSplitterRow.Height = new GridLength(0d);
            PropertiesPanelRow.Height = new GridLength(0d);
            PropertiesPanelRow.MinHeight = 0d;
            return;
        }

        double height = SessionPropertiesPanelLayout.FitHeight(
            viewModel.ServerList.PropertiesPanelHeight,
            SidebarContentGrid.ActualHeight);
        PropertiesSplitterRow.Height = new GridLength(SessionPropertiesPanelLayout.SplitterHeight);
        PropertiesPanelRow.MinHeight = SessionPropertiesPanelLayout.MinHeight;
        PropertiesPanelRow.Height = new GridLength(height);
    }

    private void OnPropertiesPanelSplitterDragCompleted(object sender, DragCompletedEventArgs e)
        => CommitPropertiesPanelHeightAfterLayout();

    /// <summary>
    /// The arrow keys move a focused splitter without any drag, so no DragCompleted ever saved a
    /// height resized from the keyboard.
    /// </summary>
    private void OnPropertiesPanelSplitterKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down)
        {
            CommitPropertiesPanelHeightAfterLayout();
        }
    }

    /// <summary>
    /// Saves the panel height once the layout has caught up with the splitter. With a preview,
    /// the splitter moves the row only as the drag completes, and the panel's ActualHeight read
    /// in that same handler is still the height before the drag.
    /// </summary>
    private void CommitPropertiesPanelHeightAfterLayout()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (DataContext is MainViewModel vm && PropertiesPanel.ActualHeight > 0d)
            {
                vm.ServerList.CommitPropertiesPanelHeight(PropertiesPanel.ActualHeight);
            }
        });
    }

    private void OnPropertiesPanelCloseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ServerList.ShowPropertiesPanel = false;
        }
    }
}
