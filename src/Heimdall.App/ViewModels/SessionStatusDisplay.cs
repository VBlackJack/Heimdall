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
/// Turns a session status token into the localization key that names it.
/// </summary>
/// <remarks>
/// Two surfaces state a session's condition: the tab header, through a value converter,
/// and the application status bar. They used to be free to disagree, and did. Holding the
/// mapping once means a token added to <see cref="SessionStatusTokens"/> either appears in
/// both or in neither.
/// </remarks>
public static class SessionStatusDisplay
{
    /// <summary>
    /// The localization key for a status token, or null when the value is not a token.
    /// </summary>
    /// <remarks>
    /// Null is a real answer, not a failure. A pane that failed carries the reason as free
    /// text, and both callers show it unchanged rather than hiding a diagnostic behind a
    /// generic label.
    /// </remarks>
    public static string? ResolveKey(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        if (string.Equals(status, SessionStatusTokens.Connected, StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusConnected";
        }

        if (string.Equals(status, SessionStatusTokens.RemoteSessionHandedOff, StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusRemoteSessionHandedOff";
        }

        if (string.Equals(status, SessionStatusTokens.Connecting, StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusConnecting";
        }

        if (string.Equals(status, SessionStatusTokens.Disconnected, StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusDisconnected";
        }

        if (string.Equals(status, SessionStatusTokens.Disconnecting, StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusDisconnecting";
        }

        if (string.Equals(status, SessionStatusTokens.Reconnecting, StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusReconnecting";
        }

        if (string.Equals(status, SessionStatusTokens.Error, StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusError";
        }

        if (string.Equals(status, SessionStatusTokens.LaunchedExternalClient, StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusLaunchedExternalClient";
        }

        return null;
    }

    /// <summary>
    /// True when the token names a session that has reached its destination, which is the
    /// only case the status bar states as a sentence of its own.
    /// </summary>
    public static bool IsEstablished(string? status)
    {
        return string.Equals(status, SessionStatusTokens.Connected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SessionStatusTokens.RemoteSessionHandedOff, StringComparison.OrdinalIgnoreCase);
    }
}
