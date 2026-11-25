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

using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The Recipes pack counts as off until the one-time step ran or the user chose, whatever the list
/// of switched-off packs says, so a step that failed or was skipped never leaves it on.
/// </summary>
public sealed class RecipesPackFailClosedTests
{
    private const string RecipeToolId = "RCP:acme.ping";

    private static ToolRegistry RegistryWithARecipe()
    {
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], _ => throw new InvalidOperationException("never built"));
        return registry;
    }

    private static ToolAvailability AvailabilityOver(AppSettings? settings, ToolRegistry? registry = null)
        => new(registry ?? RegistryWithARecipe(), new ToolPackPolicy(_ => null), () => settings, _ => { });

    [Fact]
    public void WithoutTheMarkerThePackIsOffEvenWhenTheListDoesNotNameIt()
    {
        ToolAvailability availability = AvailabilityOver(new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit)] });

        Assert.False(availability.IsPackEnabled(ToolPack.Recipes));
        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(RecipeToolId));
    }

    [Fact]
    public void BeforeTheSettingsExistThePackIsOff()
    {
        ToolAvailability availability = AvailabilityOver(settings: null);

        Assert.False(availability.IsPackEnabled(ToolPack.Recipes));
        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(RecipeToolId));
    }

    [Fact]
    public void WithTheMarkerTheListDecides()
    {
        ToolAvailability on = AvailabilityOver(new AppSettings { RecipesPackMigrated = true });
        ToolAvailability off = AvailabilityOver(new AppSettings { RecipesPackMigrated = true, DisabledToolPacks = [nameof(ToolPack.Recipes)] });

        Assert.True(on.IsPackEnabled(ToolPack.Recipes));
        Assert.Equal(ToolAvailabilityState.Available, on.GetState(RecipeToolId));
        Assert.False(off.IsPackEnabled(ToolPack.Recipes));
    }

    [Fact]
    public void TheOtherPacksFollowTheirListWhateverTheMarkerSays()
    {
        ToolAvailability availability = AvailabilityOver(new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit)] });

        Assert.False(availability.IsPackEnabled(ToolPack.Audit));
        Assert.True(availability.IsPackEnabled(ToolPack.Essentials));
        Assert.True(availability.IsPackEnabled(ToolPack.NetworkDiagnostics));
        Assert.True(availability.IsPackEnabled(ToolPack.Administration));
        Assert.True(availability.IsPackEnabled(ToolPack.Core));
    }

    [Fact]
    public void SettingTheMarkerTurnsThePackOnAndAnnouncesIt()
    {
        AppSettings settings = new();
        Action<AppSettings>? changed = null;
        ToolAvailability availability = new(
            RegistryWithARecipe(),
            new ToolPackPolicy(_ => null),
            () => settings,
            handler => changed = handler);
        int announced = 0;
        availability.Changed += () => announced++;
        Assert.False(availability.IsPackEnabled(ToolPack.Recipes));

        settings.RecipesPackMigrated = true;
        changed!(settings);

        Assert.True(availability.IsPackEnabled(ToolPack.Recipes));
        Assert.Equal(1, announced);
    }

    [Fact]
    public void ThePolicyStillBeatsAPackTheUserTurnedOn()
    {
        ToolAvailability availability = new(
            RegistryWithARecipe(),
            new ToolPackPolicy(name => name == "ToolPackRecipes" ? ToolPackPolicy.ForbiddenValue : null),
            () => new AppSettings { RecipesPackMigrated = true },
            _ => { });

        Assert.Equal(ToolAvailabilityState.ForbiddenByPolicy, availability.GetState(RecipeToolId));
    }
}
