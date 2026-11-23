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

using System.IO;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The recipe overlay of the tool registry: composed with the external overlay in both orders,
/// found by its <c>RCP:</c> id, never localised, iconed from the closed key set, and announced.
/// </summary>
public sealed class RecipeToolRegistryTests
{
    private static readonly Func<Recipe, IToolView> NoView = _ => throw new InvalidOperationException("never built");

    private static ExternalToolInfo External(string id = "PSEXEC") => new()
    {
        Id = id,
        Name = "PsExec",
        ExecutablePath = @"C:\Tools\psexec.exe",
        ProviderName = "Sysinternals",
    };

    private static string[] RecipeIds(ToolRegistry registry)
        => [.. registry.All.Where(descriptor => descriptor.Pack == ToolPack.Recipes).Select(descriptor => descriptor.Id)];

    [Fact]
    public void ARecipeToolIsAnEntryOfThePackWithItsAuthorAndNoProviderName()
    {
        ToolRegistry registry = new();

        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping", "Ping")], NoView);

        ToolDescriptor descriptor = Assert.Single(registry.All, item => item.IsRecipe);
        Assert.Equal("RCP:acme.ping", descriptor.Id);
        Assert.Equal(ToolPack.Recipes, descriptor.Pack);
        Assert.Equal("Acme", descriptor.RecipeAuthor);
        Assert.Equal("Ping the selected host.", descriptor.RecipeDescription);
        Assert.Equal("Ping", descriptor.LabelKey);
        Assert.Null(descriptor.ExternalProviderName);
        Assert.Equal(["acme.ping"], descriptor.CommandPrefixes);
        Assert.False(descriptor.IsNetworkTool);
        Assert.Equal(58 + 1, registry.All.Count);
    }

