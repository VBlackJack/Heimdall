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
using System.Windows.Media;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Services;

/// <summary>
/// Canonical projection of the logical sidebar state onto the main-window layout.
/// </summary>
/// <param name="IsVisible">Whether the sidebar column and splitter are visible.</param>
/// <param name="ShowRestoreButton">Whether the collapsed-sidebar restore button is visible.</param>
/// <param name="Width">The normalized preferred width to restore when visible.</param>
internal readonly record struct SidebarLayoutProjection(
    bool IsVisible,
    bool ShowRestoreButton,
    double Width);

/// <summary>
/// Holds transient UI state for <c>MainWindow</c> (fullscreen, sidebar,
/// tree scroll position) plus pure static helpers that operate on the
/// visual tree and the <c>ServerList</c> model. Imperative code that
/// touches named XAML elements lives in <c>MainWindow.WindowUI.cs</c>;
/// this class owns only data and pure functions.
/// </summary>
public sealed class WindowUIState
{
    /// <summary>Default sidebar (TreeView column) width when restored.</summary>
    public const double DefaultSidebarWidth = 320d;

    /// <summary>Minimum sidebar column width when visible.</summary>
    public const double MinSidebarWidth = 180d;

    /// <summary>Maximum sidebar column width when visible.</summary>
    public const double MaxSidebarWidth = 500d;

    /// <summary>Toolbar row height when visible.</summary>
    public const double ToolbarHeight = 48d;

    /// <summary>Status-bar row height when visible.</summary>
    public const double StatusBarHeight = 28d;

    /// <summary>True while the window is in fullscreen mode.</summary>
    public bool IsFullscreen { get; set; }

    /// <summary>Window state captured before entering fullscreen, restored on exit.</summary>
    public WindowState PreFullscreenState { get; set; }

    /// <summary>Window width captured before entering fullscreen.</summary>
    public double PreFullscreenWidth { get; set; }

    /// <summary>Window height captured before entering fullscreen.</summary>
    public double PreFullscreenHeight { get; set; }

    /// <summary>True while the sidebar (TreeView column) is currently hidden.</summary>
    public bool IsSidebarHidden { get; set; }

    /// <summary>True while the active tab temporarily suppresses the sidebar.</summary>
    internal bool IsSidebarSuppressedByTab { get; set; }

    /// <summary>Last sidebar width seen before the sidebar was hidden, used to restore it.</summary>
    public double SavedSidebarWidth { get; set; } = DefaultSidebarWidth;

    /// <summary>Returns the canonical sidebar layout for the current logical state.</summary>
    internal SidebarLayoutProjection SidebarLayout
    {
        get
        {
            bool isTemporarilySuppressed = IsFullscreen || IsSidebarSuppressedByTab;
            return new SidebarLayoutProjection(
                IsVisible: !IsSidebarHidden && !isTemporarilySuppressed,
                ShowRestoreButton: IsSidebarHidden && !isTemporarilySuppressed,
                Width: NormalizeSidebarWidth(SavedSidebarWidth));
        }
    }

    /// <summary>Captured vertical scroll offset of the session TreeView.</summary>
    public double TreeScrollVerticalOffset { get; set; }

    /// <summary>Captured horizontal scroll offset of the session TreeView.</summary>
    public double TreeScrollHorizontalOffset { get; set; }

    /// <summary>Captures visible sidebar state before entering fullscreen.</summary>
    internal SidebarLayoutProjection EnterFullscreen(double actualSidebarWidth)
    {
        CaptureVisibleSidebarWidth(actualSidebarWidth);
        IsFullscreen = true;
        return SidebarLayout;
    }

    /// <summary>Leaves fullscreen without changing the user's sidebar preference.</summary>
    internal SidebarLayoutProjection ExitFullscreen()
    {
        IsFullscreen = false;
        return SidebarLayout;
    }

    /// <summary>Toggles the user's sidebar preference without exposing it while suppressed.</summary>
    internal SidebarLayoutProjection ToggleSidebar(double actualSidebarWidth)
    {
        CaptureVisibleSidebarWidth(actualSidebarWidth);
        IsSidebarHidden = !IsSidebarHidden;
        return SidebarLayout;
    }

    /// <summary>Applies or clears the temporary sidebar suppression owned by tab navigation.</summary>
    internal SidebarLayoutProjection SetSidebarSuppressedByTab(
        bool isSuppressed,
        double actualSidebarWidth)
    {
        if (isSuppressed && !IsSidebarSuppressedByTab)
        {
            CaptureVisibleSidebarWidth(actualSidebarWidth);
        }

        IsSidebarSuppressedByTab = isSuppressed;
        return SidebarLayout;
    }

    private void CaptureVisibleSidebarWidth(double actualSidebarWidth)
    {
        if (!SidebarLayout.IsVisible
            || !double.IsFinite(actualSidebarWidth)
            || actualSidebarWidth <= 0d)
        {
            return;
        }

        SavedSidebarWidth = NormalizeSidebarWidth(actualSidebarWidth);
    }

    private static double NormalizeSidebarWidth(double width)
    {
        if (!double.IsFinite(width))
        {
            return DefaultSidebarWidth;
        }

        return Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth);
    }

    /// <summary>
    /// Walks the visual tree of <paramref name="parent"/> to find the first
    /// descendant <see cref="ScrollViewer"/>. Returns <c>null</c> when none
    /// is found.
    /// </summary>
    public static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv)
                return sv;
            var result = FindScrollViewer(child);
            if (result is not null)
                return result;
        }
        return null;
    }

    /// <summary>
    /// Recursively sets <see cref="FolderViewModel.IsExpanded"/> on the
    /// folder and every descendant subfolder.
    /// </summary>
    public static void SetFolderExpandedRecursive(FolderViewModel folder, bool expanded)
    {
        folder.IsExpanded = expanded;
        foreach (var sub in folder.SubFolders)
        {
            SetFolderExpandedRecursive(sub, expanded);
        }
    }
}
