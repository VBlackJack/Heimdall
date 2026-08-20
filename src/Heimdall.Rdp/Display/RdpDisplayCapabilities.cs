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
/// Current host display topology used for RDP display decisions.
/// </summary>
/// <remarks>
/// The count alone cannot answer whether a selection of monitors forms one connected block, so the
/// bounds travel with it. They are optional: a caller that only needs the count, and a host whose
/// screens could not be enumerated, both leave them empty.
/// </remarks>
public sealed record RdpDisplayCapabilities(int MonitorCount)
{
    /// <summary>
    /// Bounds of each attached monitor on the virtual desktop, in the same order as the indices a
    /// profile selects by. Empty when the topology is not known.
    /// </summary>
    public IReadOnlyList<Rectangle> MonitorBounds { get; init; } = [];

    /// <summary>
    /// Builds capabilities from an enumerated topology, keeping the count and the bounds in step.
    /// </summary>
    /// <remarks>
    /// The list is copied, so a caller cannot change the topology a decision was taken against
    /// after the fact.
    /// </remarks>
    public static RdpDisplayCapabilities FromMonitorBounds(IReadOnlyList<Rectangle> monitorBounds)
    {
        ArgumentNullException.ThrowIfNull(monitorBounds);

        return new RdpDisplayCapabilities(monitorBounds.Count)
        {
            MonitorBounds = [.. monitorBounds],
        };
    }

    /// <summary>
    /// Multimon requires at least two attached screens.
    /// </summary>
    public static bool IsMultimonAvailable(int screenCount) => screenCount >= 2;
}
