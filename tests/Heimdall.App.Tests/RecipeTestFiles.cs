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

using System.Text;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>Recipe files and recipes for the tests of the recipe store and the registry overlay.</summary>
internal static class RecipeTestFiles
{
    /// <summary>The absolute path a test stores as the resolved program; the file is never touched.</summary>
    public const string ResolvedProgram = @"C:\Windows\System32\ping.exe";

    /// <summary>The bytes of a minimal valid recipe file.</summary>
    public static byte[] Bytes(string id = "acme.ping", string name = "Ping", string description = "Ping the selected host.", string? icon = null)
    {
        string iconLine = icon is null ? string.Empty : $",\n  \"icon\": \"{icon}\"";
        string json = $$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "version": "1.0.0",
              "author": "Acme",
              "name": "{{name}}",
              "description": "{{description}}",
              "program": "ping.exe",
              "arguments": ["{Host}"]{{iconLine}}
            }
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>A parsed recipe built from <see cref="Bytes"/>.</summary>
    public static Recipe Recipe(string id = "acme.ping", string name = "Ping", string? icon = null)
    {
        RecipeParseResult parsed = RecipeParser.Parse(Bytes(id, name, icon: icon));
        return parsed.Recipe ?? throw new InvalidOperationException("The test recipe is not valid.");
    }
}
