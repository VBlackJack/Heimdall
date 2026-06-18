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

using TwinShell.Core.Enums;

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
}
