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

using Heimdall.Terminal;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Decides when the plink password file may be deleted, and deletes it once.
/// </summary>
/// <remarks>
/// <para>The file exists only so plink can read the password out of it. Measured against the
/// PuTTY 0.83 source and against the shipped 0.83 binary: <c>-pwfile</c> is handled inside
/// <c>cmdline_process_param</c> while the command line is being parsed, one line is read, and the
/// handle is closed immediately - all of it before any network activity. Pointing <c>-pwfile</c> at
/// a missing file against an unreachable host reports the file error at once, where a readable file
/// against the same host instead spends the full network timeout, which is what puts the read
/// strictly ahead of the connection.</para>
/// <para>So the first byte plink writes to stdout or stderr is proof that the password has already
/// been read and the handle closed: any output at all comes after the command line was parsed. That
/// is the signal used here. It is a real proof, not a delay - a timer would only prove that time
/// passed, and process start would not even prove that the child finished parsing its arguments.</para>
/// <para>What this does NOT cover, and the reason SSH-013 stays open: a session that connects and
/// then stays completely silent produces no first byte, so its file waits for process exit as
/// before. Heimdall does not pass <c>-v</c>, which would make plink announce the connection
/// unconditionally, because that would change what the user sees in the terminal. Closing the
/// remaining gap needs a signal that survives a silent session, measured against a real server.</para>
/// <para>Process exit therefore stays wired as a backstop, and the deletion runs exactly once
/// whichever signal arrives first.</para>
/// </remarks>
internal static class PlinkPasswordFileRelease
{
    /// <summary>
    /// Arms the deletion of <paramref name="passwordFilePath"/> on the first proof that plink has
    /// consumed it, with process exit as a backstop. Call before starting the session so no output
    /// can be missed.
    /// </summary>
    /// <param name="session">The session that will run plink.</param>
    /// <param name="passwordFilePath">The file to delete once consumption is proved.</param>
    /// <param name="delete">Performs the deletion. Invoked at most once.</param>
    internal static void Arm(
        ITerminalSession session,
        string passwordFilePath,
        Action<string?> delete)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(delete);

        // Both handlers are assigned before either subscription, so the release path can always
        // unsubscribe both no matter which one fires first.
        int released = 0;
        Action<ReadOnlyMemory<byte>>? onData = null;
        Action<int>? onExit = null;

        void Release()
        {
            // Sequentially, what keeps this single is the unsubscription below: once both handlers
            // are detached, no later byte or exit reaches this method at all. The exchange covers
            // the case the unsubscription cannot, which is the two signals arriving concurrently on
            // different threads. That window is a couple of instructions wide, so the suite cannot
            // falsify it deterministically - a non-atomic guard survives the race oracle - and it
            // is kept because it is free, not because a test proves it.
            if (Interlocked.Exchange(ref released, 1) != 0)
            {
                return;
            }

            if (onData is not null)
            {
                session.DataReceived -= onData;
            }

            if (onExit is not null)
            {
                session.ProcessExited -= onExit;
            }

            delete(passwordFilePath);
        }

        onData = _ => Release();
        onExit = _ => Release();

        session.DataReceived += onData;
        session.ProcessExited += onExit;
    }
}
