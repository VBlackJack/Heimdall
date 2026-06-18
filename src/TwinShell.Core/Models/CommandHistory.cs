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
/// Represents a command history entry
/// </summary>
public sealed class CommandHistory
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User identifier (nullable for MVP - single user mode)
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Associated action ID
    /// </summary>
    public string ActionId { get; set; } = string.Empty;

    /// <summary>
    /// Associated action (navigation property)
    /// </summary>
    public Action? Action { get; set; }

    /// <summary>
    /// Generated command text
    /// </summary>
    public string GeneratedCommand { get; set; } = string.Empty;

    /// <summary>
    /// Parameters used to generate the command (stored as dictionary)
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>
    /// Platform used for command generation
    /// </summary>
    public Platform Platform { get; set; }

    /// <summary>
    /// Timestamp when the command was generated
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Category of the action (denormalized for faster filtering)
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Action title (denormalized for display without loading Action)
    /// </summary>
    public string ActionTitle { get; set; } = string.Empty;

    /// <summary>
    /// Whether this command was executed (vs just copied)
    /// </summary>
    public bool IsExecuted { get; set; }

    /// <summary>
    /// Exit code from execution (null if not executed)
    /// </summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// Execution duration (null if not executed)
    /// </summary>
    public TimeSpan? ExecutionDuration { get; set; }

    /// <summary>
    /// Whether the execution was successful (null if not executed)
    /// </summary>
    public bool? ExecutionSuccess { get; set; }
}
