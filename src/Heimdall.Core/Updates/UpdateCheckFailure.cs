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

namespace Heimdall.Core.Updates;

/// <summary>
/// Why an update check could not produce an answer.
/// </summary>
/// <remarks>
/// <para>
/// A-17. Every one of these used to arrive as a single null and be reported as "Update check
/// failed. See the log for details." The user was told to read a log they cannot find, about
/// causes that call for completely different responses: one of these is fixed by connecting
/// to the internet, one by waiting an hour, one by the maintainer, and one by nobody at all.
/// </para>
/// <para>
/// These are the client's reading of what happened, not a transcript of the protocol. Two
/// HTTP statuses can land on the same member when the user's next move is the same, and the
/// log keeps the status either way.
/// </para>
/// </remarks>
public enum UpdateCheckFailure
{
    /// <summary>The check succeeded. No failure to report.</summary>
    None = 0,

    /// <summary>The host could not be reached: no route, no DNS, no listener.</summary>
    NetworkUnreachable,

    /// <summary>
    /// The connection was made but could not be secured.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="NetworkUnreachable"/> because it is usually not a network
    /// fault at all: an intercepting proxy, a clock far enough out to invalidate a
    /// certificate, or a machine missing a root. "You are offline" is wrong and unhelpful
    /// advice for every one of those.
    /// </remarks>
    SecureChannelFailed,

    /// <summary>The quota for this caller is spent; the answer exists but is withheld.</summary>
    RateLimited,

    /// <summary>
    /// A proxy stood between the two and would not open the tunnel.
    /// </summary>
    /// <remarks>
    /// Its own member rather than a shade of <see cref="NetworkUnreachable"/>, because the
    /// network is working perfectly: the request reached a proxy that declined it, usually
    /// for want of credentials. Nothing about connecting to the internet will help.
    /// </remarks>
    ProxyRefused,

    /// <summary>The repository, or its releases, could not be found at all.</summary>
    SourceNotFound,

    /// <summary>The source answered, but not with something this client can read.</summary>
    MalformedResponse,

    /// <summary>The request was refused for a reason other than a spent quota.</summary>
    AccessDenied,

    /// <summary>The source failed on its own side and may work later.</summary>
    SourceUnavailable,

    /// <summary>The request outlived the client's budget without an answer.</summary>
    TimedOut,
}
