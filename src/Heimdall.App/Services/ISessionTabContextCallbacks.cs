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
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Callbacks surfaced by the window layer to
/// <see cref="SessionTabContextMenuFactory"/>. These actions require access
/// to <see cref="MainWindow"/> state (window fullscreen state, floating
/// window creation, split session orchestration) and therefore cannot be
/// inlined into the factory.
/// </summary>
public interface ISessionTabContextCallbacks
{
    /// <summary>Applies a resolution choice to the given session pane.</summary>
    void OnResolutionChanged(SessionPaneModel pane, ResolutionChoice choice);

    /// <summary>Toggles fullscreen mode on the main window.</summary>
    void ToggleFullscreen();

    /// <summary>
    /// Detaches the given session tab to a new floating window. Used by the
    /// non-split "Detach" menu item and by the tab drag-drop fallback path.
    /// </summary>
    void DetachSessionToFloatingWindow(SessionTabViewModel session);

    /// <summary>
    /// Detaches the secondary pane of a split session to its own floating
    /// window. Only valid when <see cref="SessionTabViewModel.IsSplit"/>
    /// is <c>true</c>.
    /// </summary>
    void DetachSecondaryToFloatingWindow(SessionTabViewModel session);

    /// <summary>
    /// Opens the Command Palette in split mode so the user can pick a
    /// session to split with, using the given orientation.
    /// </summary>
    void RequestSplitSession(SessionTabViewModel session, SplitOrientation orientation);

    /// <summary>
    /// Unsplits a split session, detaching the secondary pane back into a
    /// standalone tab.
    /// </summary>
    void UnsplitSession(SessionTabViewModel session);

    /// <summary>
    /// Selects the source profile of the active session in the sessions tree,
    /// expanding its ancestor folders and scrolling its node into view. Relaxes an
    /// active search filter when it would hide the node and ensures the sessions
    /// sidebar is visible. No-ops safely when <paramref name="serverId"/> is unknown.
    /// </summary>
    void RevealServerInTree(string serverId);
}
