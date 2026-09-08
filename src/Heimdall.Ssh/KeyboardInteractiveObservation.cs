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

namespace Heimdall.Ssh;

/// <summary>
/// What the keyboard-interactive exchange of one connection attempt asked for and
/// could not be answered.
/// </summary>
/// <remarks>
/// The handler answers a round that asks a SINGLE question with the stored password,
/// whatever that question is, and in a round that asks several it answers only the
/// prompts that read as a password request, leaving the rest empty and recording the
/// first of them here. So this observation is made on the multi-prompt shape only: a
/// server whose sole question is a verification code receives the stored password as
/// the answer to it and nothing is recorded, which is why this class cannot be read as
/// evidence that a second factor was never answered wrongly.
/// </remarks>
public sealed class KeyboardInteractiveObservation
{
    private string? _unansweredPrompt;

    /// <summary>0 while the stored password has not been given yet, 1 once it has.</summary>
    private int _passwordAnswered;

    /// <summary>The first prompt left unanswered, trimmed, or null when every prompt was answered.</summary>
    public string? UnansweredPrompt => Volatile.Read(ref _unansweredPrompt);

    /// <summary>Records a prompt the exchange could not answer; only the first one is kept.</summary>
    public void RecordUnanswered(string? prompt)
    {
        string text = string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt.Trim();
        Interlocked.CompareExchange(ref _unansweredPrompt, text, null);
    }

    /// <summary>
    /// Claims the one chance this attempt has to answer with the stored password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True the first time and false afterwards, for the whole connection attempt. A server that
    /// authenticates in stages asks twice: the password, then a verification code, each as its
    /// own single-prompt round. The second round is indistinguishable from the first by wording
    /// alone, so without this the stored password was sent as the answer to the code prompt -
    /// which fails, and which the server records as a failed second factor.
    /// </para>
    /// <para>
    /// Refusing the second round is what lets the refusal be classified as an unanswered
    /// question rather than a rejected password, and that classification is what routes the host
    /// to the interactive client that CAN answer it. The two halves only work together: refusing
    /// without the routing would take a working route away from these hosts.
    /// </para>
    /// </remarks>
    public bool TryTakePasswordAnswer() =>
        Interlocked.CompareExchange(ref _passwordAnswered, 1, 0) == 0;

    /// <summary>Clears the record before a new attempt.</summary>
    public void Reset()
    {
        Volatile.Write(ref _unansweredPrompt, null);
        Volatile.Write(ref _passwordAnswered, 0);
    }
}
