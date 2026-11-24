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

using System.Text.Json.Nodes;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Importing a settings file replaces the list of switched-off packs as a whole, and a file written
/// before the Recipes pack existed lists only the packs of its day: it must not turn the pack on.
/// Only a file that carries the marker and does not list the pack, whose owner turned it on, does,
/// and the preview then says so as a line of its own.
/// </summary>
public sealed class RecipesPackSettingsImportTests
{
    private const string Profile = @"C:\Users\someone";

    private static JsonObject FileOf(string[] disabled, bool? marker = null, string? keyOfMarker = null)
    {
        JsonObject settings = new() { [nameof(AppSettings.DisabledToolPacks)] = new JsonArray([.. disabled.Select(name => (JsonNode?)JsonValue.Create(name))]) };
        if (marker is { } value)
        {
            settings[keyOfMarker ?? nameof(AppSettings.RecipesPackMigrated)] = value;
        }

        return new JsonObject
        {
            [SettingsTransfer.FormatProperty] = SettingsTransfer.FormatName,
            [SettingsTransfer.VersionProperty] = SettingsTransfer.FormatVersion,
            [SettingsTransfer.SettingsProperty] = settings,
        };
    }

    private static AppSettings PackOff(params string[] others)
        => new() { RecipesPackMigrated = true, DisabledToolPacks = [.. others, nameof(ToolPack.Recipes)] };

    [Fact]
    public void AFileWrittenBeforeThePackExistedLeavesThePackOff()
    {
        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) =
            SettingsTransfer.Import(PackOff(nameof(ToolPack.Audit)), FileOf([nameof(ToolPack.Audit)]));

