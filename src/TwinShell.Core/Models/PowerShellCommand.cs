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
/// Represents a PowerShell cmdlet or function
/// </summary>
public sealed class PowerShellCommand
{
    /// <summary>
    /// Command name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Module name the command belongs to
    /// </summary>
    public string ModuleName { get; set; } = string.Empty;

    /// <summary>
    /// Command type (Cmdlet, Function, Alias, etc.)
    /// </summary>
    public string CommandType { get; set; } = string.Empty;

    /// <summary>
    /// Synopsis from Get-Help
    /// </summary>
    public string Synopsis { get; set; } = string.Empty;

    /// <summary>
    /// Description from Get-Help
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Syntax examples
    /// </summary>
    public List<string> Syntax { get; set; } = new();

    /// <summary>
    /// Parameters
    /// </summary>
    public List<PowerShellParameter> Parameters { get; set; } = new();

    /// <summary>
    /// Examples from Get-Help
    /// </summary>
    public List<string> Examples { get; set; } = new();
}

/// <summary>
/// Represents a PowerShell command parameter
/// </summary>
public sealed class PowerShellParameter
{
    /// <summary>
    /// Parameter name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Parameter type
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Whether the parameter is mandatory
    /// </summary>
    public bool IsMandatory { get; set; }

    /// <summary>
    /// Parameter description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Default value if any
    /// </summary>
    public string? DefaultValue { get; set; }
}
