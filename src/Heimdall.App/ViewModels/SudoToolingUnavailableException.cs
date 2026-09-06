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

namespace Heimdall.App.ViewModels;

/// <summary>
/// A privileged transfer was refused by the server-side script because a tool it needs is
/// missing, or the coreutils are not GNU.
/// </summary>
internal sealed class SudoToolingUnavailableException : Exception
{
    public SudoToolingUnavailableException(string? remoteDiagnostic)
        : base("The server lacks a tool the privileged transfer needs.")
    {
        RemoteDiagnostic = remoteDiagnostic ?? string.Empty;
    }

    /// <summary>What the script wrote on stderr, naming the tool. Logged, never shown verbatim.</summary>
    public string RemoteDiagnostic { get; }
}
