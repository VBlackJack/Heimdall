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

using Heimdall.Core.Configuration;
using Heimdall.Core.Import;

namespace Heimdall.App.Services.Import;

/// <summary>
/// One parsed .rdp file ready for preview.
/// </summary>
public sealed class RdpImportPreviewEntry
{
    public required string SourceFilePath { get; init; }

    public required string ProposedName { get; init; }

    public required ServerProfileDto Candidate { get; init; }

    public bool HasPasswordBlob { get; init; }

    public bool HasParseError { get; init; }

    public string? ParseErrorMessage { get; init; }

    public bool HasNameConflict { get; init; }

    public string? ConflictingExistingName { get; init; }

    public int UnknownKeyCount { get; init; }

    public IReadOnlyList<string> SkippedMappings { get; init; } = [];

    /// <summary>
    /// What the file said, kept so a Replace can write onto the existing profile exactly the
    /// fields the file carries and nothing else.
    /// </summary>
    internal RdpFileSchema? Schema { get; init; }
}
