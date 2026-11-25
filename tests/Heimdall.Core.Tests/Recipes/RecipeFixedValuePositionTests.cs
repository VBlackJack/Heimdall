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
/// A choice or a default the author fixed is held, at import, to everything the expander does at the
/// place the placeholder stands, wherever in the token that is: a recipe that validates is a recipe
/// that runs with its own choices and defaults.
/// </summary>
public sealed class RecipeFixedValuePositionTests
{
    private static IRecipeCharacterConverter Plain => FakeCharacterConverter.Plain;

    private static RecipeInput Choice(params string[] choices)
        => RecipeTestData.Input("c", RecipeInputType.Choice, required: true) with { Choices = choices };

    private static RecipeInput TextDefault(string value)
        => RecipeTestData.Input("v", RecipeInputType.String, required: false, value) with { MaxLength = RecipeLimits.MaxStringValueLength };

    private static RecipeInput PathDefault(string value)
        => RecipeTestData.Input("p", RecipeInputType.Path, required: false, value);

    private static IReadOnlyList<RecipeError> Validate(IEnumerable<RecipeInput> inputs, params RecipeArgument[] arguments)
        => RecipeValidator.Validate(RecipeTestData.With(inputs, arguments), Plain);

    private static void AssertRefusedAtRun(IReadOnlyList<RecipeError> errors, string name, string value)
    {
        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.FixedValueRefusedAtRun, error.Code);
        Assert.Equal(name, error.Name);
        Assert.Equal(value, error.Fragment);
    }

    // ---- the cases the rule was written for

    [Theory]
    [InlineData("{in:c}/x")]
    [InlineData("--p={in:c}/x")]
    [InlineData(@"{in:c}\x")]
    [InlineData(@"--p={in:c}\x")]
    public void AChoiceThatEndsWithASeparator_BeforeALiteralThatStartsWithOne_IsRefused(string token)
        => AssertRefusedAtRun(Validate([Choice("a/", "b")], RecipeArgument.Bare(token)), "c", "a/");

    [Fact]
    public void APathDefaultThatEndsWithABackslash_BeforeALiteralThatStartsWithOne_IsRefused()
        => AssertRefusedAtRun(Validate([PathDefault(@"C:\Tools\")], RecipeArgument.Bare(@"--d={in:p}\x")), "p", @"C:\Tools\");

    [Fact]
    public void ASeparatorAfterTheValue_JoinsTheValueEvenWhenItIsTheOtherForm()
        => AssertRefusedAtRun(Validate([PathDefault(@"C:\Tools\")], RecipeArgument.Bare("--d={in:p}/x")), "p", @"C:\Tools\");

    [Theory]
    [InlineData("/{in:c}")]
    [InlineData(@"\{in:c}")]
    [InlineData("--p=/{in:c}")]
    public void AChoiceThatStartsWithASeparator_AfterALiteralThatEndsWithOne_IsRefused(string token)
        => AssertRefusedAtRun(Validate([Choice("/a", "b")], RecipeArgument.Bare(token)), "c", "/a");

    [Fact]
    public void ADefaultThatStartsWithABackslash_WhereThePlaceholderStartsTheToken_IsRefused()
    {
        AssertRefusedAtRun(Validate([TextDefault(@"\x")], RecipeArgument.Bare("{in:v}")), "v", @"\x");
        Assert.Empty(Validate([TextDefault(@"\x")], RecipeArgument.Bare("--v={in:v}")));
    }

    [Fact]
    public void ADefaultThatStartsWithASpace_WhereThePlaceholderStartsTheToken_IsRefused()
    {
        AssertRefusedAtRun(Validate([TextDefault(" x")], RecipeArgument.Bare("{in:v}")), "v", " x");
        Assert.Empty(Validate([TextDefault(" x")], RecipeArgument.Bare("--v={in:v}")));
    }

    [Fact]
    public void ADashChoice_InTheMiddleOfAGroupTokenThatStartsWithTheInput_IsStillTheOptionCharacterError()
    {
        IReadOnlyList<RecipeError> errors = Validate([Choice("-a", "b")], RecipeArgument.Group(["--x", "{in:c}"]));

        Assert.Equal(RecipeErrorCode.FixedValueStartsWithOptionCharacter, Assert.Single(errors).Code);
    }

    [Fact]
    public void AValueThatFitsItsPlace_IsAccepted()
    {
        Assert.Empty(Validate([Choice("a/", "b")], RecipeArgument.Bare("{in:c}x")));
        Assert.Empty(Validate([Choice("a/", "b")], RecipeArgument.Bare("{in:c}")));
        Assert.Empty(Validate([PathDefault(@"C:\Tools")], RecipeArgument.Bare(@"--d={in:p}\x")));
        Assert.Empty(Validate([PathDefault(@"C:\Tools\")], RecipeArgument.Bare("--d={in:p}x")));
    }

    // ---- the property

    private static readonly string[] Values = ["a", "a/", "/a", @"\a", @"a\", " a", "a ", "-a", "@a", "a:", "a/b"];

    private static readonly string[] Templates =
    [
        "{in:v}",
        "x{in:v}",
        "{in:v}x",
        "{in:v}/x",
        @"{in:v}\x",
        "/{in:v}",
        @"\{in:v}",
        "--o={in:v}",
        "--o={in:v}/x",
        @"--o={in:v}\x",
        "{in:v}:",
        ":{in:v}",
        "{in:v}{in:w}",
        "{in:w}{in:v}",
    ];

    /// <summary>
    /// For every value at every place: if the recipe validates, the value expands without refusal.
    /// The sweep is not vacuous: some combinations validate and some do not.
    /// </summary>
    [Fact]
    public void WhateverTheValueAndThePlace_AValidRecipeNeverHasAFixedValueThatIsAlwaysRefused()
    {
        int validated = 0;
        int refused = 0;
        foreach (string template in Templates)
        {
            foreach (string value in Values)
            {
                foreach (bool asChoice in new[] { false, true })
                {
                    RecipeInput subject = asChoice
                        ? RecipeTestData.Input("v", RecipeInputType.Choice, required: true) with { Choices = [value] }
                        : TextDefault(value);
                    RecipeInput other = RecipeTestData.Input("w", RecipeInputType.String, required: false, "ok");
                    Recipe recipe = RecipeTestData.With([subject, other], RecipeArgument.Bare(template));

                    if (RecipeValidator.Validate(recipe, Plain).Count > 0)
                    {
                        refused++;
                        continue;
                    }

                    validated++;
                    RecipeExpansion expansion = RecipeExpander.Expand(
                        recipe,
                        new Dictionary<string, string?> { ["v"] = value },
                        null,
                        null,
                        Plain);
                    Assert.True(
                        expansion.Succeeded,
                        $"{template} with {value} ({(asChoice ? "choice" : "default")}): {string.Join(", ", expansion.Refusals.Select(refusal => refusal.Reason))}");
                }
            }
        }

        Assert.True(validated > 20, validated.ToString());
        Assert.True(refused > 20, refused.ToString());
    }
}
