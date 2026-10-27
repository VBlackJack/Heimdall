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

namespace Heimdall.App.Services.Import;

public sealed class RdpImportResult
{
    public int ImportedCount { get; init; }

    public int ReplacedCount { get; init; }

    public int RenamedCount { get; init; }

    public int SkippedCount { get; init; }

    public int PasswordsIgnoredCount { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>What the import boundary turned off on the profiles that were written.</summary>
    public ImportedProfileSanitizationReport Sanitization { get; init; } = ImportedProfileSanitizationReport.Empty;
}
