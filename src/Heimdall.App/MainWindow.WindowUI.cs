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
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using WinForms = System.Windows.Forms;

namespace Heimdall.App;

/// <summary>
/// Partial of <see cref="MainWindow"/> hosting the imperative window-UI
/// handlers that mutate named XAML elements: fullscreen toggle, sidebar
/// hide/show, tree scroll save/restore, and folder expand/collapse-all.
/// All transient state lives in the <c>_uiState</c> field
/// (<see cref="WindowUIState"/>); this file only contains the glue that
/// reads/writes that state and pokes the visual tree accordingly.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan FullscreenChromeAutoHideDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FullscreenChromeFadeDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FullscreenChromeMousePollInterval = TimeSpan.FromMilliseconds(100);
    private const double FullscreenChromeRevealThresholdPx = 30;
    private const double FullscreenChromeRightMargin = 14;
    private const double FullscreenChromeTopMargin = 14;
    private const double FullscreenChromeEstimatedWidth = 50;
    private DispatcherTimer? _fullscreenChromeAutoHideTimer;
    private DispatcherTimer? _fullscreenChromeMousePollTimer;
    private System.Drawing.Point? _lastFullscreenMouseScreenPoint;
    private IDisposable? _lowLevelKeyboardHook;
    private bool _fullscreenChromeVisible;

    // ── Tree scroll save/restore ─────────────────────────────────────

    private void SaveTreeViewScrollPosition()
    {
        var sv = WindowUIState.FindScrollViewer(SessionTreeView);
        if (sv is not null)
        {
            _uiState.TreeScrollVerticalOffset = sv.VerticalOffset;
            _uiState.TreeScrollHorizontalOffset = sv.HorizontalOffset;
        }
    }

    private void RestoreTreeViewScrollPosition()
    {
        // Defer to allow the TreeView to re-render before restoring scroll position
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                var sv = WindowUIState.FindScrollViewer(SessionTreeView);
                if (sv is not null)
                {
                    sv.ScrollToVerticalOffset(_uiState.TreeScrollVerticalOffset);
                    sv.ScrollToHorizontalOffset(_uiState.TreeScrollHorizontalOffset);
                }
            }));
    }

    // ── Fullscreen toggle ────────────────────────────────────────────

    private void OnToggleFullscreenClick(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (DataContext is not MainViewModel)
            return;

        if (_uiState.IsFullscreen)
        {
            // Exit fullscreen
            _uiState.IsFullscreen = false;
            StopFullscreenChrome();
            NotifyEmbeddedViewsFullscreen(false);

            // Show toolbar, TreeView, status bar
            ToolbarRow.Height = new GridLength(WindowUIState.ToolbarHeight);
            StatusBarRow.Height = new GridLength(WindowUIState.StatusBarHeight);
            SessionTreeColumn.Width = new GridLength(WindowUIState.DefaultSidebarWidth);
            SessionTreeColumn.MinWidth = WindowUIState.MinSidebarWidth;
            SessionTreeColumn.MaxWidth = WindowUIState.MaxSidebarWidth;
            SplitterColumn.Width = GridLength.Auto;

            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _uiState.PreFullscreenState;
            if (_uiState.PreFullscreenState == WindowState.Normal)
            {
                Width = _uiState.PreFullscreenWidth;
                Height = _uiState.PreFullscreenHeight;
            }
        }
        else
        {
            // Enter fullscreen
            _uiState.IsFullscreen = true;
            _uiState.PreFullscreenState = WindowState;
            _uiState.PreFullscreenWidth = ActualWidth;
            _uiState.PreFullscreenHeight = ActualHeight;

            // Hide toolbar, TreeView, status bar
            ToolbarRow.Height = new GridLength(0);
            StatusBarRow.Height = new GridLength(0);
            SessionTreeColumn.MinWidth = 0;
            SessionTreeColumn.MaxWidth = 0;
            SessionTreeColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);

            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            StartFullscreenChrome();

            // Hide session tab headers in fullscreen (session fills the screen)
            SessionTabControl.Padding = new Thickness(0);
            SessionTabControl.Margin = new Thickness(0);

            // Hide the session header bar inside embedded views
            NotifyEmbeddedViewsFullscreen(true);
        }
    }

    private void NotifyEmbeddedViewsFullscreen(bool isFullscreen)
    {
        if (DataContext is not MainViewModel vm) return;
        foreach (var session in vm.Connection.ActiveSessions)
        {
            if (session.HostControl is Views.EmbeddedRdpView rdpView)
                rdpView.SetFullscreen(isFullscreen);
            else if (session.HostControl is Views.EmbeddedSshView sshView)
                sshView.Visibility = Visibility.Visible; // SSH always visible
        }

        // Hide/show entire tab strip by collapsing the TabPanel
        // Single session fullscreen = no tab bar needed
        if (isFullscreen && vm.Connection.ActiveSessions.Count <= 1)
        {
            SessionTabControl.Tag = "fullscreen-notabs";
            // Use a style that hides the header panel
            SessionTabControl.SetValue(System.Windows.Controls.Control.PaddingProperty, new Thickness(0));
            // Walk the visual tree to find and hide the TabPanel
            HideTabStripPanel(SessionTabControl, true);
        }
        else
        {
            SessionTabControl.Tag = null;
            HideTabStripPanel(SessionTabControl, false);
        }
    }

    private void OnExitFullscreenClick(object sender, RoutedEventArgs e)
    {
        if (_uiState.IsFullscreen) ToggleFullscreen();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((e.Key == Key.Apps
                || (e.Key == Key.F10 && modifiers == ModifierKeys.Shift))
            && SessionTreeView.IsKeyboardFocusWithin
            && DataContext is MainViewModel contextMenuViewModel)
        {
            e.Handled = true;
            OpenTreeViewKeyboardContextMenu(contextMenuViewModel);
            return;
        }

        var action = FullscreenShortcutRouter.Resolve(e.Key, modifiers, _uiState.IsFullscreen);
        if (action == FullscreenShortcutAction.None)
        {
            return;
        }

        e.Handled = true;
        ApplyFullscreenShortcutAction(action);
    }

    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        const int wmKeyDown = 0x0100;
        const int wmSysKeyDown = 0x0104;

        if (handled || (msg.message != wmKeyDown && msg.message != wmSysKeyDown))
        {
            return;
        }

        var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
        var modifiers = Keyboard.Modifiers;
        var action = FullscreenShortcutRouter.Resolve(key, modifiers, _uiState.IsFullscreen);
        if (action == FullscreenShortcutAction.None)
        {
            return;
        }

        handled = true;
        Heimdall.Core.Logging.FileLogger.Info(
            $"MainWindow.ThreadPreprocessMessage intercepted key={key} fullscreen={_uiState.IsFullscreen} action={action} handled=true");

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => ApplyFullscreenShortcutAction(action)));
    }

    private void ApplyFullscreenShortcutAction(FullscreenShortcutAction action)
    {
        switch (action)
        {
            case FullscreenShortcutAction.EnterFullscreen:
                if (!_uiState.IsFullscreen)
                {
                    ToggleFullscreen();
                }
                break;

            case FullscreenShortcutAction.ExitFullscreen:
                if (_uiState.IsFullscreen)
                {
                    ToggleFullscreen();
                }
                break;

            case FullscreenShortcutAction.ToggleFullscreen:
                ToggleFullscreen();
                break;
        }
    }

    private void OnFullscreenChromeMouseActivity(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_uiState.IsFullscreen)
        {
            return;
        }

        ShowFullscreenChrome(restartAutoHide: true);
    }

    private void StartFullscreenChrome()
    {
        StartFullscreenChromeMousePolling();
        ShowFullscreenChrome(restartAutoHide: true);
    }

    private void StopFullscreenChrome()
    {
        StopFullscreenChromeAutoHideTimer();
        StopFullscreenChromeMousePolling();
        _fullscreenChromeVisible = false;
        FullscreenChromePopup.IsOpen = false;
        FullscreenChromeRoot.Opacity = 0;
    }

    private void ShowFullscreenChrome(bool restartAutoHide)
    {
        UpdateFullscreenChromePlacement();

        if (!FullscreenChromePopup.IsOpen)
        {
            FullscreenChromeRoot.Opacity = 0;
            FullscreenChromePopup.IsOpen = true;
        }

        _fullscreenChromeVisible = true;
        AnimateFullscreenChromeOpacity(1, closeWhenDone: false);

        if (restartAutoHide)
        {
            RestartFullscreenChromeAutoHideTimer();
        }
    }

    private void HideFullscreenChrome()
    {
        if (!FullscreenChromePopup.IsOpen)
        {
            _fullscreenChromeVisible = false;
            return;
        }

        _fullscreenChromeVisible = false;
        AnimateFullscreenChromeOpacity(0, closeWhenDone: true);
    }

    private void InstallLowLevelKeyboardHook()
    {
        if (_lowLevelKeyboardHook is not null)
        {
            return;
        }

        try
        {
            // WH_KEYBOARD_LL is process-foreground filtered in the hook proc.
            // Some EDR/AV products flag low-level hooks; keeping the filter
            // strict prevents Heimdall from consuming keys in other apps.
            _lowLevelKeyboardHook = LowLevelKeyboardHook.Install(OnLowLevelKeyboardHookKeyDown);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"LowLevelKeyboardHook install failed: {ex.Message}");
        }
    }

    private void DisposeLowLevelKeyboardHook()
    {
        if (_lowLevelKeyboardHook is null)
        {
            return;
        }

        try
        {
            _lowLevelKeyboardHook.Dispose();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"LowLevelKeyboardHook dispose failed: {ex.Message}");
        }
        finally
        {
            _lowLevelKeyboardHook = null;
        }
    }

    private bool OnLowLevelKeyboardHookKeyDown(Key key, ModifierKeys modifiers)
    {
        FullscreenShortcutAction action = FullscreenShortcutRouter.Resolve(key, modifiers, _uiState.IsFullscreen);
        if (action == FullscreenShortcutAction.None)
        {
            if (key == Key.K
                && modifiers == ModifierKeys.Control
                && RdpKeyboardEscapeHook.IsRegisteredRdpViewFocused())
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(OpenCommandPalette));
                return true;
            }

            // Ctrl+L: lock the workspace even while an embedded terminal/RDP/VNC surface
            // owns focus. WebView2 runs out-of-process (its keystrokes never reach the UI
            // thread's message pump) and a shell binds Ctrl+L to "clear screen", so neither
            // the bubbling KeyDown nor OnThreadPreprocessMessage sees it. The low-level hook
            // does. Absorb it (return true) so the surface does not also act on it. Gated on
            // IsActive so Ctrl+L is never intercepted while Heimdall is in the background.
            if (key == Key.L
                && modifiers == ModifierKeys.Control
                && IsActive
                && GetMainVm()?.LockWorkspaceCommand is { } lockCommand
                && lockCommand.CanExecute(null))
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(() => lockCommand.Execute(null)));
                return true;
            }

            return false;
        }

        Heimdall.Core.Logging.FileLogger.Info(
            $"LowLevelKeyboardHook intercepted key={key} modifiers={modifiers} fullscreen={_uiState.IsFullscreen} action={action} absorbed=true");

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => ApplyFullscreenShortcutAction(action)));
        return true;
    }

    private void AnimateFullscreenChromeOpacity(double targetOpacity, bool closeWhenDone)
    {
        var animation = new DoubleAnimation(targetOpacity, FullscreenChromeFadeDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        if (closeWhenDone)
        {
            animation.Completed += (_, _) =>
            {
                if (!_fullscreenChromeVisible)
                {
                    FullscreenChromePopup.IsOpen = false;
                }
            };
        }

        FullscreenChromeRoot.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void RestartFullscreenChromeAutoHideTimer()
    {
        StopFullscreenChromeAutoHideTimer();
        _fullscreenChromeAutoHideTimer = new DispatcherTimer(
            FullscreenChromeAutoHideDelay,
            DispatcherPriority.Background,
            OnFullscreenChromeAutoHideTick,
            Dispatcher);
        _fullscreenChromeAutoHideTimer.Start();
    }

    private void StopFullscreenChromeAutoHideTimer()
    {
        if (_fullscreenChromeAutoHideTimer is null)
        {
            return;
        }

        _fullscreenChromeAutoHideTimer.Stop();
        _fullscreenChromeAutoHideTimer.Tick -= OnFullscreenChromeAutoHideTick;
        _fullscreenChromeAutoHideTimer = null;
    }

    private void OnFullscreenChromeAutoHideTick(object? sender, EventArgs e)
    {
        StopFullscreenChromeAutoHideTimer();
        if (_uiState.IsFullscreen)
        {
            HideFullscreenChrome();
        }
    }

    private void StartFullscreenChromeMousePolling()
    {
        if (_fullscreenChromeMousePollTimer is not null)
        {
            return;
        }

        _lastFullscreenMouseScreenPoint = WinForms.Control.MousePosition;
        _fullscreenChromeMousePollTimer = new DispatcherTimer(
            FullscreenChromeMousePollInterval,
            DispatcherPriority.Background,
            OnFullscreenChromeMousePollTick,
            Dispatcher);
        _fullscreenChromeMousePollTimer.Start();
    }

    private void StopFullscreenChromeMousePolling()
    {
        if (_fullscreenChromeMousePollTimer is null)
        {
            return;
        }

        _fullscreenChromeMousePollTimer.Stop();
        _fullscreenChromeMousePollTimer.Tick -= OnFullscreenChromeMousePollTick;
        _fullscreenChromeMousePollTimer = null;
        _lastFullscreenMouseScreenPoint = null;
    }

    private void OnFullscreenChromeMousePollTick(object? sender, EventArgs e)
    {
        if (!_uiState.IsFullscreen)
        {
            StopFullscreenChrome();
            return;
        }

        var current = WinForms.Control.MousePosition;
        if (_lastFullscreenMouseScreenPoint is { } previous
            && previous != current
            && _fullscreenChromeVisible)
        {
            RestartFullscreenChromeAutoHideTimer();
        }

        _lastFullscreenMouseScreenPoint = current;

        if (IsPointerNearFullscreenTopEdge(current))
        {
            ShowFullscreenChrome(restartAutoHide: true);
        }
    }

    private bool IsPointerNearFullscreenTopEdge(System.Drawing.Point screenPoint)
    {
        var topLeft = PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = PointToScreen(new System.Windows.Point(ActualWidth, ActualHeight));

        return screenPoint.X >= topLeft.X
            && screenPoint.X <= bottomRight.X
            && screenPoint.Y >= topLeft.Y
            && screenPoint.Y <= topLeft.Y + FullscreenChromeRevealThresholdPx;
    }

    private void UpdateFullscreenChromePlacement()
    {
        FullscreenChromePopup.HorizontalOffset = Math.Max(
            FullscreenChromeRightMargin,
            ActualWidth - FullscreenChromeEstimatedWidth - FullscreenChromeRightMargin);
        FullscreenChromePopup.VerticalOffset = FullscreenChromeTopMargin;
    }

    // ── Sidebar toggle ───────────────────────────────────────────────

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        if (_uiState.IsSidebarHidden)
        {
            _uiState.IsSidebarHidden = false;
            SessionTreeColumn.MinWidth = WindowUIState.MinSidebarWidth;
            SessionTreeColumn.MaxWidth = WindowUIState.MaxSidebarWidth;
            SessionTreeColumn.Width = new GridLength(_uiState.SavedSidebarWidth);
            SplitterColumn.Width = GridLength.Auto;
            ShowSidebarButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            _uiState.IsSidebarHidden = true;
            _uiState.SavedSidebarWidth = SessionTreeColumn.ActualWidth;
            SessionTreeColumn.MinWidth = 0;
            SessionTreeColumn.MaxWidth = 0;
            SessionTreeColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            ShowSidebarButton.Visibility = Visibility.Visible;
        }
    }

    // ── Folder expand/collapse all ───────────────────────────────────

    private void OnExpandAllClick(object sender, RoutedEventArgs e)
    {
        SetAllFoldersExpanded(true);
    }

    private void OnCollapseAllClick(object sender, RoutedEventArgs e)
    {
        SetAllFoldersExpanded(false);
    }

    private void SetAllFoldersExpanded(bool expanded)
    {
        if (DataContext is not MainViewModel vm) return;

        foreach (var folder in vm.ServerList.GroupedServers)
        {
            WindowUIState.SetFolderExpandedRecursive(folder, expanded);
        }
    }
}