    [Fact]
    public void TheIdOfARecipeIsFoundWithOrWithoutTheToolPrefix()
    {
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], NoView);

        Assert.Equal("RCP:acme.ping", registry.GetById("RCP:acme.ping")!.Id);
        Assert.Equal("RCP:acme.ping", registry.GetById("TOOL:RCP:acme.ping")!.Id);
        Assert.Equal("RCP:acme.ping", registry.GetById("rcp:ACME.PING")!.Id);
        Assert.Null(registry.GetById("acme.ping"));
    }

    [Fact]
    public void ARecipeCannotTakeTheIdOfABuiltInTool()
    {
        ToolRegistry registry = new();

        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping", "Ping")], NoView);

        Assert.Equal(ToolPack.NetworkDiagnostics, registry.GetById("PING")!.Pack);
        Assert.False(registry.GetById("PING")!.IsRecipe);
    }

    [Fact]
    public void ARecipeNamedLikeALocaleKeyDisplaysItsOwnName()
    {
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.fake", "PaletteToolPing")], NoView);

        Func<string, string> localize = key => key == "PaletteToolPing" ? "Ping (translated)" : key;

        Assert.Equal("PaletteToolPing", registry.GetById("RCP:acme.fake")!.ResolveLabel(localize));
        Assert.Equal("Ping (translated)", registry.GetById("PING")!.ResolveLabel(localize));
    }

    [Fact]
    public async Task ANameThatIsARealLocaleKeyIsNotTranslatedWithTheShippedLocale()
    {
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.fake", "PaletteToolPing")], NoView);
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RepositoryRoot(), "locales"), "en");

        Assert.Equal("PaletteToolPing", registry.GetById("RCP:acme.fake")!.ResolveLabel(localizer.GetString));
        Assert.NotEqual("PaletteToolPing", registry.GetById("PING")!.ResolveLabel(localizer.GetString));
    }

    [Fact]
    public void RegisteringExternalToolsKeepsTheRecipes()
    {
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], NoView);

        registry.RegisterExternalTools([External()]);

        Assert.Equal(["RCP:acme.ping"], RecipeIds(registry));
        Assert.NotNull(registry.GetById("RCP:acme.ping"));
        Assert.NotNull(registry.GetById("EXT:SYSINTERNALS:PSEXEC"));
        Assert.Equal(58 + 2, registry.All.Count);
    }

    [Fact]
    public void RegisteringRecipesKeepsTheExternalTools()
    {
        ToolRegistry registry = new();
        registry.RegisterExternalTools([External()]);

        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], NoView);

        Assert.NotNull(registry.GetById("EXT:SYSINTERNALS:PSEXEC"));
        Assert.Equal(["RCP:acme.ping"], RecipeIds(registry));
        Assert.Equal(58 + 2, registry.All.Count);
    }

    [Fact]
    public void TheCompositionIsTheSameInBothOrdersAndEndsWithTheOverlays()
    {
        ToolRegistry recipesLast = new();
        recipesLast.RegisterExternalTools([External()]);
        recipesLast.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], NoView);
        ToolRegistry recipesFirst = new();
        recipesFirst.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], NoView);
        recipesFirst.RegisterExternalTools([External()]);

        Assert.Equal(
            recipesLast.All.Select(descriptor => descriptor.Id),
            recipesFirst.All.Select(descriptor => descriptor.Id));
        Assert.Equal(["EXT:SYSINTERNALS:PSEXEC", "RCP:acme.ping"], recipesLast.All.Skip(58).Select(descriptor => descriptor.Id));
    }

    [Fact]
    public void RegisteringRecipesAgainReplacesTheEarlierSet()
    {
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.one"), RecipeTestFiles.Recipe("acme.two")], NoView);

        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.two"), RecipeTestFiles.Recipe("acme.three")], NoView);

        Assert.Equal(["RCP:acme.two", "RCP:acme.three"], RecipeIds(registry));
        Assert.Null(registry.GetById("RCP:acme.one"));
        registry.RegisterRecipeTools([], NoView);
        Assert.Empty(RecipeIds(registry));
        Assert.Equal(58, registry.All.Count);
    }

    [Fact]
    public void ARepeatedIdKeepsOneEntry()
    {
        ToolRegistry registry = new();

        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping", "First"), RecipeTestFiles.Recipe("acme.ping", "Second")], NoView);

        ToolDescriptor only = Assert.Single(registry.All, item => item.IsRecipe);
        Assert.Equal("Second", only.LabelKey);
    }

    [Fact]
    public void TheViewOfARecipeComesFromTheInjectedFactory()
    {
        ToolRegistry registry = new();
        Recipe recipe = RecipeTestFiles.Recipe("acme.ping");
        FakeView built = new();
        Recipe? received = null;
        registry.RegisterRecipeTools([recipe], given =>
        {
            received = given;
            return built;
        });

        IToolView view = registry.CreateView("TOOL:RCP:acme.ping");

        Assert.Same(built, view);
        Assert.Same(recipe, received);
    }

    [Fact]
    public void CreatingTheViewOfAnUnregisteredRecipeThrows()
    {
        ToolRegistry registry = new();

        Assert.Throws<ArgumentException>(() => registry.CreateView("RCP:acme.ping"));
    }

    [Fact]
    public void TheRecipeEventFiresOncePerRegistrationAndTheExternalEventDoesNot()
    {
        ToolRegistry registry = new();
        int recipeEvents = 0;
        int externalEvents = 0;
        registry.RecipeToolsChanged += () => recipeEvents++;
        registry.ExternalToolsChanged += () => externalEvents++;

        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], NoView);
        registry.RegisterRecipeTools([], NoView);

        Assert.Equal(2, recipeEvents);
        Assert.Equal(0, externalEvents);

        registry.RegisterExternalTools([External()]);

        Assert.Equal(2, recipeEvents);
        Assert.Equal(1, externalEvents);
    }

    [Fact]
    public void TheRecipeEventSeesTheNewCompositionWhenItFires()
    {
        ToolRegistry registry = new();
        int seen = -1;
        registry.RecipeToolsChanged += () => seen = registry.All.Count(descriptor => descriptor.IsRecipe);

        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.one"), RecipeTestFiles.Recipe("acme.two")], NoView);

        Assert.Equal(2, seen);
    }

    [Fact]
    public async Task TheOverlaysCanBeReplacedFromSeveralThreadsAtOnce()
    {
        ToolRegistry registry = new();
        Task[] workers = [.. Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int round = 0; round < 50; round++)
            {
                if (worker % 2 == 0)
                {
                    registry.RegisterRecipeTools([RecipeTestFiles.Recipe($"acme.w{worker}")], NoView);
                }
                else
                {
                    registry.RegisterExternalTools([External($"T{worker}")]);
                }

                Assert.NotNull(registry.GetById("PING"));
                Assert.True(registry.All.Count >= 58);
            }
        }))];

        await Task.WhenAll(workers);

        Assert.Single(registry.All, descriptor => descriptor.Id == "PING");
    }

    [Fact]
    public void EveryIconKeyOfTheFormatMapsToAGeometryThatExists()
    {
        string geometries = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Heimdall.App", "Themes", "IconGeometries.xaml"));

        foreach (string key in RecipeIcons.Keys)
        {
            string geometry = ToolRegistry.RecipeGeometryKeyOf(key);

            Assert.Contains($"x:Key=\"{geometry}\"", geometries, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("network", "Geo.Tool.NetworkScanner")]
    [InlineData("tool", "Geo.Tool.External")]
    [InlineData(null, "Geo.Tool.External")]
    [InlineData("not-an-icon", "Geo.Tool.External")]
    public void TheIconKeyOfARecipeSelectsABuiltInGlyph(string? iconKey, string expected)
        => Assert.Equal(expected, ToolRegistry.RecipeGeometryKeyOf(iconKey));

    [Fact]
    public void TheDescriptorAndTheStaticLookupAgreeOnTheIconOfARegisteredRecipe()
    {
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.iconcheck", icon: "network")], NoView);

        Assert.Equal("Geo.Tool.NetworkScanner", registry.GetById("RCP:acme.iconcheck")!.IconResourceKey);
        Assert.Equal("Geo.Tool.NetworkScanner", ToolRegistry.GetGeometryKey("TOOL:RCP:acme.iconcheck"));
    }

    [Fact]
    public void AnUnregisteredRecipeIdGetsTheGenericGlyphAndTheExternalBrush()
    {
        Assert.Equal("Geo.Tool.External", ToolRegistry.GetGeometryKey("TOOL:RCP:acme.never-registered"));
        Assert.Equal("ToolExternalBrush", ToolRegistry.GetCategoryBrushKey("TOOL:RCP:acme.never-registered"));
        Assert.Equal("ToolExternalBrush", ToolRegistry.GetCategoryBrushKey("RCP:acme.ping"));
    }

    [Fact]
    public void BuiltInAndExternalLookupsAreUnchanged()
    {
        Assert.Equal("Geo.Tool.NetworkScanner", ToolRegistry.GetGeometryKey("PING"));
        Assert.Equal("Geo.Tool.External", ToolRegistry.GetGeometryKey("EXT:X:Y"));
        Assert.Null(ToolRegistry.GetGeometryKey("NOT-A-TOOL"));
        Assert.Equal("ToolNetworkBrush", ToolRegistry.GetCategoryBrushKey("PING"));
        Assert.Equal("ToolBadgeBrush", ToolRegistry.GetCategoryBrushKey("NOT-A-TOOL"));
    }

    [Fact]
    public void AvailabilityFollowsThePackForARecipeTool()
    {
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], NoView);
        ToolPackPolicy open = new(_ => null);
        AppSettings off = new() { DisabledToolPacks = [nameof(ToolPack.Recipes)] };
        AppSettings on = new();

        Assert.Equal(ToolAvailabilityState.DisabledByUser, new ToolAvailability(registry, open, () => off, _ => { }).GetState("TOOL:RCP:acme.ping"));
        Assert.Equal(ToolAvailabilityState.Available, new ToolAvailability(registry, open, () => on, _ => { }).GetState("TOOL:RCP:acme.ping"));
    }

    [Fact]
    public void ThePolicyForbidsRecipeToolsWhateverTheUserChose()
    {
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], NoView);
        ToolPackPolicy forbidding = new(name => name == "ToolPackRecipes" ? ToolPackPolicy.ForbiddenValue : null);
        ToolAvailability availability = new(registry, forbidding, () => new AppSettings(), _ => { });

        Assert.Equal(ToolAvailabilityState.ForbiddenByPolicy, availability.GetState("RCP:acme.ping"));
        Assert.True(availability.IsPackForbiddenByPolicy(ToolPack.Recipes));
        Assert.False(availability.IsPackEnabled(ToolPack.Recipes));
        Assert.DoesNotContain(registry.All.Where(availability.IsListed), descriptor => descriptor.IsRecipe);
    }

    [Fact]
    public void ARemovedRecipeBecomesUnknownToTheAvailabilityService()
    {
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], NoView);
        ToolAvailability availability = new(registry, new ToolPackPolicy(_ => null), () => new AppSettings(), _ => { });

        registry.RegisterRecipeTools([], NoView);

        Assert.Equal(ToolAvailabilityState.Unknown, availability.GetState("RCP:acme.ping"));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No Heimdall.slnx above {AppContext.BaseDirectory}");
    }

    private sealed class FakeView : IToolView
    {
        public void Initialize(ToolContext? context, LocalizationManager? localizer)
        {
        }

        public void Dispose()
        {
        }
    }
}
