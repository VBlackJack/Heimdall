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

using System.Windows.Input;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>What a click on a session row does to the tabs that are already open.</summary>
internal enum TreeClickActivationKind
{
    /// <summary>The click only selects the row.</summary>
    None,

    /// <summary>The click brings a tab of the main window to the front.</summary>
    AttachedTab,

    /// <summary>The click brings a floating window to the front.</summary>
    DetachedWindow,
}

/// <summary>The outcome of <see cref="TreeClickActivationDecision.Resolve"/>.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Session">The tab to select, or the session whose floating window to raise.</param>
internal readonly record struct TreeClickActivation(TreeClickActivationKind Kind, SessionTabViewModel? Session)
{
    /// <summary>The click does nothing beyond selecting the row.</summary>
    public static TreeClickActivation Nothing { get; } = new(TreeClickActivationKind.None, null);
}

/// <summary>The facts about one pointer gesture on the session tree.</summary>
/// <param name="Modifiers">The modifier keys held when the button came back up.</param>
/// <param name="ClickCount">1 for a single click, 2 or more for the later clicks of a multi-click.</param>
/// <param name="Dragged">Whether the press turned into a drag.</param>
/// <param name="PlainSelectionPress">Whether the press selected the row on its own, as opposed to a
/// range or toggle gesture or a press deferred inside a multi-selection.</param>
internal readonly record struct TreeClickGesture(
    ModifierKeys Modifiers,
    int ClickCount,
    bool Dragged,
    bool PlainSelectionPress);

/// <summary>
/// Decides which already-open tab, if any, a click on a session row brings to the front, apart from
/// the window so the decision can be tested without one.
/// </summary>
/// <remarks>
/// Opt-in: with the setting off the answer is always nothing. Double click and Enter do not consult
/// this at all and keep opening a new tab.
/// </remarks>
internal static class TreeClickActivationDecision
{
    /// <summary>The click count of a single, first click.</summary>
    private const int SingleClickCount = 1;

    /// <summary>
    /// Resolves the effect of the release of a left click on a tree row.
    /// </summary>
    /// <param name="enabled">The user's setting.</param>
    /// <param name="gesture">What the pointer did.</param>
    /// <param name="pressedTarget">The data item under the pointer when the button went down.</param>
    /// <param name="releasedTarget">The data item under the pointer when the button came back up.</param>
    /// <param name="attached">The tabs of the main window, in strip order.</param>
    /// <param name="active">The tab showing now, if any.</param>
    /// <param name="detached">The sessions living in floating windows.</param>
    public static TreeClickActivation Resolve(
        bool enabled,
        TreeClickGesture gesture,
        object? pressedTarget,
        object? releasedTarget,
        IReadOnlyList<SessionTabViewModel> attached,
        SessionTabViewModel? active,
        IReadOnlyList<SessionTabViewModel> detached)
    {
        ArgumentNullException.ThrowIfNull(attached);
        ArgumentNullException.ThrowIfNull(detached);

        if (!enabled
            || gesture.Modifiers != ModifierKeys.None
            || gesture.ClickCount != SingleClickCount
            || gesture.Dragged
            || !gesture.PlainSelectionPress
            || !ReferenceEquals(pressedTarget, releasedTarget)
            || pressedTarget is not ServerItemViewModel server
            || ConnectionTypeCatalog.IsToolConnectionType(server.ConnectionType)
            || string.IsNullOrWhiteSpace(server.Id))
        {
            return TreeClickActivation.Nothing;
        }

        if (active is not null && ShowsSession(active, server.Id))
        {
            return TreeClickActivation.Nothing;
        }

        SessionTabViewModel? tab = attached.FirstOrDefault(candidate => ShowsSession(candidate, server.Id));
        if (tab is not null)
        {
            return new TreeClickActivation(TreeClickActivationKind.AttachedTab, tab);
        }

        SessionTabViewModel? window = detached.FirstOrDefault(candidate => ShowsSession(candidate, server.Id));
        return window is null
            ? TreeClickActivation.Nothing
            : new TreeClickActivation(TreeClickActivationKind.DetachedWindow, window);
    }

    /// <summary>
    /// Whether <paramref name="tab"/> holds the session of <paramref name="serverId"/> in any of its
    /// panes, the secondary panes of a split included.
    /// </summary>
    private static bool ShowsSession(SessionTabViewModel tab, string serverId)
        => SplitTreeHelper.EnumerateLeaves(tab.RootContent).Any(pane =>
            string.Equals(pane.ProfileLookupServerId, serverId, StringComparison.Ordinal));
}
