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

using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The condition a session tab header shows, folded from the primary pane's status.
/// </summary>
/// <remarks>
/// The strip shows a mark only when a session needs a look. A connected tab carries none, so
/// thirty healthy tabs stay quiet and the six that dropped stand out.
/// </remarks>
public enum SessionTabHeaderState
{
    /// <summary>Connected, handed off, or not reported yet: no mark.</summary>
    Established,

    /// <summary>Connecting, reconnecting or disconnecting.</summary>
    Transitioning,

    /// <summary>The session ended and the tab stayed open.</summary>
    Disconnected,

    /// <summary>The session failed, by token or with a free-text reason.</summary>
    Failed,
}

/// <summary>
/// Folds a pane status into a <see cref="SessionTabHeaderState"/>, and names that state.
/// </summary>
public static class SessionTabHeaderStates
{
    private const string FailedKey = "SessionStatusError";

    /// <summary>
    /// The header state for a status value. A recorded failure wins over the token, and a value
    /// that is not a token is a failure reason written as free text.
    /// </summary>
    public static SessionTabHeaderState From(string? status, bool hasFailureDetails)
        => From(status, hasFailureDetails, connectionType: null);

    /// <summary>
    /// The header state for a status value on a pane of the given connection type. A tool pane
    /// is not a session: its status is free text by design ("Ready"), which must not read as a
    /// failure reason, so a tool tab is marked only by a recorded failure.
    /// </summary>
    public static SessionTabHeaderState From(string? status, bool hasFailureDetails, string? connectionType)
    {
        if (hasFailureDetails)
        {
            return SessionTabHeaderState.Failed;
        }

        if (ConnectionTypeCatalog.IsToolConnectionType(connectionType))
        {
            return SessionTabHeaderState.Established;
        }

        if (string.IsNullOrWhiteSpace(status)
            || SessionStatusDisplay.IsEstablished(status)
            || Is(status, SessionStatusTokens.LaunchedExternalClient))
        {
            return SessionTabHeaderState.Established;
        }

        if (Is(status, SessionStatusTokens.Connecting)
            || Is(status, SessionStatusTokens.Reconnecting)
            || Is(status, SessionStatusTokens.Disconnecting))
        {
            return SessionTabHeaderState.Transitioning;
        }

        if (Is(status, SessionStatusTokens.Disconnected))
        {
            return SessionTabHeaderState.Disconnected;
        }

        return SessionTabHeaderState.Failed;
    }

    /// <summary>
    /// The localization key that names the state of a tab, or null for an established one,
    /// which is not announced. A free-text failure is named by the generic error key: the reason
    /// itself belongs to the pane, not to a tab name read on every arrow press.
    /// </summary>
    public static string? ResolveKey(SessionTabHeaderState state, string? status)
    {
        return state switch
        {
            SessionTabHeaderState.Established => null,
            SessionTabHeaderState.Failed => FailedKey,
            _ => SessionStatusDisplay.ResolveKey(status),
        };
    }

    private static bool Is(string status, string token)
        => string.Equals(status, token, StringComparison.OrdinalIgnoreCase);
}
