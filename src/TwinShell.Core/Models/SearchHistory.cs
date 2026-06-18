/*
 * Copyright 2025 Julien Bombled
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

namespace TwinShell.Core.Models;

/// <summary>
/// Represents a search history entry for autocomplete and suggestions.
/// Stores recent searches to improve user experience.
/// </summary>
public sealed class SearchHistory
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The search term that was used
    /// </summary>
    public required string SearchTerm { get; set; }

    /// <summary>
    /// Normalized version of the search term (for deduplication)
    /// </summary>
    public string NormalizedSearchTerm { get; set; } = string.Empty;

    /// <summary>
    /// Number of times this search was performed
    /// </summary>
    public int SearchCount { get; set; } = 1;

    /// <summary>
    /// Number of results found for this search
    /// </summary>
    public int ResultCount { get; set; }

    /// <summary>
    /// Last time this search was performed
    /// </summary>
    public DateTime LastSearchedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// First time this search was performed
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this search was successful (found at least one result)
    /// </summary>
    public bool WasSuccessful { get; set; }

    /// <summary>
    /// Optional user ID (for multi-user scenarios)
    /// </summary>
    public string? UserId { get; set; }
}
