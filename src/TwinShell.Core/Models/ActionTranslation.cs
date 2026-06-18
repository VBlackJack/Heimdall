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
/// Represents a translation for an action
/// </summary>
public sealed class ActionTranslation
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Associated action ID
    /// </summary>
    public string ActionId { get; set; } = string.Empty;

    /// <summary>
    /// Culture code (e.g., "en", "es", "fr")
    /// </summary>
    public string CultureCode { get; set; } = string.Empty;

    /// <summary>
    /// Translated title
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Translated description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Translated notes (optional)
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Navigation property to action
    /// </summary>
    public Action? Action { get; set; }
}
