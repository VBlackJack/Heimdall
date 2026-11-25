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

using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

/// <summary>
/// The one-time step that keeps the Recipes pack off for an install that predates it: once only,
/// never again after the user turned the pack on, and never a write to a settings file that comes
/// from a newer schema.
/// </summary>
public sealed class ToolPackMigrationTests : IDisposable
{
    private const string MarkerJsonName = "recipesPackMigrated";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "heimdall-tool-pack-migration-tests", Guid.NewGuid().ToString("N"));

    public ToolPackMigrationTests() => Directory.CreateDirectory(Path.Combine(_root, "config"));

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

    private string SettingsFile => Path.Combine(_root, "config", "settings.json");

    [Fact]
    public void TheMarkerIsOffOnANewSettingsObject()
        => Assert.False(new AppSettings().RecipesPackMigrated);

    [Fact]
    public void TheStepListsThePackAndSetsTheMarker()
    {
        AppSettings settings = new() { DisabledToolPacks = [nameof(ToolPack.Audit)] };

        bool changed = ToolPackMigration.DisableRecipesPackOnce(settings);

        Assert.True(changed);
        Assert.Equal([nameof(ToolPack.Audit), nameof(ToolPack.Recipes)], settings.DisabledToolPacks);
        Assert.True(settings.RecipesPackMigrated);
    }

    [Fact]
    public void ThePackIsNotListedTwiceWhateverTheCaseOrPadding()
    {
        AppSettings settings = new() { DisabledToolPacks = [" recipes "] };

        ToolPackMigration.DisableRecipesPackOnce(settings);

        Assert.Equal([" recipes "], settings.DisabledToolPacks);
        Assert.True(settings.RecipesPackMigrated);
    }

    [Fact]
    public void WithTheMarkerSetTheStepDoesNothing()
    {
        AppSettings settings = new() { RecipesPackMigrated = true };

        bool changed = ToolPackMigration.DisableRecipesPackOnce(settings);

        Assert.False(changed);
        Assert.Empty(settings.DisabledToolPacks);
    }

    [Fact]
    public async Task AnOlderSettingsFileGetsThePackListedAndTheMarkerWrittenOnce()
    {
        await File.WriteAllTextAsync(SettingsFile, "{ \"schemaVersion\": 1, \"defaultLocale\": \"fr\", \"disabledToolPacks\": [\"Audit\"] }");
        ConfigManager manager = new(_root);

        bool first = await ToolPackMigration.RunAsync(manager);
        AppSettings afterFirst = await manager.LoadSettingsAsync();
        bool second = await ToolPackMigration.RunAsync(manager);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal([nameof(ToolPack.Audit), nameof(ToolPack.Recipes)], afterFirst.DisabledToolPacks);
        Assert.True(afterFirst.RecipesPackMigrated);
        Assert.Equal("fr", afterFirst.DefaultLocale);
        Assert.Contains($"\"{MarkerJsonName}\": true", await File.ReadAllTextAsync(SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePackIsNotTurnedOffAgainAfterTheUserTurnedItOn()
    {
        await File.WriteAllTextAsync(SettingsFile, "{ \"schemaVersion\": 1 }");
        ConfigManager manager = new(_root);
        await ToolPackMigration.RunAsync(manager);

        // The user turns the pack on in Settings and saves.
        await manager.MergeSettingAsync(settings =>
            settings.DisabledToolPacks.RemoveAll(name => name == nameof(ToolPack.Recipes)));
        bool rerun = await ToolPackMigration.RunAsync(manager);
        AppSettings reloaded = await manager.LoadSettingsAsync();

        Assert.False(rerun);
        Assert.DoesNotContain(nameof(ToolPack.Recipes), reloaded.DisabledToolPacks);
        Assert.True(reloaded.RecipesPackMigrated);
    }

    [Fact]
    public async Task ASettingsFileFromANewerSchemaIsNeverWritten()
    {
        const string newer = "{ \"schemaVersion\": 99, \"disabledToolPacks\": [] }";
        await File.WriteAllTextAsync(SettingsFile, newer);
        byte[] before = await File.ReadAllBytesAsync(SettingsFile);
        ConfigManager manager = new(_root);

        bool written = await ToolPackMigration.RunAsync(manager);

        Assert.False(written);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsFile));
    }

    [Fact]
    public async Task APresentMarkerMeansNoWriteAtAll()
    {
        await File.WriteAllTextAsync(SettingsFile, $"{{ \"schemaVersion\": 1, \"{MarkerJsonName}\": true }}");
        byte[] before = await File.ReadAllBytesAsync(SettingsFile);
        ConfigManager manager = new(_root);

        bool written = await ToolPackMigration.RunAsync(manager);

        Assert.False(written);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsFile));
    }

    [Fact]
    public async Task LoadingASettingsFileNeverRunsTheStep()
    {
        await File.WriteAllTextAsync(SettingsFile, "{ \"schemaVersion\": 1 }");
        ConfigManager manager = new(_root);

        AppSettings loaded = await manager.LoadSettingsAsync();

        Assert.Empty(loaded.DisabledToolPacks);
        Assert.False(loaded.RecipesPackMigrated);
    }

    [Fact]
    public async Task AKeyThisBuildDoesNotKnowSurvivesASaveLikeTheMarkerDoesForAnOlderBuild()
    {
        // An older build has no property for the marker: it lands in the extension data, and the
        // settings writer merges the extension data of the file back in. The same path is used here
        // with a key this build does not know, which stands in for the marker as an older build sees it.
        await File.WriteAllTextAsync(
            SettingsFile,
            "{ \"schemaVersion\": 1, \"futureMarker\": true, \"disabledToolPacks\": [\"Recipes\"] }");
        ConfigManager manager = new(_root);

        AppSettings loaded = await manager.LoadSettingsAsync();
        loaded.DefaultLocale = "es";
        await manager.SaveSettingsAsync(loaded);

        using JsonDocument saved = JsonDocument.Parse(await File.ReadAllTextAsync(SettingsFile));
        Assert.True(saved.RootElement.GetProperty("futureMarker").GetBoolean());
        Assert.Equal("Recipes", saved.RootElement.GetProperty("disabledToolPacks")[0].GetString());
    }

    [Fact]
    public async Task ANameOfAPackThisBuildDoesNotKnowSurvivesASave()
    {
        // An older build ignores the name "Recipes" when it enforces the list, but never removes it
        // from the file: a list holding a name the build does not know round-trips with it in place.
        await File.WriteAllTextAsync(
            SettingsFile,
            "{ \"schemaVersion\": 1, \"disabledToolPacks\": [\"Audit\", \"SomePackOfALaterBuild\"] }");
        ConfigManager manager = new(_root);

        AppSettings loaded = await manager.LoadSettingsAsync();
        await manager.SaveSettingsAsync(loaded);

        Assert.Equal(
            [nameof(ToolPack.Audit), "SomePackOfALaterBuild"],
            (await manager.LoadSettingsAsync()).DisabledToolPacks);
    }

    [Fact]
    public async Task ANewerShapeFileWithTheMarkerRoundTrips()
    {
        await File.WriteAllTextAsync(
            SettingsFile,
            $"{{ \"schemaVersion\": 1, \"{MarkerJsonName}\": true, \"disabledToolPacks\": [\"Audit\"] }}");
        ConfigManager manager = new(_root);

        AppSettings loaded = await manager.LoadSettingsAsync();
        await manager.SaveSettingsAsync(loaded);
        AppSettings reloaded = await manager.LoadSettingsAsync();

        Assert.True(reloaded.RecipesPackMigrated);
        Assert.Equal([nameof(ToolPack.Audit)], reloaded.DisabledToolPacks);
    }
}
