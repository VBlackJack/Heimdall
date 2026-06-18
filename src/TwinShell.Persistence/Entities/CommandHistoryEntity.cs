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
/// Database entity for CommandHistory
/// </summary>
public sealed class CommandHistoryEntity
{
    public string Id { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string ActionId { get; set; } = string.Empty;
    public ActionEntity? Action { get; set; }
    public string GeneratedCommand { get; set; } = string.Empty;

    /// <summary>
    /// Parameters stored as JSON
    /// </summary>
    public string ParametersJson { get; set; } = "{}";

    public Platform Platform { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Category { get; set; } = string.Empty;
    public string ActionTitle { get; set; } = string.Empty;

    // Sprint 4: Execution tracking fields
    public bool IsExecuted { get; set; }
    public int? ExitCode { get; set; }
    public long? ExecutionDurationTicks { get; set; } // Store as ticks for EF Core compatibility
    public bool? ExecutionSuccess { get; set; }
}
