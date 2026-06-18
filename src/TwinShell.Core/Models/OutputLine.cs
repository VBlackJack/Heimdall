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
/// Represents a single line of output from a command execution
/// </summary>
public sealed class OutputLine
{
    /// <summary>
    /// The output text
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Whether this line is from stderr (vs stdout)
    /// </summary>
    public bool IsError { get; set; }

    /// <summary>
    /// Timestamp when the line was received
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
