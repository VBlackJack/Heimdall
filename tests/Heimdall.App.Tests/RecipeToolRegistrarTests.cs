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
using static Heimdall.App.Tests.RecipeBuilders;

namespace Heimdall.App.Tests;

/// <summary>
/// The recipe tools follow the Recipes pack: registered at start when it is on and not forbidden,
/// removed when it is turned off, registered again when it is turned on while the application
/// runs, with one subscription and one registration each time.
/// </summary>
public sealed class RecipeToolRegistrarTests
{
    private sealed class Rig
    {
        private Action<AppSettings>? _settingsChanged;

        public Rig(bool packOn, bool forbidden = false, int recipeCount = 2)
        {
            Settings = new AppSettings { DisabledToolPacks = packOn ? [] : [nameof(ToolPack.Recipes)], RecipesPackMigrated = true };
            ToolPackPolicy policy = new(name => forbidden && name == "ToolPackRecipes" ? ToolPackPolicy.ForbiddenValue : null);
            Availability = new ToolAvailability(Registry, policy, () => Settings, handler => _settingsChanged = handler);
            for (int index = 0; index < recipeCount; index++)
            {
                Store.Recipes.Add(Loaded(Recipe(id: $"acme.tool{index}", name: $"Tool {index}"), $@"C:\Windows\System32\tool{index}.exe"));
            }

            Registrar = new RecipeToolRegistrar(
                Registry,
                Availability,
                Store,
                loaded =>
                {
                    ViewsRequested.Add(loaded);
                    return new ProbeView();
                },
                WorkFolders);
            Registry.RecipeToolsChanged += () => RegistrationEvents++;
        }

        public AppSettings Settings { get; }

        public ToolRegistry Registry { get; } = new();

        public FakeRecipeStore Store { get; } = new();

        public ToolAvailability Availability { get; }

        public RecipeToolRegistrar Registrar { get; }

        public RecordingWorkFolders WorkFolders { get; } = new();

        public List<LoadedRecipe> ViewsRequested { get; } = [];

        public int RegistrationEvents { get; private set; }

        public string[] RecipeIds => [.. Registry.All.Where(descriptor => descriptor.IsRecipe).Select(descriptor => descriptor.Id)];

        public void SetPack(bool on) => SetDisabled(on ? [] : [nameof(ToolPack.Recipes)]);

        public void SetDisabled(params string[] packs)
        {
            Settings.DisabledToolPacks = [.. packs];
            _settingsChanged!(Settings);
        }
    }

    private sealed class ProbeView : IToolView
    {
        public void Initialize(ToolContext? context, LocalizationManager? localizer)
        {
        }

        public void Dispose()
        {
        }
    }

    internal sealed class RecordingWorkFolders : IRecipeWorkFolders
    {
        public int Cleaned { get; private set; }

        public string Create() => throw new NotSupportedException();

        public void Delete(string path)
        {
        }

        public void CleanStale() => Cleaned++;
    }

    [Fact]
    public void WhenThePackIsOnTheToolsAreRegisteredAtStart()
    {
        Rig rig = new(packOn: true);

        rig.Registrar.Start();

        Assert.Equal(["RCP:acme.tool0", "RCP:acme.tool1"], rig.RecipeIds);
        Assert.True(rig.Registrar.IsListed);
        Assert.Equal(1, rig.Store.LoadCount);
        Assert.Equal(1, rig.RegistrationEvents);
    }

    [Fact]
    public void WhenThePackIsOffNothingIsReadAndNothingIsRegistered()
    {
        Rig rig = new(packOn: false);

        rig.Registrar.Start();

        Assert.Empty(rig.RecipeIds);
        Assert.False(rig.Registrar.IsListed);
        Assert.Equal(0, rig.Store.LoadCount);
        Assert.Equal(0, rig.RegistrationEvents);
    }

    [Fact]
    public void WhenThePolicyForbidsThePackNothingIsReadAndNothingIsRegisteredWhateverTheUserChose()
    {
        Rig rig = new(packOn: true, forbidden: true);

        rig.Registrar.Start();
        rig.SetPack(on: true);

        Assert.Empty(rig.RecipeIds);
        Assert.Equal(0, rig.Store.LoadCount);
    }

    [Fact]
    public void TurningThePackOnWhileTheApplicationRunsRegistersTheTools()
    {
        Rig rig = new(packOn: false);
        rig.Registrar.Start();

        rig.SetPack(on: true);

        Assert.Equal(["RCP:acme.tool0", "RCP:acme.tool1"], rig.RecipeIds);
        Assert.Equal(1, rig.Store.LoadCount);
        Assert.Equal(1, rig.RegistrationEvents);
    }

    [Fact]
    public void TurningThePackOffRemovesTheToolsAndAnnouncesIt()
    {
        Rig rig = new(packOn: true);
        rig.Registrar.Start();

        rig.SetPack(on: false);

        Assert.Empty(rig.RecipeIds);
        Assert.False(rig.Registrar.IsListed);
        Assert.Equal(2, rig.RegistrationEvents);
        Assert.Equal(ToolAvailabilityState.Unknown, rig.Availability.GetState("RCP:acme.tool0"));
    }

