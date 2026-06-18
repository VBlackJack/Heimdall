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

namespace TwinShell.Persistence.Entities;

/// <summary>
/// Database entity for CommandBatch
/// </summary>
public sealed class CommandBatchEntity
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Universal unique identifier for GitOps synchronization.
    /// Used as the stable identifier across different environments.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public BatchExecutionMode ExecutionMode { get; set; }

    /// <summary>
    /// Commands stored as JSON array
    /// </summary>
    public string CommandsJson { get; set; } = "[]";

    /// <summary>
    /// Tags stored as JSON array
    /// </summary>
    public string TagsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastExecutedAt { get; set; }
    public bool IsUserCreated { get; set; }
}
