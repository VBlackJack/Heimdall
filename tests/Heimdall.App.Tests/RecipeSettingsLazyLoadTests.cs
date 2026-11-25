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

using Heimdall.App.ViewModels.Settings;

namespace Heimdall.App.Tests;

/// <summary>
/// An application start must not load and hash every recipe file: the list is read the first time
/// the Recipes card is shown, and after an import, a replacement or a removal.
/// </summary>
public sealed class RecipeSettingsLazyLoadTests
{
    [Fact]
    public async Task ConstructingTheViewModelTouchesTheStoreZeroTimes()
    {
        using RecipeLazyRig rig = await RecipeLazyRig.CreateAsync();

        Assert.Equal(0, rig.Store.LoadCount);
        Assert.Equal(0, rig.Store.ListCount);
        Assert.Empty(rig.ViewModel.Items);
    }

    [Fact]
    public async Task TheFirstActivationReadsTheStoreOnceAndFillsTheList()
    {
        using RecipeLazyRig rig = await RecipeLazyRig.CreateAsync();

        rig.ViewModel.EnsureLoadedCommand.Execute(null);

        Assert.Equal(1, rig.Store.LoadCount);
        Assert.Equal(1, rig.Store.ListCount);
        ImportedRecipeItemViewModel item = Assert.Single(rig.ViewModel.Items);
        Assert.Equal("One", item.Name);
    }

    [Fact]
    public async Task ShowingTheCardAgainDoesNotReadAgain()
    {
        using RecipeLazyRig rig = await RecipeLazyRig.CreateAsync();

        rig.ViewModel.EnsureLoadedCommand.Execute(null);
        rig.ViewModel.EnsureLoadedCommand.Execute(null);
        rig.ViewModel.EnsureLoaded();

        Assert.Equal(1, rig.Store.LoadCount);
    }

    [Fact]
    public async Task RewordingBeforeTheCardWasShownReadsNothingAndAfterwardsRebuildsTheList()
    {
        using RecipeLazyRig rig = await RecipeLazyRig.CreateAsync();

        rig.ViewModel.RefreshIfLoaded();
        Assert.Equal(0, rig.Store.LoadCount);

        rig.ViewModel.EnsureLoaded();
        rig.ViewModel.RefreshIfLoaded();
        Assert.Equal(2, rig.Store.LoadCount);
    }

    [Fact]
    public async Task AnExplicitRefreshAlwaysReads()
    {
        using RecipeLazyRig rig = await RecipeLazyRig.CreateAsync();

        rig.ViewModel.Refresh();
        rig.ViewModel.Refresh();

        Assert.Equal(2, rig.Store.LoadCount);
    }

    [Fact]
    public async Task ADisposedViewModelDoesNotLoad()
    {
        using RecipeLazyRig rig = await RecipeLazyRig.CreateAsync();
        rig.ViewModel.Dispose();

        rig.ViewModel.EnsureLoaded();

        Assert.Equal(0, rig.Store.LoadCount);
    }
}
