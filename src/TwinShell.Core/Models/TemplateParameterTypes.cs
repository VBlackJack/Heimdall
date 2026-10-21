/*
 * Copyright 2026 Julien Bombled
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
/// The parameter type names a command template may declare: one list shared by the
/// generator that validates them, the editor that offers them and the sync schema that
/// admits them.
/// </summary>
/// <remarks>
/// The sync schema used to carry its own list (string, integer, boolean, path, hostname,
/// ipaddress), so a template with a choice, a number or a drive letter, or with the "int"
/// and "bool" the editor writes, was refused whole on sync import.
/// </remarks>
public static class TemplateParameterTypes
{
    public const string Text = "string";
    public const string Int = "int";
    public const string Integer = "integer";
    public const string Number = "number";
    public const string Bool = "bool";
    public const string Boolean = "boolean";
    public const string Hostname = "hostname";
    public const string IpAddress = "ipaddress";
    public const string DriveLetter = "driveletter";
    public const string Path = "path";
    public const string Choice = TemplateParameter.ChoiceTypeName;

    /// <summary>The types the action editor offers, in the order it lists them.</summary>
    public static IReadOnlyList<string> Offered { get; } =
        [Text, Int, Bool, Hostname, IpAddress, Path, Choice];

    /// <summary>
    /// Every type the generator understands, the legacy spellings "integer" and "boolean"
    /// included.
    /// </summary>
    public static IReadOnlyList<string> Accepted { get; } =
        [Text, Int, Integer, Number, Bool, Boolean, Hostname, IpAddress, DriveLetter, Path, Choice];
}
