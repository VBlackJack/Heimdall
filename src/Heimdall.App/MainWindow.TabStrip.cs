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
using Heimdall.App.Services;
using Heimdall.App.ViewModels;

namespace Heimdall.App;

/// <summary>
/// Partial of <see cref="MainWindow"/> keeping the session tab strip usable once the tabs
/// overflow it: the strip scrolls to the selected tab, follows the mouse wheel, and offers a
/// list of every open tab.
/// </summary>
/// <remarks>
/// The strip used to show a scroll bar and nothing else. The selected tab was never scrolled to,
/// so after Ctrl+Tab, an open from the tree or a restore, the tab in use could sit off-screen:
/// measured with 12 tabs, the selected one at x=4062 in a strip ending at x=3196.
/// </remarks>
public partial class MainWindow
{
    /// <summary>Tolerance, in device-independent pixels, before the strip counts as overflowing.</summary>
    private const double TabStripOverflowTolerance = 0.5;

    private ScrollViewer? _tabStripScroller;

    private void OnSessionTabStripLoaded(object sender, RoutedEventArgs e)
    {
        if (_tabStripScroller is not null)
        {
            return;
        }

        SessionTabControl.ApplyTemplate();
        _tabStripScroller = SessionTabControl.Template?.FindName("HeaderScrollViewer", SessionTabControl) as ScrollViewer;
        if (_tabStripScroller is null)
        {
            return;
        }

        _tabStripScroller.ScrollChanged += (_, _) => UpdateTabStripOverflow();
        _tabStripScroller.PreviewMouseWheel += OnTabStripMouseWheel;
        UpdateTabStripOverflow();
        ScrollSelectedTabIntoView();
    }

    private void OnSessionTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only the strip's own selection; a selector inside a tab header would bubble here too.
        if (!ReferenceEquals(e.OriginalSource, SessionTabControl))
        {
            return;
        }

        ScrollSelectedTabIntoView();
    }

    /// <summary>
    /// Scrolls the strip to the selected tab once its container exists and has been measured,
    /// which is not yet the case when the selection changes on a tab that was just added.
    /// </summary>
    private void ScrollSelectedTabIntoView()
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (SessionTabControl.SelectedItem is { } selected
                    && SessionTabControl.ItemContainerGenerator.ContainerFromItem(selected) is TabItem container
                    && container.IsVisible)
                {
                    container.BringIntoView();
                }
            }));
    }

    /// <summary>
    /// Shows the list button while the tabs overflow, and makes the strip stop short of it so the
    /// tab scrolled into view never sits underneath. Overflow is judged against the width the strip
    /// would have without the button, so taking the room does not undo the decision that took it.
    /// </summary>
    private void UpdateTabStripOverflow()
    {
        if (_tabStripScroller is null)
        {
            return;
        }

        double quickAddWidth = Mw_SessionTabQuickAddButton?.Width is double w && !double.IsNaN(w) ? w + 2 : 0;
        double reserved = _tabStripScroller.Margin.Right;
        double overflowReserved = System.Math.Max(0, reserved - quickAddWidth);
        bool overflowing = _tabStripScroller.ExtentWidth
            > _tabStripScroller.ViewportWidth + overflowReserved + TabStripOverflowTolerance;

        Mw_SessionTabListButton.Visibility = overflowing ? Visibility.Visible : Visibility.Collapsed;
        double wanted = quickAddWidth + (overflowing ? Mw_SessionTabListButton.Width + 2 : 0);
        if (!wanted.Equals(reserved))
        {
            _tabStripScroller.Margin = new Thickness(0, 0, wanted, 0);
            ScrollSelectedTabIntoView();
        }
    }

    // The strip has no vertical extent, so the wheel scrolls it sideways.
    private void OnTabStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_tabStripScroller is null || _tabStripScroller.ScrollableWidth <= 0)
        {
            return;
        }

        _tabStripScroller.ScrollToHorizontalOffset(_tabStripScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// Opens the selected tab's menu under the tab, for Apps and Shift+F10 on the strip. The
    /// menu used to open on a right-click only. Returns false when there is no tab to act on.
    /// </summary>
    private bool OpenSessionTabKeyboardContextMenu()
    {
        if (GetMainVm() is not { Connection.ActiveSession: { } session } vm)
        {
            return false;
        }

        ContextMenu menu = _sessionTabContextMenuFactory.CreateMenu(session, vm, this);
        menu.PlacementTarget = SessionTabControl.ItemContainerGenerator.ContainerFromItem(session) as UIElement
            ?? SessionTabControl;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        return true;
    }

    private void OnSessionTabListButtonClick(object sender, RoutedEventArgs e)
    {
        if (GetMainVm() is not { } vm)
        {
            return;
        }

        ContextMenu menu = TabListMenuFactory.Create(vm.Connection);
        menu.PlacementTarget = Mw_SessionTabListButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Writes a tab's tooltip as it opens: the profile behind the tab is edited while the tab
    /// stays open, so a tooltip composed with the tab would go stale.
    /// </summary>
    private void OnSessionTabToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement header
            || header.DataContext is not SessionTabViewModel session
            || GetMainVm() is not { } vm)
        {
            return;
        }

        Heimdall.Core.Localization.LocalizationManager localizer = vm.GetLocalizer();
        header.ToolTip = SessionTabToolTipComposer.Compose(
            session,
            SessionTabToolTipComposer.FindProfile(vm, session),
            key => localizer[key],
            (key, args) => localizer.Format(key, args));
    }

    /// <summary>
    /// Scrolls the strip while a dragged tab is held near one of its ends, so a tab can be
    /// dropped beside one scrolled out of view. The strip shows no scroll bar to drag instead.
    /// </summary>
    private void AutoScrollTabStripDuringDrag(System.Windows.DragEventArgs e)
    {
        if (_tabStripScroller is null || _tabStripScroller.ScrollableWidth <= 0)
        {
            return;
        }

        double step = TabStripDragAutoScroll.Step(
            e.GetPosition(_tabStripScroller).X,
            _tabStripScroller.ViewportWidth);
        if (step != 0d)
        {
            _tabStripScroller.ScrollToHorizontalOffset(_tabStripScroller.HorizontalOffset + step);
        }
    }
}
