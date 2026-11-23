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

using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>Shared builders for the recipe tests.</summary>
internal static class RecipeTestData
{
    /// <summary>The smallest valid recipe file.</summary>
    public const string MinimalJson = """
        {
          "schemaVersion": 1,
          "id": "acme.ping",
          "version": "1.0.0",
          "author": "Acme",
          "name": "Ping",
          "description": "Ping the selected host.",
          "program": "ping.exe",
          "arguments": ["{Host}"]
        }
        """;

    /// <summary>A recipe file that uses every property of the format.</summary>
    public const string FullJson = """
        {
          "schemaVersion": 1,
          "id": "acme.scan",
          "version": "1.2.3-beta.1+build.5",
          "author": "Acme Labs",
          "name": "Scan",
          "description": "Scans a target.",
          "labels": {
            "fr": {
              "name": "Analyse",
              "description": "Analyse une cible.",
              "fields": { "target": "Cible" }
            }
          },
          "program": "C:\\Windows\\System32\\ping.exe",
          "arguments": [
            ["-n", "{in:count}"],
            "{in:verbose}",
            "--mode={in:mode}",
            ["--extra", "{in:extra}"],
            "{in:target}",
            ["-p", "{Port}"]
          ],
          "inputs": [
            { "name": "target", "type": "hostname", "label": "Target", "required": true },
            { "name": "count", "type": "int", "label": "Count", "default": 4 },
            { "name": "verbose", "type": "bool", "label": "Verbose", "default": false, "whenTrue": ["-v", "-d"] },
            { "name": "mode", "type": "choice", "label": "Mode", "choices": ["fast", "slow"], "default": "fast" },
            { "name": "extra", "type": "string", "label": "Extra", "pattern": "[a-z]+", "maxLength": 20 },
            { "name": "dir", "type": "path", "label": "Folder" },
            { "name": "ratio", "type": "number", "label": "Ratio" },
            { "name": "addr", "type": "ipaddress", "label": "Address" }
          ],
          "output": "csv",
          "outputEncoding": "oem",
          "stderr": "merge",
          "successExitCodes": [0, 1],
          "timeoutSeconds": 30,
          "icon": "network"
        }
        """;

    /// <summary>A valid recipe built directly, to be modified with <c>with</c> in a test.</summary>
    public static Recipe Minimal() => new(
        SchemaVersion: 1,
        Id: "acme.ping",
        Version: "1.0.0",
        Author: "Acme",
        Name: "Ping",
        Description: "Ping the selected host.",
        Labels: new Dictionary<string, RecipeLabels>(),
        Program: "ping.exe",
        Arguments: [RecipeArgument.Bare("{Host}")],
        Inputs: [],
        Output: RecipeOutputFormat.Text,
        OutputEncoding: RecipeOutputEncoding.Utf8,
        Stderr: RecipeStderrMode.Separate,
        SuccessExitCodes: [0],
        TimeoutSeconds: 60,
        Icon: null);

    /// <summary>A valid input of the given type, to be modified with <c>with</c>.</summary>
    public static RecipeInput Input(string name, RecipeInputType type, bool required = false, string? defaultValue = null)
        => new(name, type, "Label " + name, required, defaultValue, null, null, null, null);

    /// <summary>A recipe whose inputs and arguments are the given ones.</summary>
    public static Recipe With(IEnumerable<RecipeInput> inputs, params RecipeArgument[] arguments)
        => Minimal() with { Inputs = [.. inputs], Arguments = [.. arguments] };

    /// <summary>Parses a file that must be valid.</summary>
    public static Recipe ParseValid(string json)
    {
        RecipeParseResult result = RecipeParser.Parse(json);
        Assert.True(result.Succeeded, "Unexpected errors: " + string.Join(", ", result.Errors.Select(error => error.Code + "@" + error.Path)));
        return result.Recipe!;
    }

    /// <summary>The error codes of a file that must be refused.</summary>
    public static IReadOnlyList<RecipeError> ParseInvalid(string json)
    {
        RecipeParseResult result = RecipeParser.Parse(json);
        Assert.False(result.Succeeded);
        Assert.Null(result.Recipe);
        Assert.NotEmpty(result.Errors);
        return result.Errors;
    }

    /// <summary>The codes found in a recipe.</summary>
    public static IReadOnlyList<RecipeErrorCode> Codes(Recipe recipe)
        => [.. RecipeValidator.Validate(recipe).Select(error => error.Code)];
}
