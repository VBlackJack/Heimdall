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

using System.Text.Json.Serialization;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;

namespace TwinShell.Core.Models;

/// <summary>
/// Represents a system administration action
/// </summary>
public sealed class Action
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Universal unique identifier for GitOps synchronization
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Action title
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Category (AD, DNS, GPO, Logs, Linux Services, etc.)
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Supported platform(s)
    /// </summary>
    public Platform Platform { get; set; }

    /// <summary>
    /// Criticality level
    /// </summary>
    public CriticalityLevel Level { get; set; }

    /// <summary>
    /// Tags for search and filtering
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Windows command template ID (if applicable)
    /// </summary>
    public string? WindowsCommandTemplateId { get; set; }

    /// <summary>
    /// Windows command template (navigation property)
    /// </summary>
    public CommandTemplate? WindowsCommandTemplate { get; set; }

    /// <summary>
    /// Linux command template ID (if applicable)
    /// </summary>
    public string? LinuxCommandTemplateId { get; set; }

    /// <summary>
    /// Linux command template (navigation property)
    /// </summary>
    public CommandTemplate? LinuxCommandTemplate { get; set; }

    /// <summary>
    /// Command examples (legacy - used for single-platform actions)
    /// </summary>
    public List<CommandExample> Examples { get; set; } = new();

    /// <summary>
    /// Windows-specific command examples (for cross-platform actions)
    /// </summary>
    public List<CommandExample> WindowsExamples { get; set; } = new();

    /// <summary>
    /// Linux-specific command examples (for cross-platform actions)
    /// </summary>
    public List<CommandExample> LinuxExamples { get; set; } = new();

    /// <summary>
    /// Additional notes
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// External documentation links
    /// </summary>
    public List<ExternalLink> Links { get; set; } = new();

    /// <summary>
    /// Creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this action was created by a user (vs. seeded)
    /// </summary>
    public bool IsUserCreated { get; set; }

    /// <summary>
    /// Whether this shipped action left the seed but was kept because history or a favorite
    /// still points at it.
    /// </summary>
    /// <remarks>
    /// A retired action is hidden from the library, its search and the palette, and stays
    /// readable by id so the history rows that name it keep their action. A later seed that
    /// ships the id again brings it back. Local state of this database: never read from a
    /// seed or a library file, never written to one.
    /// </remarks>
    [JsonIgnore]
    public bool IsRetired { get; set; }

    /// <summary>
    /// Other languages of the pivot text, one entry per culture.
    /// </summary>
    /// <remarks>
    /// Serialized as an object keyed by two-letter culture code, the shape the shipped seeds
    /// carry under <c>translations</c>, rather than as the list this is in memory. The text
    /// fields above are the pivot (English for the shipped catalogue); a translation replaces
    /// them only at presentation, field by field.
    /// </remarks>
    [JsonConverter(typeof(ActionTranslationsJsonConverter))]
    public List<ActionTranslation> Translations { get; set; } = new();
}
