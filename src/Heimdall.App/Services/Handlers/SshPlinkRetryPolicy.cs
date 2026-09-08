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
/// Which authentication refusals are worth retrying through the embedded Plink.
/// </summary>
/// <remarks>
/// <para>
/// One predicate with one caller, rather than a condition written inline at the retry site.
/// The set is a decision about what a second, interactive attempt could plausibly change, and
/// a decision worth naming is worth putting somewhere a test can reach without a process.
/// </para>
/// </remarks>
internal static class SshPlinkRetryPolicy
{
    /// <summary>
    /// Whether a refusal carrying <paramref name="code"/> should be retried through Plink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SshFailureCode.KeyboardInteractiveUnsupportedPrompt"/> is the member this
    /// predicate exists for. The embedded client cannot answer a verification code: it has one
    /// secret and no way to ask for another. Plink can, because Heimdall starts it with a real
    /// console in the terminal pane, so the server's remaining questions reach somebody able to
    /// type an answer. Retrying is the only thing that turns an unreachable host into a
    /// reachable one, and it is why this code belongs here.
    /// </para>
    /// <para>
    /// It deliberately does NOT join
    /// <c>SftpPasswordPromptPolicy.AllowsPasswordRetry</c>. That decision opens a password box,
    /// and a password box does not answer a verification code: it would ask the user for a
    /// secret that was already correct and spend another attempt failing with it. Two sets, two
    /// questions, and the same code belongs in one and not the other.
    /// </para>
    /// <para>
    /// <see cref="SshFailureCode.TooManyAuthFailures"/> stays out of both. The server is already
    /// counting attempts against this account, and another attempt is the one thing that can
    /// make the outcome worse rather than better.
    /// </para>
    /// </remarks>
    internal static bool AllowsPlinkRetry(SshFailureCode code) =>
        code is SshFailureCode.AuthRejected
            or SshFailureCode.KeyRejected
            or SshFailureCode.PassphraseRejected
            or SshFailureCode.PasswordRejected
            or SshFailureCode.NoSupportedAuth
            or SshFailureCode.KeyboardInteractiveNoPassword
            or SshFailureCode.KeyboardInteractiveUnsupportedPrompt;
}
