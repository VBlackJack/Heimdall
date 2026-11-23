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
using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;
using static Heimdall.App.Tests.RecipeBuilders;

namespace Heimdall.App.Tests;

/// <summary>
/// Every surface that lists a tool says what a recipe tool is: its own raw name, the word "Recipe"
/// and the author marked as unverified. The tab, the sidebar row and the palette row are tested
/// here; the Tools tab card needs the application resources and is tested with the UI tests.
/// </summary>
public sealed class RecipeToolSurfaceTests
{
    private static ToolDescriptor DescriptorOf(string name = "Probe", string author = "Acme", string id = "acme.probe")
        => ToolRegistry.CreateRecipeDescriptor(Recipe(id: id, name: name, author: author));

    private static async Task<LocalizationManager> LocalizerAsync(string locale = "en")
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), locale);
        return localizer;
    }

    [Fact]
    public async Task TheProvenanceIsOneLocalizedFormatThatMarksTheAuthorAsUnverified()
    {
        LocalizationManager english = await LocalizerAsync("en");
        LocalizationManager french = await LocalizerAsync("fr");
        LocalizationManager spanish = await LocalizerAsync("es");

        Assert.Equal("Recipe by Acme (unverified)", RecipeToolText.Provenance("Acme", english.GetString));
        Assert.Equal("Recette de Acme (non v\u00e9rifi\u00e9e)", RecipeToolText.Provenance("Acme", french.GetString));
        Assert.Equal("Receta de Acme (sin verificar)", RecipeToolText.Provenance("Acme", spanish.GetString));
    }

    [Fact]
    public async Task TheTabOfARecipeToolCarriesItsNameItsWordRecipeAndItsAuthor()
    {
        LocalizationManager localizer = await LocalizerAsync();

        string title = RecipeToolText.TabTitle(DescriptorOf(), localizer.GetString);

        Assert.Equal("Probe - Recipe by Acme (unverified)", title);
    }

    [Fact]
    public async Task TheTabOfAnyOtherToolIsItsPlainName()
    {
        LocalizationManager localizer = await LocalizerAsync();
        ToolDescriptor ping = new ToolRegistry().GetById("PING")!;

        Assert.Equal(ping.ResolveLabel(localizer.GetString), RecipeToolText.TabTitle(ping, localizer.GetString));
        Assert.Equal(ping.ResolveLabel(localizer.GetString), RecipeToolText.AccessibleName(ping, localizer.GetString));
        Assert.Null(RecipeToolText.ProvenanceOf(ping, localizer.GetString));
    }

    [Fact]
    public async Task TheAccessibleNameCarriesTheSameInformationAsTheVisibleOne()
    {
        LocalizationManager localizer = await LocalizerAsync();

        string name = RecipeToolText.AccessibleName(DescriptorOf(), localizer.GetString);

        Assert.Equal("Probe, Recipe by Acme (unverified)", name);
    }

    [Fact]
    public async Task TheSidebarRowOfARecipeShowsItsNameAndItsProvenance()
    {
        LocalizationManager localizer = await LocalizerAsync();

        SidebarToolItemViewModel row = ToolsTabPopulationService.CreateSidebarToolItem(DescriptorOf(), localizer.GetString);

        Assert.Equal("RCP:acme.probe", row.Id);
        Assert.Equal("Probe", row.Name);
        Assert.Equal("Recipe by Acme (unverified)", row.Provenance);
        Assert.True(row.HasProvenance);
        Assert.Equal("Probe, Recipe by Acme (unverified)", row.AccessibleName);
        Assert.Equal("Probe - Recipe by Acme (unverified)", row.ToolTipText);
        Assert.Contains("unverified", row.Searchable);
        Assert.Contains("acme", row.Searchable);
    }

    [Fact]
    public async Task TheSidebarRowOfABuiltInToolIsUnchanged()
    {
        LocalizationManager localizer = await LocalizerAsync();
        ToolDescriptor ping = new ToolRegistry().GetById("PING")!;

        SidebarToolItemViewModel row = ToolsTabPopulationService.CreateSidebarToolItem(ping, localizer.GetString);

        Assert.Equal(ping.ResolveLabel(localizer.GetString), row.Name);
        Assert.Null(row.Provenance);
        Assert.False(row.HasProvenance);
        Assert.Equal(row.Name, row.AccessibleName);
        Assert.Equal(row.Name, row.ToolTipText);
    }

    [Fact]
    public async Task ARecipeNamedLikeALocaleKeyStillShowsItsOwnNameAndIsMarkedAsARecipe()
    {
        LocalizationManager localizer = await LocalizerAsync();
        ToolDescriptor fake = DescriptorOf(name: "PaletteToolPing", id: "evil.ping");

        SidebarToolItemViewModel row = ToolsTabPopulationService.CreateSidebarToolItem(fake, localizer.GetString);

        Assert.Equal("PaletteToolPing", row.Name);
        Assert.Equal("Recipe by Acme (unverified)", row.Provenance);
        Assert.Equal("PaletteToolPing - Recipe by Acme (unverified)", RecipeToolText.TabTitle(fake, localizer.GetString));
        Assert.Equal("Probes the thing.", ToolsTabPopulationService.ResolveCardDescription(fake, localizer.GetString));
    }

    [Fact]
    public async Task ARecipeNamedLikeABuiltInToolIsStillMarkedAsARecipe()
    {
        LocalizationManager localizer = await LocalizerAsync();
        string pingName = new ToolRegistry().GetById("PING")!.ResolveLabel(localizer.GetString);
        ToolDescriptor lookalike = DescriptorOf(name: pingName, id: "evil.ping");

        string title = RecipeToolText.TabTitle(lookalike, localizer.GetString);
        SidebarToolItemViewModel row = ToolsTabPopulationService.CreateSidebarToolItem(lookalike, localizer.GetString);

        Assert.StartsWith(pingName, title, StringComparison.Ordinal);
        Assert.EndsWith("(unverified)", title, StringComparison.Ordinal);
        Assert.NotNull(row.Provenance);
        Assert.NotEqual("PING", row.Id);
    }

    [Fact]
    public async Task ThePaletteRowOfARecipeShowsItsNameAndItsProvenanceAndABuiltInRowIsUnchanged()
    {
        LocalizationManager localizer = await LocalizerAsync();
        CommandPaletteViewModel palette = new(null!, localizer, null!, null!, null!, null!, null!, null!, null!);
        MethodInfo build = typeof(CommandPaletteViewModel).GetMethod("BuildToolPaletteItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ToolDescriptor ping = new ToolRegistry().GetById("PING")!;

        ServerItemViewModel recipeRow = (ServerItemViewModel)build.Invoke(palette, [DescriptorOf()])!;
        ServerItemViewModel builtInRow = (ServerItemViewModel)build.Invoke(palette, [ping])!;

        Assert.Equal("Probe - Recipe by Acme (unverified)", recipeRow.DisplayName);
        Assert.Equal("tool-rcp:acme.probe", recipeRow.Id);
        Assert.Equal(ping.ResolveLabel(localizer.GetString), builtInRow.DisplayName);
    }

    [Fact]
    public async Task TheDescriptionOfARecipeIsItsOwnTextAndNeverALocaleLookup()
    {
        LocalizationManager localizer = await LocalizerAsync();
        Recipe recipe = Recipe(id: "acme.desc") with { Description = "ToolDescPING <b>bold</b>" };
        ToolDescriptor descriptor = ToolRegistry.CreateRecipeDescriptor(recipe);
        ToolDescriptor ping = new ToolRegistry().GetById("PING")!;

        Assert.Equal("ToolDescPING <b>bold</b>", ToolsTabPopulationService.ResolveCardDescription(descriptor, localizer.GetString));
        Assert.NotEmpty(ToolsTabPopulationService.ResolveCardDescription(ping, localizer.GetString));
    }

    [Fact]
    public async Task TheGroupOfTheRecipeToolsIsHeadedByTheNameOfThePackInEveryLanguage()
    {
        Assert.Equal("Recipes", (await LocalizerAsync("en"))[RecipeToolText.GroupHeadingKey]);
        Assert.Equal("Recettes", (await LocalizerAsync("fr"))[RecipeToolText.GroupHeadingKey]);
        Assert.NotEqual(RecipeToolText.GroupHeadingKey, (await LocalizerAsync("es"))[RecipeToolText.GroupHeadingKey]);
    }

    [Fact]
    public async Task WhicheverRouteOpensARecipeToolTheTabSaysWhatItIs()
    {
        LocalizationManager localizer = await LocalizerAsync();
        ToolDescriptor recipe = DescriptorOf();
        ToolDescriptor ping = new ToolRegistry().GetById("PING")!;

        Assert.Equal("Probe - Recipe by Acme (unverified)", MainViewModel.TitleOfToolTab(recipe, "my own title", localizer.GetString));
        Assert.Equal("my own title", MainViewModel.TitleOfToolTab(ping, "my own title", localizer.GetString));
        Assert.Equal("kept", MainViewModel.TitleOfToolTab(null, "kept", localizer.GetString));
    }
}
