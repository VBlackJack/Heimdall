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
/// Owns per-session plain-text transcript writers: batched flush to disk, size-based
/// rollover, and restrictive ACLs. Captures decoded, ANSI-stripped terminal output only;
/// it does not read application settings and does not gate on any policy (gating is wired
/// by a later lot). Thread-safe for concurrent calls across different session keys.
/// </summary>
public interface ISessionLogService : IDisposable
{
    /// <summary>
    /// Begins recording for the given session and writes a header. If a session with the same
    /// key is already active it is stopped first.
    /// </summary>
    /// <param name="context">The session description.</param>
    /// <returns>The full path of the created log file, or <c>null</c> if recording could not start.</returns>
    string? StartSession(SessionLogContext context);

    /// <summary>
    /// Records a chunk of raw terminal output bytes for the given session. Bytes are UTF-8
    /// decoded and stripped of ANSI/VT escape sequences before being buffered. No-op when the
    /// session is unknown or no longer active. Never throws into the caller.
    /// </summary>
    /// <param name="sessionKey">The key supplied to <see cref="StartSession"/>.</param>
    /// <param name="data">Raw output bytes, in stream order.</param>
    void WriteOutput(string sessionKey, ReadOnlySpan<byte> data);

    /// <summary>
    /// Stops recording for the given session: writes a footer with the session duration,
    /// flushes, and releases the underlying file. No-op when the session is unknown.
    /// </summary>
    /// <param name="sessionKey">The key supplied to <see cref="StartSession"/>.</param>
    void StopSession(string sessionKey);

    /// <summary>
    /// Indicates whether a session with the given key is currently recording.
    /// </summary>
    /// <param name="sessionKey">The key supplied to <see cref="StartSession"/>.</param>
    /// <returns><c>true</c> when the session is active; otherwise <c>false</c>.</returns>
    bool IsSessionActive(string sessionKey);
}
