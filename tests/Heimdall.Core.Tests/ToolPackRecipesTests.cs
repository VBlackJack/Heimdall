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

using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// The Recipes pack through the machine policy and the descriptor of a recipe tool. The policy
/// iterates the enum, so the new pack needs no code there; these tests pin that it does not.
/// </summary>
public sealed class ToolPackRecipesTests
{
    [Fact]
    public void TheRecipesPackHasItsOwnPolicyValueName()
        => Assert.Equal("ToolPackRecipes", ToolPackPolicy.ValueNameFor(ToolPack.Recipes));

    [Fact]
    public void TheRecipesPackIsTheLastMemberOfTheEnum()
        => Assert.Equal(ToolPack.Recipes, Enum.GetValues<ToolPack>().Last());

    [Fact]
    public void ZeroInTheRecipesValueForbidsThePackAndOnlyThatPack()
    {
        ToolPackPolicy policy = new(name => name == "ToolPackRecipes" ? ToolPackPolicy.ForbiddenValue : null);

        Assert.True(policy.IsForbidden(ToolPack.Recipes));
        Assert.All(
            Enum.GetValues<ToolPack>().Where(pack => pack != ToolPack.Recipes),
            pack => Assert.False(policy.IsForbidden(pack), pack.ToString()));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    public void OnlyZeroForbidsTheRecipesPack(int? raw, bool forbidden)
    {
        ToolPackPolicy policy = new(name => name == "ToolPackRecipes" ? raw : null);

        Assert.Equal(forbidden, policy.IsForbidden(ToolPack.Recipes));
    }

    [Fact]
    public void EveryOptionalPackOfTheEnumIsReadFromItsOwnValue()
    {
        List<string> requested = [];
        ToolPackPolicy policy = new(name =>
        {
            requested.Add(name);
            return null;
        });

        _ = policy.IsForbidden(ToolPack.Recipes);

        string[] expected = [.. Enum.GetValues<ToolPack>().Where(pack => pack != ToolPack.Core).Select(ToolPackPolicy.ValueNameFor)];
        Assert.Equal(expected.Order(StringComparer.Ordinal), requested.Order(StringComparer.Ordinal));
        Assert.Contains("ToolPackRecipes", requested);
    }

    private static ToolDescriptor RecipeDescriptor(string name) => new(
        Id: ToolDescriptor.RecipeIdPrefix + "acme.ping",
        Category: ToolCategory.External,
        CategoryLabelKey: "ToolCategoryExternal",
        LabelKey: name,
        LabelWithArgKey: null,
        CommandPrefixes: ["acme.ping"],
        IsNetworkTool: false,
        Pack: ToolPack.Recipes,
        RecipeAuthor: "Acme");

    [Fact]
    public void ARecipeNamedLikeALocaleKeyDisplaysItsOwnRawName()
    {
        ToolDescriptor descriptor = RecipeDescriptor("PaletteToolPing");

        string label = descriptor.ResolveLabel(key => key == "PaletteToolPing" ? "Translated Ping" : key);

        Assert.Equal("PaletteToolPing", label);
    }

    [Fact]
    public void TheNameOfARecipeIsNeverLookedUpAsALocaleKey()
    {
        ToolDescriptor descriptor = RecipeDescriptor("Anything");

        string label = descriptor.ResolveLabel(_ => throw new InvalidOperationException("looked up"));

        Assert.Equal("Anything", label);
    }

    [Fact]
    public void ABuiltInToolStillResolvesItsLabelThroughTheLocale()
    {
        ToolDescriptor descriptor = new("PING", ToolCategory.Network, "ToolCategoryNetwork", "PaletteToolPing", null, ["ping"], true);

        Assert.Equal("Translated Ping", descriptor.ResolveLabel(key => key == "PaletteToolPing" ? "Translated Ping" : key));
        Assert.False(descriptor.IsRecipe);
    }

    [Fact]
    public void ARecipeDescriptorKnowsItIsOne()
    {
        ToolDescriptor descriptor = RecipeDescriptor("Ping");

        Assert.True(descriptor.IsRecipe);
        Assert.Equal("Acme", descriptor.RecipeAuthor);
        Assert.Null(descriptor.ExternalProviderName);
        Assert.StartsWith(ToolDescriptor.RecipeIdPrefix, descriptor.Id, StringComparison.Ordinal);
    }
}
