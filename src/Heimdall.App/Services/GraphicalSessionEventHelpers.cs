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

namespace Heimdall.App.Services;

/// <summary>
/// Protocol-agnostic helpers shared by the graphical-protocol session-event factories
/// (RDP / VNC / Citrix): host resolution with defensive "user@" stripping and a fallback, and
/// session-duration arithmetic. Pure and side-effect free so every factory maps inputs the same way.
/// </summary>
internal static class GraphicalSessionEventHelpers
{
    /// <summary>
    /// Resolves the host to record: the target host, falling back to <paramref name="fallback"/>
    /// (typically the display name / title) when the host is empty, with any leading "user@" prefix
    /// stripped (the sink strips it again as defense in depth; doing it here keeps the resolved value
    /// honest for callers and tests).
    /// </summary>
    public static string ResolveHost(string? rawHost, string? fallback)
    {
        string host = string.IsNullOrWhiteSpace(rawHost) ? (fallback ?? string.Empty) : rawHost;
        return StripUserPrefix(host.Trim());
    }

    /// <summary>
    /// Computes the session duration in milliseconds, or null when the connect instant is unknown
    /// (e.g. a failed connect that never reached the connected state). Negative spans clamp to zero.
    /// </summary>
    public static long? ResolveDurationMs(DateTime connectedAtUtc, DateTime nowUtc)
    {
        if (connectedAtUtc == default)
        {
            return null;
        }

        double ms = (nowUtc - connectedAtUtc).TotalMilliseconds;
        return ms < 0 ? 0L : (long)ms;
    }

    // Strips a leading "user@" so a "user@host" endpoint cannot leak an identity into the event log.
    // Splits on the first '@'; values without one pass through.
    private static string StripUserPrefix(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return host;
        }

        int at = host.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? host[(at + 1)..] : host;
    }
}
