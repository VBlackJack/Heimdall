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
/// How the disabled packs are stored: an existing settings file that predates the key keeps every
/// tool on, and a fresh install, which copies the shipped defaults, starts with the Audit pack
/// off. Both go through the application's own configuration manager, so its serializer options
/// are the ones under test.
/// </summary>
public sealed class ToolPackSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "heimdall-tool-pack-settings-tests", Guid.NewGuid().ToString("N"));

    public ToolPackSettingsTests() => Directory.CreateDirectory(_root);

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
    public void ANewSettingsObjectDisablesNothing()
        => Assert.Empty(new AppSettings().DisabledToolPacks);

    [Fact]
    public async Task ASettingsFileWithoutTheKeyLoadsAsAnEmptyListAndDisablesNothing()
    {
        // The shape of a settings file written by a build that predates the key.
        string configDirectory = Path.Combine(_root, "config");
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(configDirectory, "settings.json"),
            "{ \"schemaVersion\": 1, \"defaultLocale\": \"fr\" }");

        AppSettings loaded = await new ConfigManager(_root).LoadSettingsAsync();

        Assert.Equal("fr", loaded.DefaultLocale);
        Assert.NotNull(loaded.DisabledToolPacks);
        Assert.Empty(loaded.DisabledToolPacks);
        ToolRegistry registry = new();
        ToolAvailability availability = new(registry, new ToolPackPolicy(_ => null), () => loaded, _ => { });
        Assert.All(registry.All, descriptor => Assert.Equal(ToolAvailabilityState.Available, availability.GetState(descriptor.Id)));
    }

    [Fact]
    public async Task TheDisabledPacksSurviveASaveAndALoad()
    {
        ConfigManager manager = new(_root);

        await manager.SaveSettingsAsync(new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit), nameof(ToolPack.Administration)] });
        AppSettings loaded = await manager.LoadSettingsAsync();

        Assert.Equal([nameof(ToolPack.Audit), nameof(ToolPack.Administration)], loaded.DisabledToolPacks);
    }

    [Fact]
    public async Task AFreshInstallDisablesExactlyTheAuditAndRecipesPacks()
    {
        string installRoot = AppContext.BaseDirectory;
        string dataRoot = Path.Combine(_root, "data");
        Assert.True(
            File.Exists(Path.Combine(installRoot, "config", "settings.default.json")),
            "settings.default.json is not next to the test binaries");
        ConfigManager manager = new(installRoot, dataRoot);

        await manager.InitializeAsync();
        AppSettings loaded = await manager.LoadSettingsAsync();

        Assert.Equal([nameof(ToolPack.Audit), nameof(ToolPack.Recipes)], loaded.DisabledToolPacks);
        ToolRegistry registry = new();
        ToolAvailability availability = new(registry, new ToolPackPolicy(_ => null), () => loaded, _ => { });
        string[] disabledIds = registry.All
            .Where(descriptor => availability.GetState(descriptor.Id) == ToolAvailabilityState.DisabledByUser)
            .Select(descriptor => descriptor.Id)
            .ToArray();
        string[] auditIds = registry.All
            .Where(descriptor => descriptor.Pack == ToolPack.Audit)
            .Select(descriptor => descriptor.Id)
            .ToArray();
        Assert.Equal(10, disabledIds.Length);
        Assert.Equal(auditIds.Order(StringComparer.Ordinal), disabledIds.Order(StringComparer.Ordinal));
    }
}
