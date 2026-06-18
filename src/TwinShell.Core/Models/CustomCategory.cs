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
/// Represents a custom user-defined category for organizing actions.
/// </summary>
public sealed class CustomCategory
{
    /// <summary>
    /// Unique identifier for the custom category.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Universal unique identifier for GitOps synchronization
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Display name of the category.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Icon key for displaying the category (e.g., "folder", "star", "tools").
    /// </summary>
    public string IconKey { get; set; } = "folder";

    /// <summary>
    /// Color hex code for the category (e.g., "#2196F3").
    /// </summary>
    public string ColorHex { get; set; } = "#2196F3";

    /// <summary>
    /// Indicates if this is a system-defined category (cannot be deleted).
    /// </summary>
    public bool IsSystemCategory { get; set; } = false;

    /// <summary>
    /// Display order for sorting categories (lower values appear first).
    /// </summary>
    public int DisplayOrder { get; set; } = 0;

    /// <summary>
    /// Indicates if the category is hidden from the navigation.
    /// </summary>
    public bool IsHidden { get; set; } = false;

    /// <summary>
    /// Optional description of the category.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Date and time when the category was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date and time when the category was last modified.
    /// </summary>
    public DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// Collection of action IDs assigned to this category.
    /// </summary>
    public List<string> ActionIds { get; set; } = new();
}
