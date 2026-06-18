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
/// Represents real-time progress of a batch execution
/// </summary>
public sealed class BatchExecutionProgress
{
    /// <summary>
    /// Current command being executed (0-based index)
    /// </summary>
    public int CurrentCommandIndex { get; set; }

    /// <summary>
    /// Total number of commands in the batch
    /// </summary>
    public int TotalCommands { get; set; }

    /// <summary>
    /// Current command being executed
    /// </summary>
    public BatchCommandItem? CurrentCommand { get; set; }

    /// <summary>
    /// Number of commands completed
    /// </summary>
    public int CompletedCount { get; set; }

    /// <summary>
    /// Number of commands that succeeded
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of commands that failed
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Progress percentage (0-100)
    /// </summary>
    public double ProgressPercentage => TotalCommands > 0 ? (CompletedCount / (double)TotalCommands) * 100 : 0;

    /// <summary>
    /// Whether the batch is currently running
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Error message if current command failed
    /// </summary>
    public string? ErrorMessage { get; set; }
}
