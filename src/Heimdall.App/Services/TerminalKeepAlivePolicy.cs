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
/// Decides when the embedded terminal writes its TMOUT-reset carriage return, and for which
/// sessions the timer exists at all.
/// </summary>
/// <remarks>
/// <para>The reset is a bare CR written into the shell so a remote <c>TMOUT</c> does not log the
/// user out. It is not a transport keepalive: SSH.NET already sends those below the shell. A CR
/// is also a keystroke, and one written while the user is typing submits a half-typed line, an
/// empty password to <c>sudo</c>, or Enter inside vim. So a tick is skipped whenever real input
/// reached the session during the last interval: an active user keeps <c>TMOUT</c> reset by
/// typing, and the CR only goes out into an idle shell.</para>
/// <para>Only an SSH shell has a remote <c>TMOUT</c> to reset. The same view hosts the local shell
/// and WinRM sessions, where the CR is nothing but a stray Enter, so those get no timer.</para>
/// </remarks>
internal static class TerminalKeepAlivePolicy
{
    /// <summary>Sentinel for "no terminal input has been written yet".</summary>
    public const long NoInputRecorded = -1;

    private const string SshConnectionType = "SSH";
    private const int MillisecondsPerSecond = 1000;

    /// <summary>
    /// Whether a session of this connection type has a remote <c>TMOUT</c> worth resetting.
    /// </summary>
    public static bool AppliesTo(string? connectionType) =>
        string.Equals(connectionType, SshConnectionType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The interval the timer should run at: the requested one for SSH, zero (no timer) for
    /// every other session type.
    /// </summary>
    public static int ResolveIntervalSeconds(string? connectionType, int requestedIntervalSeconds) =>
        AppliesTo(connectionType) ? requestedIntervalSeconds : 0;

    /// <summary>
    /// Whether the tick at <paramref name="nowMilliseconds"/> should write the reset, given the
    /// timestamp of the last real terminal input and the timer interval.
    /// </summary>
    /// <param name="nowMilliseconds">The current monotonic clock, in milliseconds.</param>
    /// <param name="lastInputMilliseconds">
    /// The monotonic clock when real input was last written, or <see cref="NoInputRecorded"/>.
    /// </param>
    /// <param name="intervalSeconds">The timer interval, in seconds.</param>
    public static bool ShouldSendTick(long nowMilliseconds, long lastInputMilliseconds, int intervalSeconds)
    {
        if (lastInputMilliseconds == NoInputRecorded)
        {
            return true;
        }

        long intervalMilliseconds = (long)intervalSeconds * MillisecondsPerSecond;
        return nowMilliseconds - lastInputMilliseconds >= intervalMilliseconds;
    }
}
