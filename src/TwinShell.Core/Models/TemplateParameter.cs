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

    /// <summary>
    /// The values a <c>choice</c> parameter accepts, in the order they should be offered.
    /// </summary>
    /// <remarks>
    /// Null or empty for every other type. A choice with no values left to offer behaves
    /// like a free-text parameter rather than refusing everything: an action that arrived
    /// that way is worth less, not worth nothing.
    /// </remarks>
    public List<string>? AllowedValues { get; set; }

    /// <summary>
    /// True when this parameter offers a fixed set of values rather than free text.
    /// </summary>
    /// <remarks>
    /// One predicate for the question, because four places ask it: the generator when it
    /// validates, the editor when it decides what to show, the panel when it renders a
    /// list instead of a box, and the import door when it checks what arrived.
    /// </remarks>
    public bool IsChoice =>
        string.Equals(Type, ChoiceTypeName, StringComparison.OrdinalIgnoreCase)
        && AllowedValues is { Count: > 0 };

    /// <summary>The type name identifying a fixed-set parameter.</summary>
    public const string ChoiceTypeName = "choice";

    /// <summary>
    /// Optional quoting behavior override for command generation.
    /// </summary>
    public QuotingMode? Quoting { get; set; }
}
