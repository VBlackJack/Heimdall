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

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// Decides whether a live region has anything new to say.
/// </summary>
/// <remarks>
/// <para>The status line and the health dot are rewritten by every state handler on the way
/// through a transition, whether or not the text moved. Each rewrite raised a live-region event,
/// so one connection announced "Connected" from the phase change, again from the session status,
/// and the health dot twice more in between: six announcements for one event, on a channel that
/// exists to be heard once.</para>
/// <para>One latch per element. Announcing is still the view's call; this only remembers what
/// was last said there, and says no when the new text is the same.</para>
/// </remarks>
internal sealed class RdpAnnouncementLatch
{
    private string? _lastAnnounced;

    /// <summary>Whether <paramref name="text"/> differs from what this region last announced.</summary>
    /// <remarks>Records the text when it does, so the next identical write is refused.</remarks>
    internal bool ShouldAnnounce(string? text)
    {
        if (string.Equals(_lastAnnounced, text, StringComparison.Ordinal))
        {
            return false;
        }

        _lastAnnounced = text;
        return true;
    }
}
