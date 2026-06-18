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

namespace TwinShell.Persistence.Entities;

/// <summary>
/// Database entity for custom user-defined categories.
/// </summary>
public sealed class CustomCategoryEntity
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Universal unique identifier for GitOps synchronization.
    /// Used as the stable identifier across different environments.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string IconKey { get; set; } = "folder";
    public string ColorHex { get; set; } = "#2196F3";
    public bool IsSystemCategory { get; set; } = false;
    public int DisplayOrder { get; set; } = 0;
    public bool IsHidden { get; set; } = false;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }

    // Navigation property for many-to-many relationship
    public ICollection<ActionCategoryMappingEntity> ActionMappings { get; set; } = new List<ActionCategoryMappingEntity>();
}
