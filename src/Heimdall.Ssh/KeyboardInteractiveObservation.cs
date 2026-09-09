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
/// Records only whether input was supplied, never its value. Legacy callers can still map
/// an ambiguous first single-question round to the password, so this observation alone does
/// not prove that every authentication question was answered with the intended secret.
/// </remarks>
public sealed class KeyboardInteractiveObservation
{
    private string? _unansweredPrompt;

    /// <summary>0 while the stored password has not been given yet, 1 once it has.</summary>
    private int _passwordAnswered;

    private int _interactiveAnswers;

    /// <summary>Whether a user supplied an answer during this attempt.</summary>
    public bool HasInteractiveAnswer => Volatile.Read(ref _interactiveAnswers) != 0;

    /// <summary>Records an answer without retaining its text.</summary>
    public void RecordInteractiveAnswer() => Interlocked.Increment(ref _interactiveAnswers);

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
    public bool TryTakePasswordAnswer() =>
        Interlocked.CompareExchange(ref _passwordAnswered, 1, 0) == 0;

    /// <summary>Clears the record before a new attempt.</summary>
    public void Reset()
    {
        Volatile.Write(ref _unansweredPrompt, null);
        Volatile.Write(ref _passwordAnswered, 0);
        Volatile.Write(ref _interactiveAnswers, 0);
    }
}
