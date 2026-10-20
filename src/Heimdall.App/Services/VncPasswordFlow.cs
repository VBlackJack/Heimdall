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

/// <summary>Asks for, keeps and drops the password of one VNC session the profile cannot hold.</summary>
public interface IVncPasswordKeeper
{
    /// <summary>Asks the user for the password; null when they cancelled.</summary>
    Task<PasswordPromptResult?> AskAsync(CancellationToken cancellationToken);

    /// <summary>Keeps <paramref name="password"/> for the next connections.</summary>
    Task RememberAsync(string password);

    /// <summary>Drops the kept password, if any.</summary>
    Task ForgetAsync();
}

/// <summary>
/// The password a VNC view sends, and what happens to it once the server answered.
/// </summary>
/// <remarks>
/// <para>A VNC connection returns before the VNC sign-in starts, so only the view learns whether
/// the password was right: the server accepting it is the session connecting, the server refusing
/// it is a security failure raised after the password was sent. This class turns those answers
/// into keeping or dropping the password, and holds no WPF and no browser so it can be tested on
/// its own.</para>
/// <para>Without a keeper (a profile of the user's own, which holds its password) it only carries
/// the password the connection was given.</para>
/// </remarks>
internal sealed class VncPasswordFlow
{
    private readonly IVncPasswordKeeper? _keeper;
    private bool _rememberOnConnect;

    /// <param name="password">The password the connection was given, if any.</param>
    /// <param name="keeper">Asks, keeps and drops; null when the profile holds the password.</param>
    internal VncPasswordFlow(string? password, IVncPasswordKeeper? keeper)
    {
        Password = string.IsNullOrEmpty(password) ? null : password;
        _keeper = keeper;
    }

    /// <summary>The password the next connect sends, or null.</summary>
    internal string? Password { get; private set; }

    /// <summary>Whether the user can type the password here rather than in the profile.</summary>
    internal bool CanAsk => _keeper is not null;

    /// <summary>
    /// Whether the server asked for the password and is still waiting for it on the open
    /// connection, so an answer goes to that connection rather than to a new one.
    /// </summary>
    /// <remarks>
    /// Ending the attempt instead costs a failure on servers that count an abandoned sign-in as a
    /// wrong password: TigerVNC blocks the address after a few of them. TigerVNC keeps the sign-in
    /// open for as long as the user takes to type.
    /// </remarks>
    internal bool ServerWaiting { get; private set; }

    /// <summary>The server asked for the password on the open connection and waits for it.</summary>
    internal void OnServerWaiting()
    {
        if (_keeper is not null)
        {
            ServerWaiting = true;
        }
    }

    /// <summary>The attempt ended, or got its answer: the next one is a new connection.</summary>
    internal void OnAttemptEnded() => ServerWaiting = false;

    /// <summary>
    /// Takes the open connection for the answer just typed: true when it still waits, and from then
    /// on it no longer does.
    /// </summary>
    internal bool TakeWaitingServer()
    {
        bool waiting = ServerWaiting;
        ServerWaiting = false;
        return waiting;
    }

    /// <summary>
    /// Asks for the password. On an answer it becomes the one the next connect sends, and is kept
    /// once the server accepts it if the user asked for that.
    /// </summary>
    /// <returns>Whether the user answered; false when they cancelled or nothing can ask.</returns>
    internal async Task<bool> AskAsync(CancellationToken cancellationToken)
    {
        if (_keeper is null)
        {
            return false;
        }

        PasswordPromptResult? answer = await _keeper.AskAsync(cancellationToken).ConfigureAwait(true);
        if (answer is null || string.IsNullOrEmpty(answer.Password))
        {
            return false;
        }

        Password = answer.Password;
        _rememberOnConnect = answer.Remember;
        return true;
    }

    /// <summary>The server accepted the password: keep it if the user asked to.</summary>
    internal async Task OnConnectedAsync()
    {
        ServerWaiting = false;
        if (!_rememberOnConnect || _keeper is null || Password is null)
        {
            return;
        }

        _rememberOnConnect = false;
        await _keeper.RememberAsync(Password).ConfigureAwait(true);
    }

    /// <summary>
    /// The server ended the sign-in with a security failure.
    /// </summary>
    /// <param name="afterPassword">
    /// Whether the password had been sent. A refusal before it (a server that offers no security
    /// type it shares with the client, or one blocking this address after too many failures) says
    /// nothing of the password, so the kept one stays.
    /// </param>
    internal async Task OnSecurityFailureAsync(bool afterPassword)
    {
        ServerWaiting = false;
        if (!afterPassword)
        {
            return;
        }

        _rememberOnConnect = false;
        Password = null;
        if (_keeper is not null)
        {
            await _keeper.ForgetAsync().ConfigureAwait(true);
        }
    }
}
