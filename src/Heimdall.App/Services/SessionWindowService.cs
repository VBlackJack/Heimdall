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

using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Orchestrates session split, merge, and detach operations: spawning
/// floating session windows, extracting secondary panes back to standalone
/// tabs, and opening the split palette. Extracted from
/// <c>MainWindow.xaml.cs</c> to isolate session-tree manipulation logic
/// from the window code-behind.
/// </summary>
/// <remarks>
/// The service is deliberately window-agnostic: public methods take the
/// <see cref="MainViewModel"/> as an explicit parameter (mirroring
/// <see cref="ContextMenuFactory"/> / <see cref="SessionTabContextMenuFactory"/>),
/// and the one piece of view-layer interop that could not be inlined -
/// focusing the Command Palette input after
/// <see cref="RequestSplitSession"/> - is surfaced via the
/// <see cref="SplitPaletteRequested"/> event. The host window subscribes
/// and calls its own <c>BeginFocusCommandPalette</c> helper.
/// </remarks>
public sealed class SessionWindowService : ISessionWindowService
{
    private readonly Action<SessionTabViewModel, LocalizationManager> _showFloatingWindow;
    private readonly Func<IReadOnlyList<SessionTabViewModel>> _readDetachedSessions;
    private readonly Func<SessionTabViewModel, bool> _reattachDetachedSession;

    /// <summary>
    /// Initialises a new <see cref="SessionWindowService"/>.
    /// </summary>
    public SessionWindowService()
        : this(ShowFloatingWindow, ReadDetachedSessions)
    {
    }

    internal SessionWindowService(
        Action<SessionTabViewModel, LocalizationManager> showFloatingWindow)
        : this(showFloatingWindow, static () => [])
    {
    }

    internal SessionWindowService(
        Action<SessionTabViewModel, LocalizationManager> showFloatingWindow,
        Func<IReadOnlyList<SessionTabViewModel>> readDetachedSessions)
        : this(showFloatingWindow, readDetachedSessions, ReattachDetachedSession)
    {
    }

    internal SessionWindowService(
        Action<SessionTabViewModel, LocalizationManager> showFloatingWindow,
        Func<IReadOnlyList<SessionTabViewModel>> readDetachedSessions,
        Func<SessionTabViewModel, bool> reattachDetachedSession)
    {
        _showFloatingWindow = showFloatingWindow
            ?? throw new ArgumentNullException(nameof(showFloatingWindow));
        _readDetachedSessions = readDetachedSessions
            ?? throw new ArgumentNullException(nameof(readDetachedSessions));
        _reattachDetachedSession = reattachDetachedSession
            ?? throw new ArgumentNullException(nameof(reattachDetachedSession));
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionTabViewModel> DetachedSessions => _readDetachedSessions();

    /// <summary>
    /// Reads the sessions hosted by the floating windows that are open right now.
    /// </summary>
    /// <remarks>
    /// Touches the window collection, so it belongs on the UI thread. Its only caller is the
    /// session census, which runs there because it may raise a dialog. Before any window exists
    /// there is nothing detached, which is the honest answer rather than a failure.
    /// </remarks>
    private static IReadOnlyList<SessionTabViewModel> ReadDetachedSessions()
    {
        System.Windows.Application? application = System.Windows.Application.Current;
        if (application is null)
        {
            return [];
        }

        return
        [
            .. application.Windows
                .OfType<Views.FloatingSessionWindow>()
                .Select(window => window.Session)
        ];
    }

    /// <summary>
    /// Hands a session hosted by a floating window back to the main window, through the window
    /// itself so it lets go of the host control first. False when no window hosts it.
    /// </summary>
    private static bool ReattachDetachedSession(SessionTabViewModel session)
    {
        Views.FloatingSessionWindow? window = System.Windows.Application.Current?.Windows
            .OfType<Views.FloatingSessionWindow>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Session, session));
        if (window is null)
        {
            return false;
        }

