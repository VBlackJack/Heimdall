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

namespace Heimdall.Core.Models;

/// <summary>
/// Pure, side-effect-free resolver that decides which terminal panes should
/// receive broadcast input for a given <see cref="BroadcastScope"/>. Lives in
/// Core with no WPF/COM dependency so it is fully unit-testable and is reused by
/// the per-pane subset selection planned for Lot B.
/// </summary>
public static class BroadcastTargetResolver
{
    /// <summary>
    /// Returns the ordered set of terminal panes that should receive broadcast
    /// input. The originating pane (identified by <paramref name="sender"/>,
    /// compared against <see cref="SessionPaneModel.HostControl"/> by reference)
    /// is always excluded, and only panes accepted by
    /// <paramref name="isBroadcastTarget"/> are included so non-terminal
    /// surfaces (RDP/VNC/SFTP/FTP/Citrix) are never targeted.
    /// </summary>
    /// <param name="sessionRoots">
    /// Root content of every open session tab, in tab order. Used when the
    /// scope spans all tabs.
    /// </param>
    /// <param name="activeSessionRoot">
    /// Root content of the active session tab. Used only by
    /// <see cref="BroadcastScope.CurrentTab"/>; <see cref="BroadcastScope.AllTabs"/>
    /// and <see cref="BroadcastScope.SelectedPanes"/> span every tab.
    /// </param>
    /// <param name="scope">The active broadcast scope.</param>
    /// <param name="sender">
    /// The host control that originated the input; excluded from the result.
    /// </param>
    /// <param name="isBroadcastTarget">
    /// Predicate identifying broadcast-capable terminal panes. The production
    /// caller passes a check for the embedded terminal view type; tests pass a
    /// classifier over a stand-in host control.
    /// </param>
    public static IReadOnlyList<SessionPaneModel> ResolveTargets(
        IReadOnlyList<ISplitContent?> sessionRoots,
        ISplitContent? activeSessionRoot,
        BroadcastScope scope,
        object? sender,
        Func<SessionPaneModel, bool> isBroadcastTarget)
    {
        ArgumentNullException.ThrowIfNull(sessionRoots);
        ArgumentNullException.ThrowIfNull(isBroadcastTarget);

        // AllTabs and SelectedPanes both span every tab; the difference is the
        // predicate (SelectedPanes additionally requires the pane to be marked as a
        // target). CurrentTab is restricted to the active tab. An empty selection
        // therefore yields an empty target set rather than falling back to a wider
        // scope.
        IEnumerable<ISplitContent?> roots = scope is BroadcastScope.AllTabs or BroadcastScope.SelectedPanes
            ? sessionRoots
            : activeSessionRoot is null
                ? []
                : [activeSessionRoot];

        var targets = new List<SessionPaneModel>();
        foreach (var root in roots)
        {
            foreach (var pane in SplitTreeHelper.EnumerateLeaves(root))
            {
                if (!isBroadcastTarget(pane))
                {
                    continue;
                }

                if (sender is not null && ReferenceEquals(pane.HostControl, sender))
                {
                    continue;
                }

                targets.Add(pane);
            }
        }

        return targets;
    }
}
