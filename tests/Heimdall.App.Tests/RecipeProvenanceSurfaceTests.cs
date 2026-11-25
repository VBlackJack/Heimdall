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
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// Wherever a recipe is named, its author is named with it and marked as unverified, and its
/// description is the text of the file: the picker entry, the card tooltip and the words of a refusal.
/// </summary>
public sealed class RecipeProvenanceSurfaceTests
{
    private const string TitleOfTheTestRecipe = "Ping - Unverified recipe by Acme";

    private static async Task<LocalizationManager> EnglishAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), "en");
        return localizer;
    }

    private static ToolDescriptor RecipeTool(string name = "Ping")
        => ToolRegistry.CreateRecipeDescriptor(RecipeTestFiles.Recipe("acme.ping", name));

    [Fact]
    public async Task ThePickerEntryOfARecipeIsNamedLikeItsTab_AndThatIsTheNameOfTheSavedEntry()
    {
        LocalizationManager localizer = await EnglishAsync();

        string name = ToolPickerText.DisplayName(RecipeTool(), localizer.GetString);

        Assert.Equal(TitleOfTheTestRecipe, name);
        Assert.Equal(RecipeToolText.TabTitle(RecipeTool(), localizer.GetString), name);
    }

    [Fact]
    public async Task TheDescriptionOfARecipeInThePickerIsTheFilesTextAndIsNeverLookedUpAsAKey()
    {
        LocalizationManager localizer = await EnglishAsync();
        List<string> looked = [];

        string description = ToolPickerText.Description(RecipeTool(), key =>
        {
            looked.Add(key);
            return localizer.GetString(key);
        });

        Assert.Equal("Ping the selected host.", description);
        Assert.Empty(looked);
    }

    [Fact]
    public async Task ARecipeNamedLikeALocaleKeyKeepsItsOwnNameInThePicker()
    {
        LocalizationManager localizer = await EnglishAsync();
        Assert.True(localizer.HasKey("BtnCancel"));

        string name = ToolPickerText.DisplayName(RecipeTool(name: "BtnCancel"), localizer.GetString);

        Assert.Equal("BtnCancel - Unverified recipe by Acme", name);
    }

    [Fact]
    public async Task ABuiltInToolKeepsItsLocalizedNameAndDescriptionInThePicker()
    {
        LocalizationManager localizer = await EnglishAsync();
        ToolDescriptor ping = new ToolRegistry().GetById("PING")!;

        Assert.Equal(ping.ResolveLabel(localizer.GetString), ToolPickerText.DisplayName(ping, localizer.GetString));
        Assert.Equal(
            localizer.HasKey("ToolDescPING") ? localizer["ToolDescPING"] : string.Empty,
            ToolPickerText.Description(ping, localizer.GetString));
    }

    [Fact]
    public async Task TheTooltipOfARecipeCardNamesTheAuthorThenGivesTheDescription()
    {
        LocalizationManager localizer = await EnglishAsync();

        string? tooltip = ToolsTabPopulationService.ResolveCardToolTip(RecipeTool(), localizer.GetString);

        Assert.Equal(
            "Unverified recipe by Acme" + Environment.NewLine + "Ping the selected host.",
            tooltip);
    }

    [Fact]
    public async Task TheTooltipOfABuiltInCardIsStillItsDescriptionOnly()
    {
        LocalizationManager localizer = await EnglishAsync();
        ToolDescriptor ping = new ToolRegistry().GetById("PING")!;

        string? tooltip = ToolsTabPopulationService.ResolveCardToolTip(ping, localizer.GetString);

        Assert.Equal(ToolsTabPopulationService.ResolveCardDescription(ping, localizer.GetString) is { Length: > 0 } text ? text : null, tooltip);
        Assert.DoesNotContain("Unverified", tooltip ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecipeIsNamedWithItsAuthorInTheWordsOfARefusal()
    {
        LocalizationManager localizer = await EnglishAsync();

        string refusal = localizer.Format(
            ToolUnavailableException.MessageKeyFor(ToolAvailabilityState.DisabledByUser),
            RecipeToolText.TabTitle(RecipeTool(), localizer.GetString));

        Assert.Contains(TitleOfTheTestRecipe, refusal, StringComparison.Ordinal);
    }
}

public sealed partial class SessionCoordinatorPreMountTests
{
    private const string RecipeToolId = "RCP:acme.ping";

    private static string RecipeLabel(LocalizationManager localizer)
        => RecipeToolText.TabTitle(ToolRegistry.CreateRecipeDescriptor(RecipeTestFiles.Recipe("acme.ping", "Ping")), localizer.GetString);

    [Theory]
    [InlineData(ToolAvailabilityState.DisabledByUser, ToolUnavailableException.DisabledByUserMessageKey)]
    [InlineData(ToolAvailabilityState.ForbiddenByPolicy, ToolUnavailableException.ForbiddenByPolicyMessageKey)]
    public async Task OpenToolTabAsync_RefusedRecipe_NamesItWithItsAuthor(ToolAvailabilityState state, string expectedKey)
    {
        ScriptedToolAvailability availability = new();
        availability.Set(RecipeToolId, state);
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        harness.Main.ToolRegistry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping", "Ping")], _ => throw new InvalidOperationException("never built"));
        LocalizationManager localizer = await LoadEnglishAsync();

        ToolOpenOutcome outcome = await harness.Main.OpenToolTabAsync(RecipeToolId, "Ping", null);

        Assert.Equal(ToolOpenOutcome.Refused, outcome);
        Assert.Equal(localizer.Format(expectedKey, RecipeLabel(localizer)), harness.Main.StatusText);
        Assert.Contains("Unverified recipe by Acme", harness.Main.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ToolAvailabilityState.DisabledByUser, ToolUnavailableException.DisabledByUserMessageKey)]
    [InlineData(ToolAvailabilityState.ForbiddenByPolicy, ToolUnavailableException.ForbiddenByPolicyMessageKey)]
    public async Task SplitSessionWithTool_RefusedRecipe_NamesItWithItsAuthor(ToolAvailabilityState state, string expectedKey)
    {
        ScriptedToolAvailability availability = new();
        availability.Set(RecipeToolId, state);
        string rootPath = CreateTempRoot("heimdall-split-recipe-refusal-");
        try
        {
            LocalizationManager localizer = await LoadEnglishAsync();
            FakeEmbeddedSessionManager sessionManager = new();
            using TunnelManager tunnelManager = new();
            ToolRegistry registry = new();
            registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping", "Ping")], _ => throw new InvalidOperationException("never built"));
            SplitService splitService = new(
                new ConfigManager(rootPath),
                localizer,
                new ConnectionStateMachine(),
                tunnelManager,
                sessionManager,
                connectionService: null!,
                registry,
                dialogService: null!,
                new PaneCloseArbiter(),
                availability);
            string? status = null;
            splitService.SetStatusText = text => status = text;
            SessionTabViewModel session = new();
            session.RootContent = new SessionPaneModel { ConnectionType = "SSH" };

            bool split = splitService.SplitSessionWithTool(session, RecipeToolId, SplitOrientation.Vertical);

            Assert.False(split);
            Assert.Equal(localizer.Format(expectedKey, RecipeLabel(localizer)), status);
            Assert.Contains("Unverified recipe by Acme", status, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(rootPath);
        }
    }
}

public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task TheDetailPaneOfASavedRecipeEntryNamesTheAuthorAndShowsTheFilesDescription()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        ToolDescriptor recipe = ToolRegistry.CreateRecipeDescriptor(RecipeTestFiles.Recipe("acme.ping", "Ping"));
        fixture.ViewModel.ToolDescriptorResolver = id => id == recipe.Id ? recipe : null;
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateToolEntry("tool", "My ping", recipe.Id));

        fixture.ViewModel.SelectSingle(fixture.ServerById("tool"));

        Assert.Equal("Ping - Unverified recipe by Acme", fixture.ViewModel.ToolDetailName);
        Assert.Equal("Ping the selected host.", fixture.ViewModel.ToolDetailDescription);
    }
}
