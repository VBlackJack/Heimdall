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
/// Represents the result of a command execution
/// </summary>
public sealed class ExecutionResult
{
    /// <summary>
    /// Whether the execution completed successfully (ExitCode == 0)
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Process exit code (0 = success, non-zero = error)
    /// </summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// Standard output stream content
    /// </summary>
    public string Stdout { get; set; } = string.Empty;

    /// <summary>
    /// Standard error stream content
    /// </summary>
    public string Stderr { get; set; } = string.Empty;

    /// <summary>
    /// Total execution duration
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Timestamp when execution started
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Error message if execution failed to start or was cancelled
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Whether the execution was cancelled by user
    /// </summary>
    public bool WasCancelled { get; set; }

    /// <summary>
    /// Whether the execution timed out
    /// </summary>
    public bool TimedOut { get; set; }
}
