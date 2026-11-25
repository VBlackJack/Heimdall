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

namespace Heimdall.Core.Tests.Recipes;

/// <summary>
/// A value the author fixed (a choice, a default) is held to the rule the run applies at the place
/// where its placeholder is used, so a recipe that validates cannot carry a value that is always refused.
/// </summary>
public sealed class RecipeFixedValueTests
{
    private static string Json(string argumentsJson, string inputsJson)
        => $$"""
            {
              "schemaVersion": 1,
              "id": "acme.probe",
              "version": "1.0.0",
              "author": "Acme",
              "name": "Probe",
              "description": "Probe.",
              "program": "ping.exe",
              "arguments": {{argumentsJson}},
              "inputs": {{inputsJson}}
            }
            """;

    private static RecipeParseResult Parse(string argumentsJson, string inputsJson)
        => RecipeParser.Parse(Encoding.UTF8.GetBytes(Json(argumentsJson, inputsJson)));

    private const string DashChoices =
        "[{\"name\":\"record\",\"type\":\"choice\",\"label\":\"Record\",\"required\":true,\"choices\":[\"-type=A\",\"-type=AAAA\"]}]";

    [Fact]
    public void ADashChoiceInAnInputThatStartsItsArgumentIsRefusedForEachValue()
    {
        RecipeParseResult result = Parse("[\"{in:record}\"]", DashChoices);

        Assert.False(result.Succeeded);
        RecipeError[] errors = [.. result.Errors.Where(error => error.Code == RecipeErrorCode.FixedValueStartsWithOptionCharacter)];
        Assert.Equal(2, errors.Length);
        Assert.All(errors, error => Assert.Equal("record", error.Name));
        Assert.Equal(["-type=A", "-type=AAAA"], errors.Select(error => error.Fragment));
        Assert.All(errors, error => Assert.Equal("arguments[0]", error.Path));
    }

    [Fact]
    public void ADashDefaultIsRefusedWhateverTheTypeOfTheInput()
    {
        foreach ((string type, string value) in new[] { ("string", "-v"), ("int", "-1"), ("number", "-1.5"), ("path", "-x") })
        {
            RecipeParseResult result = Parse(
                "[\"{in:x}\"]",
                $"[{{\"name\":\"x\",\"type\":\"{type}\",\"label\":\"X\",\"default\":\"{value}\"}}]");

            Assert.Contains(result.Errors, error => error.Code == RecipeErrorCode.FixedValueStartsWithOptionCharacter);
        }
    }

    [Fact]
    public void ASlashDefaultIsRefusedWhereItStartsTheArgument()
    {
        RecipeParseResult result = Parse(
            "[\"{in:x}\"]",
            "[{\"name\":\"x\",\"type\":\"string\",\"label\":\"X\",\"default\":\"/all\"}]");

        Assert.Contains(result.Errors, error => error.Code == RecipeErrorCode.FixedValueStartsWithOptionCharacter);
    }

    [Fact]
    public void TheSecondArgumentOfAGroupThatStartsWithTheInputIsHeldToTheSameRule()
    {
        RecipeParseResult result = Parse(
            "[[\"--count\", \"{in:x}\"]]",
            "[{\"name\":\"x\",\"type\":\"int\",\"label\":\"X\",\"default\":\"-5\"}]");

        Assert.Contains(result.Errors, error => error.Code == RecipeErrorCode.FixedValueStartsWithOptionCharacter && error.Path == "arguments[0][1]");
    }

