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

namespace Heimdall.App.Services.Import;

/// <summary>
/// Outcome categories for a Command Library import operation.
/// </summary>
public enum CommandLibraryImportOutcome
{
    /// <summary>The file was parsed and the actions were merged.</summary>
    Success,

    /// <summary>The file exceeded the maximum accepted size.</summary>
    FileTooLarge,

    /// <summary>The file could not be parsed into a known action format.</summary>
    InvalidFormat
}

/// <summary>
/// Typed result of a Command Library import, carrying the outcome and the
/// per-category counts of merged actions.
/// </summary>
public sealed class CommandLibraryImportResult
{
    /// <summary>The high-level outcome of the import.</summary>
    public CommandLibraryImportOutcome Outcome { get; init; }

    /// <summary>Number of actions created.</summary>
    public int Imported { get; init; }

    /// <summary>Number of existing user-created actions updated.</summary>
    public int Updated { get; init; }

    /// <summary>Number of actions skipped (invalid or system-owned).</summary>
    public int Skipped { get; init; }

    /// <summary>Creates a file-too-large result.</summary>
    public static CommandLibraryImportResult FileTooLarge() =>
        new() { Outcome = CommandLibraryImportOutcome.FileTooLarge };

    /// <summary>Creates an invalid-format result.</summary>
    public static CommandLibraryImportResult InvalidFormat() =>
        new() { Outcome = CommandLibraryImportOutcome.InvalidFormat };

    /// <summary>Creates a success result with the given merge counts.</summary>
    public static CommandLibraryImportResult Success(int imported, int updated, int skipped) =>
        new()
        {
            Outcome = CommandLibraryImportOutcome.Success,
            Imported = imported,
            Updated = updated,
            Skipped = skipped
        };
}
