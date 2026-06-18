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
/// Represents a parameter in a command template
/// </summary>
public sealed class TemplateParameter
{
    /// <summary>
    /// Parameter name (used in template substitution, e.g., {targetHost})
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Display label for the UI
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Parameter type (string, int, bool, etc.)
    /// </summary>
    public string Type { get; set; } = "string";

    /// <summary>
    /// Default value for the parameter
    /// </summary>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Whether this parameter is required
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// Description/help text for the parameter
    /// </summary>
    public string? Description { get; set; }
}
