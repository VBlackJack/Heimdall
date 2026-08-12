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
/// Pure builder that maps Citrix session inputs (host, application title, duration, end trigger)
/// into <see cref="SessionEventRecord"/> values. Citrix exposes only binary alive/dead liveness and
/// no protocol reason code, so the reason fields are always null; the honest end-of-session cause is
/// carried by <see cref="SessionEventRecord.EndTrigger"/> ("remote" / "user" / "teardown"). Shares
/// host and duration resolution with the other graphical factories through
/// <see cref="GraphicalSessionEventHelpers"/>.
/// </summary>
internal static class CitrixSessionEventFactory
{
    private const string Protocol = "CITRIX";

    /// <summary>Builds a connect event for the given host and application title.</summary>
    public static SessionEventRecord BuildConnected(string? rawHost, string? title)
        => SessionEventRecord.Connected(Protocol, ResolveEventHost(rawHost, title), title);

    /// <summary>
    /// Builds a disconnect event. Reason and code are null (Citrix carries no protocol reason); the
    /// <paramref name="endTrigger"/> ("remote" / "user" / "teardown") records how the session ended.
    /// </summary>
    public static SessionEventRecord BuildDisconnected(
        string? rawHost,
        string? title,
        long? durationMs,
        string endTrigger)
    {
        return SessionEventRecord.Disconnected(
            Protocol,
            ResolveEventHost(rawHost, title),
            title,
            reasonKey: null,
            reasonCode: null,
            durationMs,
            endTrigger);
    }

    private static string ResolveEventHost(string? rawHost, string? title)
    {
        string? sanitizedHost = CitrixStoreFrontUrlSanitizer.Sanitize(rawHost);
        return GraphicalSessionEventHelpers.ResolveHost(sanitizedHost, title);
    }
}
