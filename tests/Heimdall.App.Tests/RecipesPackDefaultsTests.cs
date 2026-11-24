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
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The Recipes pack is off by default on a fresh install (the shipped defaults) and, through the
/// one-time step, on an existing install; in both cases the step does not run a second time.
/// </summary>
public sealed class RecipesPackDefaultsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "heimdall-recipes-pack-defaults-tests", Guid.NewGuid().ToString("N"));

    public RecipesPackDefaultsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    [Fact]
    public async Task AFreshInstallStartsWithTheRecipesPackOffAndTheStepAlreadyRecorded()
    {
        ConfigManager manager = new(AppContext.BaseDirectory, Path.Combine(_root, "data"));
        await manager.InitializeAsync();

        AppSettings loaded = await manager.LoadSettingsAsync();
        byte[] before = await File.ReadAllBytesAsync(manager.SettingsPath);
        bool written = await ToolPackMigration.RunAsync(manager);

        Assert.Equal([nameof(ToolPack.Audit), nameof(ToolPack.Recipes)], loaded.DisabledToolPacks);
        Assert.True(loaded.RecipesPackMigrated);
        Assert.False(written);
        Assert.Equal(before, await File.ReadAllBytesAsync(manager.SettingsPath));
    }

    [Fact]
    public async Task ARecipeToolOfAFreshInstallIsOffUntilTheUserTurnsThePackOn()
    {
        ConfigManager manager = new(AppContext.BaseDirectory, Path.Combine(_root, "data"));
        await manager.InitializeAsync();
        AppSettings loaded = await manager.LoadSettingsAsync();
        ToolRegistry registry = new();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.ping")], _ => throw new InvalidOperationException("never built"));

        ToolAvailability off = new(registry, new ToolPackPolicy(_ => null), () => loaded, _ => { });
        loaded.DisabledToolPacks.Remove(nameof(ToolPack.Recipes));
        ToolAvailability on = new(registry, new ToolPackPolicy(_ => null), () => loaded, _ => { });

        Assert.Equal(ToolAvailabilityState.DisabledByUser, off.GetState("RCP:acme.ping"));
        Assert.Equal(ToolAvailabilityState.Available, on.GetState("RCP:acme.ping"));
    }

    [Fact]
    public async Task AnExistingInstallOfTheLastBuildGetsTheRecipesPackOffAtTheNextStart()
    {
        // The settings an install of the previous build holds: no marker, no Recipes entry.
        string configDirectory = Path.Combine(_root, "existing", "config");
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(configDirectory, "settings.json"),
            "{ \"schemaVersion\": 1, \"defaultLocale\": \"fr\", \"disabledToolPacks\": [\"Audit\"] }");
        ConfigManager manager = new(Path.Combine(_root, "existing"));

        AppSettings beforeStep = await manager.LoadSettingsAsync();
        bool first = await ToolPackMigration.RunAsync(manager);
        AppSettings afterStep = await manager.LoadSettingsAsync();
        bool second = await ToolPackMigration.RunAsync(manager);

        Assert.DoesNotContain(nameof(ToolPack.Recipes), beforeStep.DisabledToolPacks);
        Assert.True(first);
        Assert.False(second);
        Assert.Equal([nameof(ToolPack.Audit), nameof(ToolPack.Recipes)], afterStep.DisabledToolPacks);
        Assert.Equal("fr", afterStep.DefaultLocale);
    }
}