    [Fact]
    public void TurningItOffAndOnAgainRegistersWhatTheStoreHoldsNow()
    {
        Rig rig = new(packOn: true);
        rig.Registrar.Start();
        rig.SetPack(on: false);
        rig.Store.Recipes.RemoveAt(1);

        rig.SetPack(on: true);

        Assert.Equal(["RCP:acme.tool0"], rig.RecipeIds);
        Assert.Equal(2, rig.Store.LoadCount);
    }

    [Fact]
    public void StartingTwiceDoesNotSubscribeOrRegisterTwice()
    {
        Rig rig = new(packOn: false);
        rig.Registrar.Start();
        rig.Registrar.Start();

        rig.SetPack(on: true);

        Assert.Equal(1, rig.Store.LoadCount);
        Assert.Equal(1, rig.RegistrationEvents);
        Assert.Equal(1, rig.WorkFolders.Cleaned);
    }

    [Fact]
    public void AChangeThatLeavesThePackOnDoesNotReadTheStoreAgain()
    {
        Rig rig = new(packOn: true);
        rig.Registrar.Start();

        // Another pack is turned off: the availability service raises its event, the Recipes pack is unchanged.
        int availabilityEvents = 0;
        rig.Availability.Changed += () => availabilityEvents++;
        rig.SetDisabled(nameof(ToolPack.Audit));

        Assert.Equal(1, availabilityEvents);
        Assert.Equal(1, rig.Store.LoadCount);
        Assert.Equal(1, rig.RegistrationEvents);
        Assert.Equal(2, rig.RecipeIds.Length);
    }

    [Fact]
    public void AfterDisposeThePackIsNoLongerFollowed()
    {
        Rig rig = new(packOn: true);
        rig.Registrar.Start();

        rig.Registrar.Dispose();
        rig.SetPack(on: false);

        Assert.Equal(2, rig.RecipeIds.Length);
        Assert.Equal(1, rig.RegistrationEvents);
    }

    [Fact]
    public void ReloadReadsTheStoreAgainWhileThePackIsOnAndDoesNothingWhileItIsOff()
    {
        Rig on = new(packOn: true);
        on.Registrar.Start();
        on.Store.Recipes.Add(Loaded(Recipe(id: "acme.late", name: "Late")));
        on.Registrar.Reload();
        Assert.Equal(3, on.RecipeIds.Length);

        Rig off = new(packOn: false);
        off.Registrar.Start();
        off.Registrar.Reload();
        Assert.Empty(off.RecipeIds);
        Assert.Equal(0, off.Store.LoadCount);
    }

    [Fact]
    public void TheViewOfARecipeIsBuiltFromItsStoredEntryThroughTheRegistry()
    {
        Rig rig = new(packOn: true);
        rig.Registrar.Start();

        IToolView view = rig.Registry.CreateView("RCP:acme.tool1");

        Assert.IsType<ProbeView>(view);
        LoadedRecipe requested = Assert.Single(rig.ViewsRequested);
        Assert.Equal("acme.tool1", requested.Recipe.Id);
        Assert.Equal(@"C:\Windows\System32\tool1.exe", requested.Entry.ResolvedProgramPath);
    }

    [Fact]
    public void ARecipeToolOfAPackThatIsOnIsAvailableAndOneOfAPackTurnedOffIsNotListed()
    {
        Rig rig = new(packOn: true);
        rig.Registrar.Start();
        Assert.Equal(ToolAvailabilityState.Available, rig.Availability.GetState("RCP:acme.tool0"));
        Assert.True(rig.Availability.IsListed("RCP:acme.tool0"));

        rig.SetPack(on: false);

        Assert.False(rig.Availability.IsListed("RCP:acme.tool0"));
    }

    [Fact]
    public void AFailingStoreDoesNotBringTheSettingsChangeDown()
    {
        Rig rig = new(packOn: false);
        rig.Store.Recipes.Clear();
        RecipeToolRegistrar failing = new(
            rig.Registry,
            rig.Availability,
            new ThrowingStore(),
            _ => new ProbeView());
        failing.Start();

        rig.SetPack(on: true);

        Assert.Empty(rig.RecipeIds);
        Assert.False(failing.IsListed);
    }

    private sealed class ThrowingStore : IRecipeStore
    {
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public RecipeStoreEntry? Current(string id) => null;

        public RecipeLoadResult LoadAll() => throw new IOException("disk");

        public IReadOnlyList<RecipeStoreEntry> List() => [];

        public RecipeStoreResult Add(byte[] bytes, string resolvedProgramPath) => throw new NotSupportedException();

        public RecipeStoreResult Replace(byte[] bytes, string resolvedProgramPath) => throw new NotSupportedException();

        public RecipeStoreResult Remove(string id) => throw new NotSupportedException();

        public byte[]? ReadOriginalBytes(string id) => null;
    }
}
