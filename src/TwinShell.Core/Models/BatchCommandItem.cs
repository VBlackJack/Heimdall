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
/// Represents a single command within a batch
/// </summary>
public sealed class BatchCommandItem
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Parent batch ID
    /// </summary>
    public string BatchId { get; set; } = string.Empty;

    /// <summary>
    /// Order within the batch (0-based)
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Action ID (reference to the Action being executed)
    /// </summary>
    public string? ActionId { get; set; }

    /// <summary>
    /// Action title (denormalized for display)
    /// </summary>
    public string ActionTitle { get; set; } = string.Empty;

    /// <summary>
    /// The actual command to execute
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Platform for this command
    /// </summary>
    public Platform Platform { get; set; }

    /// <summary>
    /// Optional description for this command in the batch
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether this command has been executed
    /// </summary>
    public bool IsExecuted { get; set; }

    /// <summary>
    /// Execution result for this command (null if not yet executed)
    /// </summary>
    public ExecutionResult? ExecutionResult { get; set; }
}
