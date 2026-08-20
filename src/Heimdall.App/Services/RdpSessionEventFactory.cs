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

using System.Text;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.App.Services;

/// <summary>
/// Pure builder that maps RDP COM lifecycle inputs (host, title, disconnect reason codes, connect
/// timestamp) into <see cref="SessionEventRecord"/> values. Extracted from the view so the reason
/// resolution, duration arithmetic, and host discipline are unit-testable without COM. Mirrors the
/// codebase's pure-resolver pattern (e.g. RdpDisplayResolver).
/// </summary>
internal static class RdpSessionEventFactory
{
    private const string Protocol = "RDP";

    /// <summary>Symbolic reason token used when neither the primary nor extended decoder yields a key.</summary>
    internal const string UnknownReasonKey = "RDP_UNKNOWN";

    /// <summary>
    /// Resolves the host to record: the RDP target host, falling back to the display name when the
    /// host is empty, with any leading "user@" prefix stripped. Delegates to the shared graphical
    /// helper so RDP/VNC/Citrix all resolve hosts identically.
    /// </summary>
    public static string ResolveHost(string? rawHost, string? displayName)
        => GraphicalSessionEventHelpers.ResolveHost(rawHost, displayName);

    /// <summary>
    /// Resolves a stable, symbolic RDP disconnect reason key ("RDP_&lt;UPPER_SNAKE&gt;"), or
    /// "RDP_UNKNOWN" when neither code decodes.
    /// </summary>
    /// <remarks>
    /// Shares <see cref="RdpActiveXHost.ResolveDisconnectReasonKey"/> with the on-screen
    /// diagnostic. This used to compose the two decoders primary-first while the diagnostic
    /// composed them extended-first, so the same disconnect could be named one thing to the user
    /// and another in the log. Both numeric codes travel separately on the record, so a reader can
    /// always recover which decoder produced the key.
    /// </remarks>
    public static string ResolveReasonKey(int reason, int extendedReason)
    {
        string? key = RdpActiveXHost.ResolveDisconnectReasonKey(reason, extendedReason);

        return string.IsNullOrEmpty(key) ? UnknownReasonKey : $"{Protocol}_{ToUpperSnakeCase(key)}";
    }

    /// <summary>
    /// Computes the session duration in milliseconds, or null when the connect instant is unknown.
    /// Delegates to the shared graphical helper.
    /// </summary>
    public static long? ResolveDurationMs(DateTime connectedAtUtc, DateTime nowUtc)
        => GraphicalSessionEventHelpers.ResolveDurationMs(connectedAtUtc, nowUtc);

    /// <summary>Builds a connect event for the given profile fields.</summary>
    public static SessionEventRecord BuildConnected(string? rawHost, string? displayName)
        => SessionEventRecord.Connected(Protocol, ResolveHost(rawHost, displayName), displayName);

    /// <summary>
    /// Builds a disconnect event with the resolved symbolic reason key, the numeric reason code, and
    /// the computed duration. <c>endTrigger</c> is intentionally null for RDP: the reason fields carry
    /// the precise cause and the duration is accurate, so the remote/user/teardown honesty flag is
    /// reserved for VNC/Citrix.
    /// </summary>
    public static SessionEventRecord BuildDisconnected(
        string? rawHost,
        string? displayName,
        int reason,
        int extendedReason,
        DateTime connectedAtUtc,
        DateTime nowUtc)
    {
        return SessionEventRecord.Disconnected(
            Protocol,
            ResolveHost(rawHost, displayName),
            displayName,
            ResolveReasonKey(reason, extendedReason),
            reason,
            ResolveDurationMs(connectedAtUtc, nowUtc),
            endTrigger: null,
            extendedReasonCode: extendedReason);
    }

    /// <summary>
    /// Maps the Heimdall teardown <see cref="DisconnectReason"/> to a session-event end trigger:
    /// a user-initiated disconnect (<see cref="DisconnectReason.UserAction"/>) is "user"; every other
    /// teardown cause (tab close, failed session, app shutdown) is "teardown". Pure and total.
    /// </summary>
    public static string ResolveTeardownTrigger(DisconnectReason reason)
        => reason == DisconnectReason.UserAction ? "user" : "teardown";

    /// <summary>
    /// Builds a reasonless teardown disconnect for the Dispose backstop (a user close or app-shutdown
    /// teardown, which has no MsTscAx reason code). Reason key and code are null; <c>endTrigger</c> is
    /// derived from <paramref name="reason"/> via <see cref="ResolveTeardownTrigger"/> so a user-initiated
    /// disconnect is tagged "user" for cross-protocol parity. The reason-carrying
    /// <see cref="BuildDisconnected"/> path is unaffected.
    /// </summary>
    public static SessionEventRecord BuildTeardownDisconnected(
        string? rawHost,
        string? displayName,
        DisconnectReason reason,
        DateTime connectedAtUtc,
        DateTime nowUtc)
    {
        return SessionEventRecord.Disconnected(
            Protocol,
            ResolveHost(rawHost, displayName),
            displayName,
            reasonKey: null,
            reasonCode: null,
            ResolveDurationMs(connectedAtUtc, nowUtc),
            ResolveTeardownTrigger(reason));
    }

    // Converts a PascalCase reason key (e.g. "BadCredentials") to UPPER_SNAKE ("BAD_CREDENTIALS").
    // ASCII-only; mirrors the symbolic formatting RdpActiveXHost uses for support codes.
    private static string ToUpperSnakeCase(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            if (i > 0 && char.IsUpper(current))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(current));
        }

        return builder.ToString();
    }
}