        Assert.Contains(nameof(ToolPack.Recipes), merged.DisabledToolPacks);
        Assert.DoesNotContain(changed, change => change.Key == SettingsTransfer.RecipesPackTurnedOnKey);
        Assert.Empty(changed);
    }

    [Fact]
    public void AnOldFileStillTurnsTheOtherPacksOnAndOffAsItsListSays()
    {
        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) =
            SettingsTransfer.Import(PackOff(nameof(ToolPack.Audit)), FileOf([nameof(ToolPack.Administration)]));

        Assert.Equal([nameof(ToolPack.Administration), nameof(ToolPack.Recipes)], merged.DisabledToolPacks);
        Assert.DoesNotContain(nameof(ToolPack.Audit), merged.DisabledToolPacks);
        Assert.DoesNotContain(changed, change => change.Key == SettingsTransfer.RecipesPackTurnedOnKey);
        Assert.Contains(changed, change => change.Key == nameof(AppSettings.DisabledToolPacks));
    }

    [Fact]
    public void AnOldFileThatClearsTheListStillLeavesThePackOff()
    {
        (AppSettings merged, _) = SettingsTransfer.Import(PackOff(nameof(ToolPack.Audit)), FileOf([]));

        Assert.Equal([nameof(ToolPack.Recipes)], merged.DisabledToolPacks);
    }

    [Fact]
    public void AFileFromThisVersionWhoseOwnerHadThePackOffKeepsItOff()
    {
        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(
            PackOff(),
            FileOf([nameof(ToolPack.Audit), nameof(ToolPack.Recipes)], marker: true));

        Assert.Contains(nameof(ToolPack.Recipes), merged.DisabledToolPacks);
        Assert.DoesNotContain(changed, change => change.Key == SettingsTransfer.RecipesPackTurnedOnKey);
    }

    [Fact]
    public void AFileFromThisVersionWhoseOwnerHadThePackOnTurnsItOnAndSaysSoAsALineOfItsOwn()
    {
        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(
            PackOff(nameof(ToolPack.Audit)),
            FileOf([nameof(ToolPack.Audit)], marker: true));

        Assert.DoesNotContain(nameof(ToolPack.Recipes), merged.DisabledToolPacks);
        Assert.True(merged.RecipesPackMigrated);
        SettingsTransferChange notice = Assert.Single(changed, change => change.Key == SettingsTransfer.RecipesPackTurnedOnKey);
        Assert.True(notice.After!.GetValue<bool>());
        Assert.Contains(changed, change => change.Key == nameof(AppSettings.DisabledToolPacks));
    }

    [Fact]
    public void UnderAPolicyVetoAFileThatTurnsThePackOnAddsNoNoticeAndSetsNoMarker()
    {
        AppSettings current = new() { DisabledToolPacks = [nameof(ToolPack.Audit), nameof(ToolPack.Recipes)] };

        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(
            current,
            FileOf([nameof(ToolPack.Audit)], marker: true),
            recipesPackForbidden: true);

        Assert.DoesNotContain(changed, change => change.Key == SettingsTransfer.RecipesPackTurnedOnKey);
        Assert.False(merged.RecipesPackMigrated);
        Assert.Contains(nameof(ToolPack.Recipes), merged.DisabledToolPacks);
    }

    [Fact]
    public void AMarkerThatIsNotTrueDoesNotTurnThePackOn()
    {
        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(
            PackOff(),
            FileOf([], marker: false));

        Assert.Contains(nameof(ToolPack.Recipes), merged.DisabledToolPacks);
        Assert.DoesNotContain(changed, change => change.Key == SettingsTransfer.RecipesPackTurnedOnKey);
    }

    [Fact]
    public void ThePackThatIsOnNowFollowsTheFileLikeAnyOther()
    {
        AppSettings current = new() { RecipesPackMigrated = true, DisabledToolPacks = [nameof(ToolPack.Audit)] };

        (AppSettings oldFile, _) = SettingsTransfer.Import(current, FileOf([nameof(ToolPack.Audit)]));
        (AppSettings newFile, _) = SettingsTransfer.Import(current, FileOf([nameof(ToolPack.Recipes)], marker: true));

        Assert.Equal([nameof(ToolPack.Audit)], oldFile.DisabledToolPacks);
        Assert.Equal([nameof(ToolPack.Recipes)], newFile.DisabledToolPacks);
    }

    [Fact]
    public void AnExportCarriesTheMarkerOnlyWhenTheInstallHasIt()
    {
        JsonObject with = SettingsTransfer.Export(new AppSettings { RecipesPackMigrated = true }, includeUserPaths: false, Profile, out _);
        JsonObject without = SettingsTransfer.Export(new AppSettings(), includeUserPaths: false, Profile, out _);

        Assert.True(with[SettingsTransfer.SettingsProperty]![nameof(AppSettings.RecipesPackMigrated)]!.GetValue<bool>());
        Assert.False(without[SettingsTransfer.SettingsProperty]!.AsObject().ContainsKey(nameof(AppSettings.RecipesPackMigrated)));
    }

    [Fact]
    public void AnExportOfAnInstallWithThePackOnTurnsItOnInAnotherInstallThatHasItOff()
    {
        JsonObject exported = SettingsTransfer.Export(
            new AppSettings { RecipesPackMigrated = true, DisabledToolPacks = [nameof(ToolPack.Audit)] },
            includeUserPaths: false,
            Profile,
            out _);

        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(PackOff(nameof(ToolPack.Audit)), exported);

        Assert.DoesNotContain(nameof(ToolPack.Recipes), merged.DisabledToolPacks);
        Assert.Contains(changed, change => change.Key == SettingsTransfer.RecipesPackTurnedOnKey);
    }

    [Fact]
    public void AnExportOfAnInstallWithThePackOffKeepsItOffInAnotherInstall()
    {
        JsonObject exported = SettingsTransfer.Export(PackOff(nameof(ToolPack.Audit)), includeUserPaths: false, Profile, out _);

        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(PackOff(), exported);

        Assert.Contains(nameof(ToolPack.Recipes), merged.DisabledToolPacks);
        Assert.DoesNotContain(changed, change => change.Key == SettingsTransfer.RecipesPackTurnedOnKey);
    }

    [Fact]
    public void TheNoticeIsASensitiveKeyAndNotASettingKey()
    {
        Assert.Contains(SettingsTransfer.RecipesPackTurnedOnKey, SettingsImportPreview.SensitiveKeys);
        Assert.DoesNotContain(SettingsTransfer.RecipesPackTurnedOnKey, SettingsTransfer.TransferableKeys);
        Assert.DoesNotContain(nameof(AppSettings.RecipesPackMigrated), SettingsTransfer.TransferableKeys);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task ThePreviewSaysInWordsThatThePackIsTurnedOnAmongTheSensitiveLinesAndDoesNotCountItAsASetting(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(System.IO.Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), locale);
        SettingsTransferChange packs = new(
            nameof(AppSettings.DisabledToolPacks),
            new JsonArray(nameof(ToolPack.Audit), nameof(ToolPack.Recipes)),
            new JsonArray(nameof(ToolPack.Audit)));
        SettingsTransferChange notice = new(SettingsTransfer.RecipesPackTurnedOnKey, JsonValue.Create(false), JsonValue.Create(true));

        string preview = SettingsImportPreview.Compose(localizer, [packs, notice]);
        string alone = SettingsImportPreview.Compose(localizer, [packs]);

        string sentence = localizer["SettingsImportPreviewRecipesOn"];
        string blankLine = Environment.NewLine + Environment.NewLine;
        Assert.NotEqual("SettingsImportPreviewRecipesOn", sentence);
        Assert.StartsWith(localizer["SettingsImportPreviewSensitive"], preview, StringComparison.Ordinal);
        Assert.Contains(sentence, preview[..preview.IndexOf(blankLine, StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.DoesNotContain(sentence, alone, StringComparison.Ordinal);
        Assert.Equal(alone.Split(blankLine).Last(), preview.Split(blankLine).Last());
    }
}
