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
/// could not be answered. Heimdall answers a password prompt with the stored
/// password and nothing else; a server that goes on to ask for a verification code
/// or any other second factor gets an empty answer, and the refusal that follows
/// must be reported as that unanswered question rather than as a wrong password.
/// </summary>
public sealed class KeyboardInteractiveObservation
{
    private string? _unansweredPrompt;

    /// <summary>The first prompt left unanswered, trimmed, or null when every prompt was answered.</summary>
    public string? UnansweredPrompt => Volatile.Read(ref _unansweredPrompt);

    /// <summary>Records a prompt the exchange could not answer; only the first one is kept.</summary>
    public void RecordUnanswered(string? prompt)
    {
        string text = string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt.Trim();
        Interlocked.CompareExchange(ref _unansweredPrompt, text, null);
    }

    /// <summary>Clears the record before a new attempt.</summary>
    public void Reset() => Volatile.Write(ref _unansweredPrompt, null);
}
