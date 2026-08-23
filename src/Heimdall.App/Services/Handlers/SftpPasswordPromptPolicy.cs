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

using Heimdall.Ssh;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// When a failed SFTP connection is worth offering a password for, and how many times.
/// </summary>
/// <remarks>
/// Separate from the handler so both decisions can be pinned without a socket, a dialog
/// or a tunnel - and so widening the set later is a visible edit rather than a condition
/// that drifts inside a long method.
/// </remarks>
internal static class SftpPasswordPromptPolicy
{
    /// <summary>
    /// The original attempt plus one retry with a typed password. Never a loop.
    /// </summary>
    /// <remarks>
    /// Each attempt that carries a password registers more than one failed authentication
    /// on the remote, because the connection offers both password and keyboard-interactive
    /// methods with the same secret. A loop would walk a user into a lockout policy they
    /// cannot see from here, so a typo costs one more press of Connect rather than an
    /// account.
    /// </remarks>
    internal const int MaxConnectAttempts = 2;

    /// <summary>
    /// True when the failure means a password could plausibly fix it.
    /// </summary>
    /// <remarks>
    /// A strict subset of the set that gates SSH's plink fallback, and deliberately not
    /// the same list. That set opens a path that can also do KEY authentication, so it
    /// legitimately includes key and passphrase rejections. This one opens a password
    /// box, which cannot fix a key - sharing the literal would share text where the two
    /// decisions genuinely differ.
    /// <para>
    /// <see cref="SshFailureCode.TooManyAuthFailures"/> is excluded, and terminal even on
    /// the first attempt. Retrying opens a NEW connection, which resets the server's
    /// per-connection attempt counter - that is precisely the shape that walks an account
    /// into a lockout rather than away from one.
    /// </para>
    /// <para>
    /// <see cref="SshFailureCode.PasswordRejected"/> is included by arbitration: a profile
    /// whose stored password has gone stale is otherwise a permanent dead end. The
    /// classifier is context-aware, so the same server refusal reads as
    /// <see cref="SshFailureCode.KeyboardInteractiveNoPassword"/> when nothing was sent
    /// and as a rejection once something was.
    /// </para>
    /// </remarks>
    internal static bool AllowsPasswordRetry(SshFailureCode code) =>
        code is SshFailureCode.NoSupportedAuth
            or SshFailureCode.KeyboardInteractiveNoPassword
            or SshFailureCode.AuthRejected
            or SshFailureCode.PasswordRejected;
}
