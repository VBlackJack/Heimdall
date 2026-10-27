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

namespace Heimdall.Core.Import;

/// <summary>
/// Describes the result of importing a selected set of sessions.
/// </summary>
/// <param name="ImportedCount">Number of new sessions added.</param>
/// <param name="SkippedDuplicates">Number of entries skipped because they duplicate an existing alias.</param>
/// <param name="SkippedInvalid">Number of entries skipped because they are structurally invalid.</param>
/// <param name="WarningCount">Number of warnings surfaced during import.</param>
public sealed record ImportOutcome(
    int ImportedCount,
    int SkippedDuplicates,
    int SkippedInvalid = 0,
    int WarningCount = 0)
{
    /// <summary>What the import boundary took away, profile by profile, for the summary to name.</summary>
    public Configuration.ImportedProfileSanitizationReport Sanitization { get; init; } =
        Configuration.ImportedProfileSanitizationReport.Empty;
}
