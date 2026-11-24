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

/// <summary>
/// Builds the structural paths carried by errors (<c>arguments[2][0]</c>, <c>inputs[1].default</c>).
/// Names that come from the file are made safe first, so a path is always displayable.
/// </summary>
internal static class RecipePath
{
    public const string SchemaVersion = "schemaVersion";
    public const string Id = "id";
    public const string Version = "version";
    public const string Author = "author";
    public const string Name = "name";
    public const string Description = "description";
    public const string Labels = "labels";
    public const string Fields = "fields";
    public const string Program = "program";
    public const string Arguments = "arguments";
    public const string Inputs = "inputs";
    public const string Output = "output";
    public const string OutputEncoding = "outputEncoding";
    public const string Stderr = "stderr";
    public const string SuccessExitCodes = "successExitCodes";
    public const string TimeoutSeconds = "timeoutSeconds";
    public const string Icon = "icon";
    public const string Type = "type";
    public const string Label = "label";
    public const string Required = "required";
    public const string Default = "default";
    public const string Pattern = "pattern";
    public const string MaxLength = "maxLength";
    public const string Choices = "choices";
    public const string WhenTrue = "whenTrue";

    public static string Property(string parent, string name)
    {
        string safe = RecipeText.MakeSafe(name);
        return parent.Length == 0 ? safe : parent + "." + safe;
    }

    public static string Index(string parent, int index)
        => parent + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

    /// <summary>The path with its last segment removed; empty for a top-level path.</summary>
    public static string Parent(string path)
    {
        if (path.Length == 0)
            return path;

        int cut = path[^1] == ']' ? path.LastIndexOf('[') : path.LastIndexOf('.');
        return cut < 0 ? string.Empty : path[..cut];
    }
}
