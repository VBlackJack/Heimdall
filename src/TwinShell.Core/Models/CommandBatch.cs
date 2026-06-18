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
/// Represents a batch of commands to be executed sequentially
/// </summary>
public sealed class CommandBatch
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
    /// Batch name/title
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this batch does
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Execution mode (stop on error vs continue on error)
    /// </summary>
    public BatchExecutionMode ExecutionMode { get; set; } = BatchExecutionMode.StopOnError;

    /// <summary>
    /// Commands in this batch (ordered)
    /// </summary>
    public List<BatchCommandItem> Commands { get; set; } = new();

    /// <summary>
    /// Creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last execution timestamp (null if never executed)
    /// </summary>
    public DateTime? LastExecutedAt { get; set; }

    /// <summary>
    /// Whether this batch was created by a user (vs. seeded)
    /// </summary>
    public bool IsUserCreated { get; set; } = true;

    /// <summary>
    /// Tags for search and organization
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Number of commands in this batch
    /// </summary>
    public int CommandCount => Commands.Count;

    /// <summary>
    /// Whether all commands have been executed
    /// </summary>
    public bool IsFullyExecuted => Commands.All(c => c.IsExecuted);

    /// <summary>
    /// Number of commands that succeeded
    /// </summary>
    public int SuccessCount => Commands.Count(c => c.ExecutionResult?.Success == true);

    /// <summary>
    /// Number of commands that failed
    /// </summary>
    public int FailureCount => Commands.Count(c => c.IsExecuted && c.ExecutionResult?.Success == false);
}