    [Fact]
    public void TheSameValuesAreAcceptedWhenALiteralComesFirstInTheArgument()
    {
        RecipeParseResult result = Parse("[\"-type={in:record}\"]", DashChoices);

        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));
        RecipeExpansion expansion = RecipeExpander.Expand(
            result.Recipe!,
            new Dictionary<string, string?> { ["record"] = "-type=A" });
        Assert.True(expansion.Succeeded);
    }

    [Fact]
    public void BareValuesAreAcceptedWhereTheInputStartsTheArgument()
    {
        RecipeParseResult result = Parse(
            "[\"{in:record}\"]",
            "[{\"name\":\"record\",\"type\":\"choice\",\"label\":\"Record\",\"choices\":[\"A\",\"AAAA\"],\"default\":\"A\"}]");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void AnInputThatIsNeverUsedIsNotHeldToThePositionRule()
    {
        RecipeParseResult result = Parse(
            "[\"-n\"]",
            "[{\"name\":\"x\",\"type\":\"string\",\"label\":\"X\",\"default\":\"-v\"}]");

        Assert.DoesNotContain(result.Errors, error => error.Code == RecipeErrorCode.FixedValueStartsWithOptionCharacter);
    }

    [Fact]
    public void TheFormatVersionIsStillOne()
        => Assert.Equal(1, RecipeFormat.SupportedSchemaVersion);

    /// <summary>
    /// The theory below returns early on a file that does not parse, so a row that stopped parsing would prove
    /// nothing. This test reads the rows of that theory: a row is either valid, or refused by the option
    /// character rule alone (five rows are refused on purpose), and exactly the five others must be valid.
    /// </summary>
    [Fact]
    public void EveryRowOfTheNeverAlwaysRefusedTheoryIsValidOrRefusedForTheOptionCharacterOnly()
    {
        const int ValidRows = 5;
        int valid = 0;
        System.Reflection.MethodInfo method = typeof(RecipeFixedValueTests).GetMethod(nameof(AValidRecipeNeverHasAChoiceOrADefaultThatIsAlwaysRefused))!;
        InlineDataAttribute[] rows = [.. method.GetCustomAttributes(typeof(InlineDataAttribute), inherit: false).Cast<InlineDataAttribute>()];

        Assert.NotEmpty(rows);
        foreach (InlineDataAttribute row in rows)
        {
            object?[] data = [.. row.GetData(method).Single()];
            RecipeParseResult result = Parse((string)data[0]!, (string)data[1]!);

            Assert.True(
                result.Succeeded || result.Errors.All(error => error.Code == RecipeErrorCode.FixedValueStartsWithOptionCharacter),
                $"{data[0]} {data[1]}: {string.Join(", ", result.Errors.Select(error => error.Code + "@" + error.Path))}");
            if (result.Succeeded)
                valid++;
        }

        Assert.Equal(ValidRows, valid);
    }

    /// <summary>
    /// The property that makes the rule worth having: for each of these recipes, if it validates then
    /// every choice and every default expands without refusal at the place it is used.
    /// </summary>
    [Theory]
    [InlineData("[\"{in:c}\"]", "[{\"name\":\"c\",\"type\":\"choice\",\"label\":\"C\",\"required\":true,\"choices\":[\"-a\",\"b\"]}]")]
    [InlineData("[\"-t={in:c}\"]", "[{\"name\":\"c\",\"type\":\"choice\",\"label\":\"C\",\"required\":true,\"choices\":[\"-a\",\"b\"]}]")]
    [InlineData("[\"{in:c}\"]", "[{\"name\":\"c\",\"type\":\"choice\",\"label\":\"C\",\"required\":true,\"choices\":[\"/a\",\"b\"]}]")]
    [InlineData("[\"{in:c}\"]", "[{\"name\":\"c\",\"type\":\"choice\",\"label\":\"C\",\"choices\":[\"a\",\"b\"],\"default\":\"b\"}]")]
    [InlineData("[\"{in:s}\"]", "[{\"name\":\"s\",\"type\":\"string\",\"label\":\"S\",\"default\":\"-x\"}]")]
    [InlineData("[\"--s={in:s}\"]", "[{\"name\":\"s\",\"type\":\"string\",\"label\":\"S\",\"default\":\"-x\"}]")]
    [InlineData("[\"{in:n}\"]", "[{\"name\":\"n\",\"type\":\"int\",\"label\":\"N\",\"default\":\"-3\"}]")]
    [InlineData("[\"-n={in:n}\"]", "[{\"name\":\"n\",\"type\":\"int\",\"label\":\"N\",\"default\":\"-3\"}]")]
    [InlineData("[[\"-w\", \"{in:n}\"]]", "[{\"name\":\"n\",\"type\":\"int\",\"label\":\"N\",\"default\":\"-3\"}]")]
    [InlineData("[\"{in:p}\"]", "[{\"name\":\"p\",\"type\":\"path\",\"label\":\"P\",\"default\":\"C:\\\\Tools\"}]")]
    public void AValidRecipeNeverHasAChoiceOrADefaultThatIsAlwaysRefused(string argumentsJson, string inputsJson)
    {
        RecipeParseResult result = Parse(argumentsJson, inputsJson);
        if (!result.Succeeded)
        {
            return;
        }

        Recipe recipe = result.Recipe!;
        foreach (RecipeInput input in recipe.Inputs)
        {
            List<string?> candidates = [null, .. input.Choices ?? []];
            foreach (string? candidate in candidates)
            {
                Dictionary<string, string?> values = [];
                foreach (RecipeInput other in recipe.Inputs)
                {
                    values[other.Name] = other.Default ?? (other.Type == RecipeInputType.Int ? "1" : other.Choices?[0] ?? "abc");
                }

                if (candidate is not null)
                {
                    values[input.Name] = candidate;
                }

                RecipeExpansion expansion = RecipeExpander.Expand(recipe, values, "host", "22");
                Assert.True(
                    expansion.Succeeded,
                    $"{input.Name}={candidate ?? "(default)"}: {string.Join(", ", expansion.Refusals.Select(refusal => refusal.Reason))}");
            }
        }
    }
}
