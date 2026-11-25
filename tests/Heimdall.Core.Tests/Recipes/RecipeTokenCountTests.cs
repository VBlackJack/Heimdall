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
/// The token cap counts what the expander can produce: a switch placed as a bare token becomes its whenTrue
/// tokens once per placement, and the declared whenTrue tokens are not counted on their own.
/// </summary>
public sealed class RecipeTokenCountTests
{
    private static FakeCharacterConverter Plain => FakeCharacterConverter.Plain;

    private static RecipeInput Flag(string name, params string[] whenTrue)
        => RecipeTestData.Input(name, RecipeInputType.Bool) with { WhenTrue = whenTrue };

    private static RecipeArgument[] Bare(int count, string token = "x")
        => [.. Enumerable.Range(0, count).Select(_ => RecipeArgument.Bare(token))];

    private static IReadOnlyList<RecipeError> Validate(Recipe recipe) => RecipeValidator.Validate(recipe, Plain);

    [Fact]
    public void ThirtyTwoPlacementsOfASwitchWithThirtyTwoTokens_AreRefused()
    {
        RecipeInput flag = Flag("flag", [.. Enumerable.Range(0, 32).Select(index => "-t" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        Recipe recipe = RecipeTestData.Minimal() with
        {
            Inputs = [flag],
            Arguments = [.. Enumerable.Range(0, 32).Select(_ => RecipeArgument.Bare("{in:flag}"))],
        };

        RecipeError error = Assert.Single(Validate(recipe));

        Assert.Equal(RecipeErrorCode.TooMany, error.Code);
        Assert.Equal(32 * 32, error.Actual);
        Assert.Equal(RecipeLimits.MaxTokens, error.Limit);
    }

    [Fact]
    public void ASwitchWithOneTokenPlacedOnce_CountsOne()
    {
        Recipe atCap = RecipeTestData.Minimal() with
        {
            Inputs = [Flag("flag", "-a")],
            Arguments = [.. Bare(RecipeLimits.MaxTokens - 1), RecipeArgument.Bare("{in:flag}")],
        };
        Recipe over = atCap with { Arguments = [.. atCap.Arguments, RecipeArgument.Bare("y")] };

        Assert.Empty(Validate(atCap));
        Assert.Equal(RecipeLimits.MaxTokens + 1, Assert.Single(Validate(over)).Actual);
    }

    [Fact]
    public void ASwitchWithTwoTokensPlacedThreeTimes_CountsSix()
    {
        RecipeArgument[] placements = [.. Enumerable.Range(0, 3).Select(_ => RecipeArgument.Bare("{in:flag}"))];
        Recipe atCap = RecipeTestData.Minimal() with
        {
            Inputs = [Flag("flag", "-a", "-b")],
            Arguments = [.. Bare(RecipeLimits.MaxTokens - 6), .. placements],
        };
        Recipe over = atCap with { Arguments = [.. Bare(RecipeLimits.MaxTokens - 5), .. placements] };

        Assert.Empty(Validate(atCap));
        RecipeError error = Assert.Single(Validate(over));
        Assert.Equal(RecipeErrorCode.TooMany, error.Code);
        Assert.Equal(RecipeLimits.MaxTokens + 1, error.Actual);
    }

    [Fact]
    public void ASwitchPlacedTwice_IsStillAllowed()
    {
        Recipe recipe = RecipeTestData.Minimal() with
        {
            Inputs = [Flag("flag", "-a")],
            Arguments = [RecipeArgument.Bare("{in:flag}"), RecipeArgument.Bare("{in:flag}")],
        };

        Assert.Empty(Validate(recipe));
    }

    public static TheoryData<int> Tables => new() { 0, 1, 2, 3, 5, 10 };

    [Theory]
    [MemberData(nameof(Tables))]
    public void TheExpandersRealOutput_NeverExceedsTheValidatorsCount(int placements)
    {
        RecipeInput flag = Flag("flag", "-a", "-b", "-c");
        RecipeArgument[] arguments =
        [
            RecipeArgument.Bare("fixed"),
            RecipeArgument.Group(["--g", "1"]),
            .. Enumerable.Range(0, placements).Select(_ => RecipeArgument.Bare("{in:flag}")),
            RecipeArgument.Bare("{in:flag}"),
        ];
        Recipe recipe = RecipeTestData.Minimal() with { Inputs = [flag], Arguments = arguments };
        Assert.Empty(Validate(recipe));

        RecipeExpansion on = RecipeExpander.Expand(recipe, new Dictionary<string, string?> { ["flag"] = "true" }, null, null, Plain);
        RecipeExpansion off = RecipeExpander.Expand(recipe, new Dictionary<string, string?> { ["flag"] = "false" }, null, null, Plain);

        int counted = 1 + 2 + ((placements + 1) * 3);
        Assert.True(on.Succeeded && off.Succeeded);
        Assert.Equal(counted, on.Arguments.Count);
        Assert.True(off.Arguments.Count <= counted);
    }

    [Fact]
    public void AProgramHoldingALookAlikeCharacter_IsRefused()
    {
        Recipe recipe = RecipeTestData.Minimal() with { Program = "pi\uFF0Dng.exe" };

        RecipeError error = Assert.Single(Validate(recipe));

        Assert.Equal(RecipeErrorCode.ForbiddenLookAlikeCharacter, error.Code);
        Assert.Equal(RecipePath.Program, error.Path);
    }

    [Fact]
    public void AnOrdinaryProgram_IsStillAccepted()
        => Assert.Empty(Validate(RecipeTestData.Minimal() with { Program = "ping.exe" }));
}