        window.ReattachToMainWindow();
        return true;
    }

    /// <summary>
    /// Makes sure <paramref name="session"/> is a tab of the main window before it is split.
    /// </summary>
    /// <remarks>
    /// A floating window shows only the session's single host. Splitting the session there added
    /// a pane nothing displayed, which still connected and held its server. The session goes back
    /// to the main window first, where the split is visible.
    /// </remarks>
    private bool EnsureInMainWindow(SessionTabViewModel session, MainViewModel vm)
    {
        if (vm.Connection.ActiveSessions.Contains(session))
        {
            return true;
        }

        if (_reattachDetachedSession(session) && vm.Connection.ActiveSessions.Contains(session))
        {
            return true;
        }

        FileLogger.Warn($"Split ignored for '{session.Title}': the session is not in the main window.");
        return false;
    }

    /// <summary>
    /// Fired after <see cref="RequestSplitSession"/> opens the split palette
    /// on the view-model. Subscribers (typically <c>MainWindow</c>) are
    /// expected to focus the palette input so the user can start typing
    /// immediately.
    /// </summary>
    public event EventHandler? SplitPaletteRequested;

    /// <summary>
    /// Detaches a session tab from the main window into a standalone
    /// floating window. The host control is removed from the
    /// <c>TabControl</c> and re-parented to the new window (WPF UIElement
    /// single-parent rule preserved by nulling and re-assigning
    /// <see cref="SessionTabViewModel.HostControl"/>).
    /// </summary>
    public void DetachSessionToFloatingWindow(SessionTabViewModel session, MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(vm);

        if (!vm.Connection.ActiveSessions.Contains(session)) return;
        if (session.HostControl is null) return;

        // A floating window shows one host. Dragging a split tab out of the strip used to detach it
        // anyway: only the primary pane was shown, and the other panes stayed connected, out of
        // reach, until the application exited. The context menu already withholds Detach here.
        if (session.IsSplit)
        {
            vm.StatusText = vm.GetLocalizer()["StatusDetachSplitTabRefused"];
            return;
        }

        // Detach the host control from the tab (UIElement single-parent rule)
        var hostControl = session.HostControl;
        session.HostControl = null;

        // Remove the session from the main window's collection
        vm.Connection.ActiveSessions.Remove(session);
        if (vm.Connection.ActiveSession == session)
        {
            vm.Connection.ActiveSession = vm.Connection.ActiveSessions.LastOrDefault();
        }
        vm.Connection.HasActiveSessions = vm.Connection.ActiveSessions.Count > 0;

        // Re-assign the host control so the floating window can pick it up
        session.HostControl = hostControl;

        // Spawn the floating window
        var localizer = vm.GetLocalizer();
        _showFloatingWindow(session, localizer);

        FileLogger.Info(
            string.Format(localizer["LogSessionDetached"], session.Title));
    }

    private static void ShowFloatingWindow(
        SessionTabViewModel session,
        LocalizationManager localizer)
    {
        // The shared arbiter, read the same way this view layer already reads the dialog service.
        // Sharing matters: a clearance obtained before detaching must still be honoured here rather
        // than raising the same question twice. A standalone one is only a fallback for the case
        // where no main view model is reachable at all.
        IPaneCloseArbiter arbiter =
            System.Windows.Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel vm
                ? vm.CloseArbiter
                : new PaneCloseArbiter();

        var floatingWindow = new FloatingSessionWindow(session, localizer, arbiter)
        {
            Owner = null // Independent top-level window
        };
        floatingWindow.Show();
    }

    /// <summary>
    /// Detaches the secondary pane of a split session into its own floating
    /// window. No-op when <paramref name="session"/> is not split or its
    /// secondary pane has no host control yet.
    /// </summary>
    public void DetachSecondaryToFloatingWindow(SessionTabViewModel session, MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(vm);

        if (!session.IsSplit) return;

        SessionPaneModel? secondaryPane = session.SecondaryPaneOrNull;
        if (secondaryPane is not null && secondaryPane.HostControl is not null)
        {
            DetachPaneToFloatingWindow(session, secondaryPane.PaneId, vm);
        }
    }

    /// <summary>
    /// Opens the Command Palette in split mode with the requested
    /// orientation, so the user can pick a session to split with via fuzzy
    /// search (replaces the legacy context-menu picker that broke down at
    /// 100+ servers). Raises <see cref="SplitPaletteRequested"/> after the
    /// view-model call so subscribers can drive the popup focus.
    /// </summary>
    public void RequestSplitSession(
        SessionTabViewModel session,
        SplitOrientation orientation,
        MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(vm);

        if (!EnsureInMainWindow(session, vm))
        {
            return;
        }

        vm.CommandPalette.OpenSplit(session, orientation);
        SplitPaletteRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Unsplits a split session by detaching its secondary pane back to a
    /// new independent tab. No-op on non-split sessions.
    /// </summary>
    public void UnsplitSession(SessionTabViewModel session, MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(vm);

        if (!session.IsSplit) return;

        SessionPaneModel? secondaryPane = session.SecondaryPaneOrNull;
        if (secondaryPane is not null)
        {
            DetachPaneToTab(session, secondaryPane.PaneId, vm);
        }
    }

    /// <summary>
    /// Dispatches an embedded-view split request: unsplits the session if
    /// it is already split, otherwise opens the split palette with a
    /// vertical default orientation. Called from <c>MainWindow</c> via the
    /// <c>EmbeddedSessionManager.SplitRequestedCallback</c> wiring.
    /// </summary>
    public void HandleEmbeddedSplitRequest(SessionTabViewModel session, MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(vm);

        if (session.IsSplit)
        {
            UnsplitSession(session, vm);
        }
        else
        {
            RequestSplitSession(session, SplitOrientation.Vertical, vm);
        }
    }

    // ── Private helpers ──────────────────────────────────────────────

    /// <summary>
    /// Extracts a specific pane from the split tree into a new tab and
    /// then immediately detaches that tab to a floating window.
    /// </summary>
    private void DetachPaneToFloatingWindow(SessionTabViewModel session, string paneId, MainViewModel vm)
    {
        var pane = SplitTreeHelper.FindPane(session.RootContent, paneId);
        if (pane is null || pane.HostControl is null) return;

        // Capture pane metadata
        var hostControl = pane.HostControl;
        var serverId = pane.ServerId;
        var originalServerId = pane.OriginalServerId;
        var connType = pane.ConnectionType;
        var title = pane.Title;
        var status = pane.Status;
        var tunnelRoute = pane.TunnelRoute;
        var envColor = pane.EnvironmentColor;

        // Detach host control and remove pane from tree
        pane.HostControl = null;
        var newRoot = SplitTreeHelper.RemovePane(session.RootContent, paneId);
        session.SetRootContent(newRoot ?? new SessionPaneModel());

        // Create a new independent tab and detach it
        var newTab = vm.Connection.AddSession(serverId, title, connType);
        newTab.OriginalServerId = originalServerId;
        newTab.HostControl = hostControl;
        newTab.Status = !string.IsNullOrEmpty(status) ? status : "Connected";
        newTab.TunnelRoute = tunnelRoute;
        newTab.EnvironmentColor = envColor;

        DetachSessionToFloatingWindow(newTab, vm);
    }

    /// <summary>
    /// Extracts a specific pane from the split tree into its own independent
    /// tab (without detaching to a floating window). Handles the edge case
    /// where the pane is still connecting (no host control): cleans up
    /// orphan state and aborts without restoring a tab.
    /// </summary>
    private static void DetachPaneToTab(SessionTabViewModel session, string paneId, MainViewModel vm)
    {
        var pane = SplitTreeHelper.FindPane(session.RootContent, paneId);
        if (pane is null) return;

        // Capture metadata
        var hostControl = pane.HostControl;
        var serverId = pane.ServerId;
        var originalServerId = pane.OriginalServerId;
        var connType = pane.ConnectionType;
        var title = pane.Title;
        var status = pane.Status;
        var tunnelRoute = pane.TunnelRoute;
        var envColor = pane.EnvironmentColor;

        // Detach host control (UIElement single-parent rule)
        pane.HostControl = null;

        // Remove pane from tree
        var newRoot = SplitTreeHelper.RemovePane(session.RootContent, paneId);
        session.SetRootContent(newRoot ?? new SessionPaneModel());

        // If the pane was still connecting (no host control), clean up state and abort
        if (hostControl is null)
        {
            vm.CleanupOrphanedPane(serverId);
            FileLogger.Info($"Detach cancelled connecting pane '{title}'.");
            return;
        }

        // A pane without a server id cannot become a tab of its own; its host would otherwise be
        // dropped here still holding its connection.
        if (string.IsNullOrEmpty(serverId))
        {
            if (hostControl is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already gone.
                }
            }

            FileLogger.Warn($"Detached pane '{title}' had no server id; its host was disposed.");
            return;
        }

        // Restore as independent tab with original metadata
        var displayTitle = !string.IsNullOrEmpty(title) ? title : serverId;
        var restoredTab = vm.Connection.AddSession(serverId, displayTitle, connType);
        restoredTab.OriginalServerId = originalServerId;
        restoredTab.HostControl = hostControl;
        restoredTab.Status = !string.IsNullOrEmpty(status) ? status : "Connected";
        restoredTab.TunnelRoute = tunnelRoute;
        restoredTab.EnvironmentColor = envColor;
    }
}
