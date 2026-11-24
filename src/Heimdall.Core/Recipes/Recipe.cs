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

namespace Heimdall.Core.Recipes;

/// <summary>The type of an input field of a recipe.</summary>
public enum RecipeInputType
{
    /// <summary>Free text, optionally bounded by a pattern and a length.</summary>
    String,

    /// <summary>A 32-bit integer.</summary>
    Int,

    /// <summary>A finite decimal number, written with the invariant culture.</summary>
    Number,

    /// <summary>A switch; it contributes its <c>whenTrue</c> tokens when on.</summary>
    Bool,

    /// <summary>A DNS host name.</summary>
    Hostname,

    /// <summary>An IPv4 or IPv6 address.</summary>
    IpAddress,

    /// <summary>A local file or folder path (no network or device forms).</summary>
    Path,

    /// <summary>One value of a closed list.</summary>
    Choice,
}

/// <summary>How the output of the program is presented.</summary>
public enum RecipeOutputFormat
{
    /// <summary>Plain text.</summary>
    Text,

    /// <summary>Comma separated values shown as a grid.</summary>
    Csv,

    /// <summary>JSON shown as a grid.</summary>
    Json,
}

/// <summary>The encoding the program writes its output in.</summary>
public enum RecipeOutputEncoding
{
    /// <summary>UTF-8.</summary>
    Utf8,

    /// <summary>The OEM code page of the console.</summary>
    Oem,
}

/// <summary>What happens to the standard error stream.</summary>
public enum RecipeStderrMode
{
    /// <summary>Kept apart from the output.</summary>
    Separate,

    /// <summary>Interleaved with the output.</summary>
    Merge,
}

/// <summary>
/// One entry of the argument list: a bare token, or a group of tokens that is kept or dropped as a whole.
/// </summary>
/// <param name="Tokens">The tokens, exactly as written in the file (one token is one process argument).</param>
/// <param name="IsGroup">True for a group, false for a bare token (which then holds exactly one token).</param>
public sealed record RecipeArgument(IReadOnlyList<string> Tokens, bool IsGroup)
{
    /// <summary>Builds a bare-token entry.</summary>
    public static RecipeArgument Bare(string token) => new([token], false);

    /// <summary>Builds a group entry.</summary>
    public static RecipeArgument Group(IEnumerable<string> tokens) => new(Array.AsReadOnly(tokens.ToArray()), true);
}

/// <summary>One typed input field of a recipe.</summary>
/// <param name="Name">The field name used by <c>{in:name}</c>.</param>
/// <param name="Type">The field type.</param>
/// <param name="Label">The English label shown next to the field.</param>
/// <param name="Required">True when a value must be supplied (a default counts as supplied).</param>
/// <param name="Default">The default value as text (booleans as <c>true</c> or <c>false</c>), or null.</param>
/// <param name="Pattern">For a <c>string</c> input, a regular expression the whole value must match; otherwise null.</param>
/// <param name="MaxLength">For a <c>string</c> input, the longest accepted value; otherwise null.</param>
/// <param name="Choices">For a <c>choice</c> input, the closed list; null when the file declared none.</param>
/// <param name="WhenTrue">For a <c>bool</c> input, the tokens contributed when on; null when the file declared none.</param>
public sealed record RecipeInput(
    string Name,
    RecipeInputType Type,
    string Label,
    bool Required,
    string? Default,
    string? Pattern,
    int? MaxLength,
    IReadOnlyList<string>? Choices,
    IReadOnlyList<string>? WhenTrue)
{
    /// <summary>
    /// True when the field can be empty at run time: not required and without a non-empty default.
    /// A group that uses such a field is dropped while it is empty.
    /// </summary>
    public bool IsOptional => Type != RecipeInputType.Bool && !Required && string.IsNullOrWhiteSpace(Default);
}

/// <summary>The translated texts of one language of a recipe.</summary>
/// <param name="Name">The translated name, or null.</param>
/// <param name="Description">The translated description, or null.</param>
/// <param name="Fields">Translated input labels keyed by input name.</param>
public sealed record RecipeLabels(
    string? Name,
    string? Description,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>An immutable recipe: a tool described by data, run as one program with a list of arguments.</summary>
/// <param name="SchemaVersion">The format version the file was written for.</param>
/// <param name="Id">The namespaced identity, <c>author.name</c>, never renamed by Heimdall.</param>
/// <param name="Version">The semantic version, for display and replace comparison only.</param>
/// <param name="Author">The author, display only and always shown as unverified.</param>
/// <param name="Name">The English name.</param>
/// <param name="Description">The English description.</param>
/// <param name="Labels">Optional translations keyed by language tag.</param>
/// <param name="Program">The program exactly as written in the file; resolving it is a later step.</param>
/// <param name="Arguments">The argument entries in order.</param>
/// <param name="Inputs">The input fields in order.</param>
/// <param name="Output">How the output is presented.</param>
/// <param name="OutputEncoding">The encoding of the output.</param>
/// <param name="Stderr">What happens to standard error.</param>
/// <param name="SuccessExitCodes">The exit codes that mean success.</param>
/// <param name="TimeoutSeconds">The run timeout in seconds.</param>
/// <param name="Icon">A key of <see cref="RecipeIcons.Keys"/>, or null for the default icon.</param>
public sealed record Recipe(
    int SchemaVersion,
    string Id,
    string Version,
    string Author,
    string Name,
    string Description,
    IReadOnlyDictionary<string, RecipeLabels> Labels,
    string Program,
    IReadOnlyList<RecipeArgument> Arguments,
    IReadOnlyList<RecipeInput> Inputs,
    RecipeOutputFormat Output,
    RecipeOutputEncoding OutputEncoding,
    RecipeStderrMode Stderr,
    IReadOnlyList<int> SuccessExitCodes,
    int TimeoutSeconds,
    string? Icon)
{
    /// <summary>The input with the given name, or null.</summary>
    public RecipeInput? FindInput(string name)
    {
        foreach (RecipeInput input in Inputs)
        {
            if (string.Equals(input.Name, name, StringComparison.Ordinal))
                return input;
        }

        return null;
    }
}
