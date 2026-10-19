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
using Heimdall.App.ViewModels;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Picks the terminal pane of a session tab whose text a command acts on. The context menu, the
/// keyboard shortcut and the command palette all ask this one place, so none of them carries its
/// own type switch over the host controls.
/// </summary>
/// <remarks>
/// <para>
/// Order of preference: the pane the user pointed at (a right-click), else the pane that has the
/// keyboard focus, else the only terminal pane of the tab. A tab with two terminals and no
/// pointer or focus has no single answer, and none is returned rather than a guess.
/// </para>
/// <para>
/// A pane counts only when its host control is an <see cref="ITerminalTextSource"/>: an RDP, VNC,
/// SFTP or web view, or a local tab whose elevated shell runs in an external window, yields
/// nothing. A source that is not ready yields nothing either, so a command is offered only when
/// it can run.
/// </para>
/// </remarks>
internal static class TerminalTextSourceResolver
{
    /// <summary>
    /// Returns the terminal text source the command should act on, or null when the tab has none.
    /// </summary>
    /// <param name="session">The tab; null yields null.</param>
    /// <param name="clickedPane">The pane the user right-clicked, when the gesture was a pointer one.</param>
    /// <param name="hasKeyboardFocus">
    /// Tells whether a host control holds the keyboard focus; defaults to the WPF keyboard focus
    /// of the control. The focus is process-wide state, so a caller that cannot give a control the
    /// real focus supplies the answer.
    /// </param>
    public static ITerminalTextSource? Resolve(
        SessionTabViewModel? session,
        SessionPaneModel? clickedPane = null,
        Func<object, bool>? hasKeyboardFocus = null)
    {
        if (session is null)
        {
            return null;
        }

        hasKeyboardFocus ??= HasWpfKeyboardFocus;

        if (clickedPane?.HostControl is { } clickedHost)
        {
            // The user pointed at this pane: a pane that is not a terminal is a "no", not a cue
            // to fall back on another pane they did not point at.
            return clickedHost is ITerminalTextSource clickedSource ? ReadyOrNull(clickedSource) : null;
        }

        List<ITerminalTextSource> terminals =
        [
            .. SplitTreeHelper.EnumerateLeaves(session.RootContent)
                .Select(pane => pane.HostControl)
                .OfType<ITerminalTextSource>()
        ];

        ITerminalTextSource? focused = terminals.FirstOrDefault(source => hasKeyboardFocus(source));
        if (focused is not null)
        {
            return ReadyOrNull(focused);
        }

        return terminals.Count == 1 ? ReadyOrNull(terminals[0]) : null;
    }

    private static ITerminalTextSource? ReadyOrNull(ITerminalTextSource source) =>
        source.CanReadText ? source : null;

    private static bool HasWpfKeyboardFocus(object host) =>
        host is UIElement { IsKeyboardFocusWithin: true };
}
