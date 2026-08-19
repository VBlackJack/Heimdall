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
/// The single gate through which the SSH password file is deleted, and the decision of when that is
/// allowed to happen early.
/// </summary>
/// <remarks>
/// <para>The file exists only so the launcher can read the password out of it, and it used to live
/// until the session ended. For a launcher whose <c>-pwfile</c> behaviour has been measured, the
/// first byte it writes proves the password has already been read and the handle closed, so the file
/// goes then. See <see cref="PlinkCompatibilityAttestation"/> for what "measured" means and why the
/// executable's bytes are what decides it.</para>
/// <para>For any other executable the first byte proves nothing - a different build may well write
/// something before it reads the file - so the early deletion is withheld and the file is released
/// at process exit, exactly as before. That is a deliberate fail-closed default: withholding the
/// early deletion costs exposure time, while deleting too early would break the authentication.</para>
/// <para>The first byte is a proof and not a delay. A timer would only show that time passed, and
/// process start would not even show that the child finished parsing its arguments. A
/// delete-and-retry loop was rejected as well: it would succeed just as readily before the launcher
/// ever opened the file, and nothing on this side separates that from success.</para>
/// <para>What is still not covered, and the reason SSH-013 stays open: a session that connects and
/// then stays silent writes no first byte, so even an attested launcher waits for process exit.</para>
/// </remarks>
internal static class PlinkPasswordFileRelease
{
    /// <summary>
    /// Arms the deletion of <paramref name="passwordFilePath"/> and returns the handle every caller
    /// must go through. Call before starting the session so no output can be missed.
    /// </summary>
    /// <param name="session">The session that will run the launcher.</param>
    /// <param name="passwordFilePath">The file to delete once it is safe to do so.</param>
    /// <param name="delete">Performs the deletion. Invoked at most once, from any path.</param>
    /// <param name="firstByteProvesConsumption">
    /// Whether the launcher's first byte of output proves it already read the password file. When
    /// <see langword="false"/>, output is not listened to at all and only process exit or an
    /// explicit <see cref="PlinkPasswordFileReleaseHandle.Release"/> frees the file.
    /// </param>
    internal static PlinkPasswordFileReleaseHandle Arm(
        ITerminalSession session,
        string passwordFilePath,
        Action<string?> delete,
        bool firstByteProvesConsumption)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(delete);

        return new PlinkPasswordFileReleaseHandle(
            session,
            passwordFilePath,
            delete,
            firstByteProvesConsumption);
    }
}

/// <summary>
/// Owns one password file and deletes it once, whichever path gets there first.
/// </summary>
/// <remarks>
/// The launch failure and cancellation paths dispose the session, which can itself raise
/// <see cref="ITerminalSession.ProcessExited"/>, and then release explicitly. Both arrive here, so
/// the deletion still runs a single time.
/// </remarks>
internal sealed class PlinkPasswordFileReleaseHandle
{
    private readonly ITerminalSession _session;
    private readonly string _passwordFilePath;
    private readonly Action<string?> _delete;
    private readonly Action<ReadOnlyMemory<byte>>? _onData;
    private readonly Action<int> _onExit;

    private int _released;

    internal PlinkPasswordFileReleaseHandle(
        ITerminalSession session,
        string passwordFilePath,
        Action<string?> delete,
        bool firstByteProvesConsumption)
    {
        _session = session;
        _passwordFilePath = passwordFilePath;
        _delete = delete;

        _onExit = _ => Release();
        _session.ProcessExited += _onExit;

        if (!firstByteProvesConsumption)
        {
            // Not subscribing at all, rather than subscribing and ignoring: an unattested launcher
            // must have no path from its output to the deletion, not merely a disabled one.
            return;
        }

        _onData = _ => Release();
        _session.DataReceived += _onData;
    }

    /// <summary>
    /// Deletes the password file if it has not been deleted yet, and stops listening.
    /// </summary>
    /// <remarks>
    /// Sequentially, what keeps this single is the unsubscription: once the handlers are detached,
    /// no later byte or exit reaches this method. The exchange covers what the unsubscription cannot,
    /// which is two signals arriving concurrently on different threads. That window is a couple of
    /// instructions wide, so the suite cannot falsify it - a non-atomic guard survives the race
    /// oracle - and it is kept because it is free, not because a test proves it.
    /// </remarks>
    internal void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        if (_onData is not null)
        {
            _session.DataReceived -= _onData;
        }

        _session.ProcessExited -= _onExit;

        _delete(_passwordFilePath);
    }
}
