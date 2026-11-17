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

using Heimdall.Sftp;

namespace Heimdall.App.ViewModels;

/// <summary>
/// A privileged operation was refused by the server-side script for this one entry: it is
/// a symbolic link or a special file, or it is no longer the entry that was listed.
/// </summary>
/// <remarks>
/// Not a failure of sudo, so it never stops a batch: the next entry may well be an ordinary file.
/// </remarks>
internal sealed class SudoEntryRefusedException : Exception
{
    public SudoEntryRefusedException(EntryRefusalReason reason, string? remoteDiagnostic, string? remotePath = null)
        : base("The privileged operation was refused for this entry.")
    {
        Reason = reason;
        RemoteDiagnostic = remoteDiagnostic ?? string.Empty;
        RemotePath = remotePath ?? string.Empty;
    }

    /// <summary>The path of the entry the script was given, when the caller knew it; empty otherwise.</summary>
    public string RemotePath { get; }

    /// <summary>Why the entry was refused: what the message shown to the user is chosen from.</summary>
    public EntryRefusalReason Reason { get; }

    /// <summary>What the script wrote on stderr, naming the reason. Logged, never shown verbatim.</summary>
    public string RemoteDiagnostic { get; }
}
