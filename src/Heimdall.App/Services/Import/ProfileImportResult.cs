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
/// Result returned by the profile import workflow after preview and conflict resolution.
/// </summary>
public sealed class ProfileImportResult
{
    public bool IsFailure { get; init; }

    public bool IsCancelled { get; init; }

    public bool HasChanges { get; init; }

    public string? ErrorMessage { get; init; }

    public string? UserMessage { get; init; }

    public int ImportedCount { get; init; }

    public int ReplacedCount { get; init; }

    public int RenamedCount { get; init; }

    public int SkippedCount { get; init; }

    public int PasswordsIgnoredCount { get; init; }

    public int GatewayCreatedCount { get; init; }

    public int GatewayMergedCount { get; init; }

    public int GatewayOrphanCount { get; init; }

    /// <summary>
    /// How many imported profiles will not use the external credential provider until the user
    /// confirms them. Zero when the provider is off.
    /// </summary>
    public int UnconfirmedLookupCount { get; init; }

    public static ProfileImportResult NoChanges() => new();

    public static ProfileImportResult Cancelled() => new()
    {
        IsCancelled = true
    };

    public static ProfileImportResult Failure(string message) => new()
    {
        IsFailure = true,
        ErrorMessage = message
    };
}
