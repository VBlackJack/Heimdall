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
/// Pure, side-effect-controlled helpers for per-session broadcast target selection
/// (Lot B tab-strip). They operate on a session's split-pane tree and a predicate
/// that identifies broadcast-capable terminal panes, with no WPF/COM dependency so
/// the selection logic is unit-testable. Mutations are confined to
/// <see cref="SessionPaneModel.IsBroadcastTarget"/>.
/// </summary>
public static class BroadcastTargetSelection
{
    /// <summary>True when the session tree contains at least one terminal pane.</summary>
    public static bool SessionHasTerminal(ISplitContent? root, Func<SessionPaneModel, bool> isTerminal)
    {
        ArgumentNullException.ThrowIfNull(isTerminal);
        return SplitTreeHelper.EnumerateLeaves(root).Any(isTerminal);
    }

    /// <summary>
    /// True when the session has terminal panes and all of them are marked as
    /// broadcast targets. False for sessions with no terminal pane.
    /// </summary>
    public static bool IsSessionTargeted(ISplitContent? root, Func<SessionPaneModel, bool> isTerminal)
    {
        ArgumentNullException.ThrowIfNull(isTerminal);
        var terminals = SplitTreeHelper.EnumerateLeaves(root).Where(isTerminal).ToList();
        return terminals.Count > 0 && terminals.TrueForAll(p => p.IsBroadcastTarget);
    }

    /// <summary>
    /// Flips the session's broadcast-target membership: marks every terminal pane
    /// when not all are currently marked, otherwise unmarks them all. Returns the
    /// new aggregate value, or <c>null</c> when the session has no terminal pane
    /// (non-selectable, e.g. RDP/VNC/SFTP/FTP/Citrix).
    /// </summary>
    public static bool? ToggleSession(ISplitContent? root, Func<SessionPaneModel, bool> isTerminal)
    {
        ArgumentNullException.ThrowIfNull(isTerminal);
        var terminals = SplitTreeHelper.EnumerateLeaves(root).Where(isTerminal).ToList();
        if (terminals.Count == 0)
        {
            return null;
        }

        bool newValue = !terminals.TrueForAll(p => p.IsBroadcastTarget);
        foreach (var pane in terminals)
        {
            pane.IsBroadcastTarget = newValue;
        }

        return newValue;
    }

    /// <summary>Counts terminal panes currently marked as broadcast targets across the given session trees.</summary>
    public static int CountTargets(IEnumerable<ISplitContent?> roots, Func<SessionPaneModel, bool> isTerminal)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(isTerminal);

        int count = 0;
        foreach (var root in roots)
        {
            foreach (var pane in SplitTreeHelper.EnumerateLeaves(root))
            {
                if (isTerminal(pane) && pane.IsBroadcastTarget)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
