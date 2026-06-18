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
/// DTO for user configuration export/import
/// </summary>
public sealed class UserConfigurationDto
{
    /// <summary>
    /// Version of the configuration format
    /// </summary>
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Export date and time
    /// </summary>
    public DateTime ExportDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User identifier (optional)
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Exported favorites
    /// </summary>
    public List<FavoriteDto> Favorites { get; set; } = new();

    /// <summary>
    /// Exported command history
    /// </summary>
    public List<CommandHistoryDto> History { get; set; } = new();

    /// <summary>
    /// User preferences
    /// </summary>
    public Dictionary<string, string> Preferences { get; set; } = new();
}

/// <summary>
/// Favorite DTO for export
/// </summary>
public sealed class FavoriteDto
{
    public string ActionId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int DisplayOrder { get; set; }
}

/// <summary>
/// Command history DTO for export
/// </summary>
public sealed class CommandHistoryDto
{
    public string ActionId { get; set; } = string.Empty;
    public string GeneratedCommand { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string Platform { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Category { get; set; } = string.Empty;
    public string ActionTitle { get; set; } = string.Empty;
}
