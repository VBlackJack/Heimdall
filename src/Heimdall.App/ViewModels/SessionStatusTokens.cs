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

namespace Heimdall.App.ViewModels;

/// <summary>
/// The values a session pane's status may carry.
/// </summary>
/// <remarks>
/// <para>A pane's status is a token, not a sentence. It is parsed back into a connection state to
/// decide whether a session counts as live, and it is turned into text for display by a converter.
/// Writing localized text into it breaks both: the parse fails, and the text is shown twice over
/// or, worse, shown with its format placeholder unfilled.</para>
/// <para>The vocabulary has two tiers, and the difference matters. Most tokens name a connection
/// state, so they parse and take part in the census of live sessions. Two do not exist as
/// connection states at all and are display-only; a pane carrying one is deliberately not counted
/// as connected, which is right for a session that is still being established.</para>
/// <para>There is a third case, which is not a token at all: a pane that failed carries the reason
/// it failed, as free text. The display converter passes an unrecognised value through unchanged
/// for exactly that, and a failed session is not counted as live whichever way it is written.</para>
/// <para>Naming these here is what lets a test find every site that writes something else.</para>
/// </remarks>
public static class SessionStatusTokens
{
    /// <summary>A session that reached its destination.</summary>
    public const string Connected = "Connected";

    /// <summary>A session handed off to an external process.</summary>
    public const string LaunchedExternalClient = "LaunchedExternalClient";

    /// <summary>A session whose remote end owns the interaction, as WinRM does.</summary>
    public const string RemoteSessionHandedOff = "RemoteSessionHandedOff";

    /// <summary>A session that ended.</summary>
    public const string Disconnected = "Disconnected";

    /// <summary>A session on its way out.</summary>
    public const string Disconnecting = "Disconnecting";

    /// <summary>A session that failed.</summary>
    public const string Error = "Error";

    /// <summary>
    /// A session being established. Display-only: no connection state carries this name, so a pane
    /// showing it is not counted among the live sessions, which is the intended answer.
    /// </summary>
    public const string Connecting = "Connecting";

    /// <summary>
    /// A session being re-established. Display-only, for the same reason as
    /// <see cref="Connecting"/>.
    /// </summary>
    public const string Reconnecting = "Reconnecting";
}
