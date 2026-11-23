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

/// <summary>
/// The small recipe a person can place by hand to see the recipe view on screen: a System32
/// console tool, one text field, text output. Kept as a test so that it cannot rot into a file
/// the parser refuses.
/// </summary>
public sealed class RecipeOnScreenSampleTests
{
    private const string Sample = """
        {
          "schemaVersion": 1,
          "id": "sample.ping-once",
          "version": "1.0.0",
          "author": "Sample",
          "name": "Ping once",
          "description": "Sends one echo request to the address you type.",
          "program": "ping.exe",
          "arguments": ["-n", "1", "{in:target}"],
          "inputs": [
            { "name": "target", "type": "string", "label": "Address to ping", "required": true, "default": "127.0.0.1" }
          ],
          "output": "text",
          "outputEncoding": "oem",
          "timeoutSeconds": 20
        }
        """;

    [Fact]
    public void TheSampleIsAValidRecipeWithOneTextFieldAndTextOutput()
    {
        Recipe recipe = RecipeTestData.ParseValid(Sample);

        Assert.Equal("sample.ping-once", recipe.Id);
        RecipeInput input = Assert.Single(recipe.Inputs);
        Assert.Equal(RecipeInputType.String, input.Type);
        Assert.Equal(RecipeOutputFormat.Text, recipe.Output);
        Assert.False(RecipeSessionUse.Of(recipe).UsesSession);
    }

    [Fact]
    public void TheSampleExpandsToOneArgumentPerToken()
    {
        Recipe recipe = RecipeTestData.ParseValid(Sample);

        RecipeExpansion expansion = RecipeExpander.Expand(recipe, new Dictionary<string, string?> { ["target"] = "127.0.0.1" });

        Assert.True(expansion.Succeeded);
        Assert.Equal(["-n", "1", "127.0.0.1"], expansion.Arguments);
    }
}
