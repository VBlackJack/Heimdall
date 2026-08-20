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

using System.Drawing;

namespace Heimdall.Rdp.Display;

/// <summary>
/// Whether a set of monitors forms a single connected block on the virtual desktop.
/// </summary>
/// <remarks>
/// <para>The question this answers is connectedness, <b>not</b> whether the monitors form a
/// rectangle. Windows Vista required a multi-monitor session to span a rectangle; Windows 7 removed
/// that requirement, so an L-shaped arrangement is a perfectly good multi-monitor session and must
/// be accepted. Re-introducing a rectangularity test here would reject arrangements that work.</para>
/// <para>Two monitors count as connected when they share a border of non-zero length, or overlap.
/// Meeting only at a corner is not enough: there is no border to carry the desktop across, and the
/// session would be describing two islands.</para>
/// </remarks>
public static class RdpMonitorContiguity
{
    /// <summary>
    /// Whether two monitors share a border of non-zero length, or overlap.
    /// </summary>
    /// <remarks>
    /// <para>Arithmetic is done in <see cref="long"/> so that a right or bottom edge far out on the
    /// virtual desktop cannot overflow while being compared.</para>
    /// <para>The single expression covers all four cases. Both overlaps positive means the monitors
    /// overlap; exactly one being zero with the other positive means they meet along a real edge;
    /// both zero means they meet only at a corner; either being negative means there is a gap.</para>
    /// </remarks>
    public static bool Touch(Rectangle first, Rectangle second)
    {
        long horizontal = Math.Min((long)first.Right, second.Right)
            - Math.Max((long)first.Left, second.Left);
        long vertical = Math.Min((long)first.Bottom, second.Bottom)
            - Math.Max((long)first.Top, second.Top);

        return horizontal >= 0
            && vertical >= 0
            && (horizontal > 0 || vertical > 0);
    }

    /// <summary>
    /// Whether every monitor in the set is reachable from every other through shared borders.
    /// </summary>
    /// <param name="monitorBounds">Bounds of the monitors under consideration.</param>
    /// <returns>
    /// <see langword="true"/> when the set forms one connected block. Sets of fewer than two
    /// monitors are connected by definition: there is nothing for them to be disconnected from, and
    /// the caller decides separately whether an empty set means anything of its own.
    /// </returns>
    public static bool AreContiguous(IReadOnlyList<Rectangle> monitorBounds)
    {
        ArgumentNullException.ThrowIfNull(monitorBounds);

        if (monitorBounds.Count < 2)
        {
            return true;
        }

        bool[] reached = new bool[monitorBounds.Count];
        Stack<int> pending = new();
        pending.Push(0);
        reached[0] = true;
        int reachedCount = 1;

        while (pending.Count > 0)
        {
            int current = pending.Pop();
            for (int candidate = 0; candidate < monitorBounds.Count; candidate++)
            {
                if (reached[candidate] || !Touch(monitorBounds[current], monitorBounds[candidate]))
                {
                    continue;
                }

                reached[candidate] = true;
                reachedCount++;
                pending.Push(candidate);
            }
        }

        return reachedCount == monitorBounds.Count;
    }
}
