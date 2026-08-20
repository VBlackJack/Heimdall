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
/// Orchestrates session windowing: detaching tabs and split panes into
/// standalone floating windows, extracting split panes back to tabs, and
/// opening the split palette.
/// </summary>
public interface ISessionWindowService
{
    /// <summary>
    /// Raised after <see cref="RequestSplitSession"/> opens the split
    /// palette, so the host window can focus the palette input.
    /// </summary>
    event EventHandler? SplitPaletteRequested;

    /// <summary>
    /// The sessions currently living in detached windows.
    /// </summary>
    /// <remarks>
    /// Read on demand rather than kept as a registry. A registry has to be told about every
    /// detach, reattach and close, and a missed unregistration would leave a session counted
    /// forever - locking the user out of opening new ones, which is worse than the limit being
    /// bypassable. Reading the live windows cannot drift from them.
    /// </remarks>
    IReadOnlyList<SessionTabViewModel> DetachedSessions { get; }

    /// <summary>Detaches a session tab into a standalone floating window.</summary>
    void DetachSessionToFloatingWindow(SessionTabViewModel session, MainViewModel vm);

    /// <summary>Detaches a split session's secondary pane into its own floating window.</summary>
    void DetachSecondaryToFloatingWindow(SessionTabViewModel session, MainViewModel vm);

    /// <summary>Opens the Command Palette in split mode with the given orientation.</summary>
    void RequestSplitSession(SessionTabViewModel session, SplitOrientation orientation, MainViewModel vm);

    /// <summary>Unsplits a split session, returning its secondary pane to a standalone tab.</summary>
    void UnsplitSession(SessionTabViewModel session, MainViewModel vm);

    /// <summary>Toggles split state: unsplits when split, otherwise opens the split palette.</summary>
    void HandleEmbeddedSplitRequest(SessionTabViewModel session, MainViewModel vm);
}
