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
/// Join table entity for many-to-many relationship between Actions and CustomCategories.
/// </summary>
public sealed class ActionCategoryMappingEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Foreign key to ActionEntity.
    /// </summary>
    public string ActionId { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to CustomCategoryEntity.
    /// </summary>
    public string CategoryId { get; set; } = string.Empty;

    /// <summary>
    /// Date and time when the action was added to the category.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ActionEntity Action { get; set; } = null!;
    public CustomCategoryEntity Category { get; set; } = null!;
}
