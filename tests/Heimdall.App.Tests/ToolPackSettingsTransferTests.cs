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
using System.Text.Json.Nodes;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The list of disabled tool packs in settings export and import, in the import preview, and the
/// locale keys every pack needs in each language.
/// </summary>
public sealed class ToolPackSettingsTransferTests
{
    private const string Profile = @"C:\Users\someone";

    private static readonly string[] Locales = ["en", "fr", "es"];

    [Fact]
    public void TheList_IsASensitiveKeyOfTheImportPreview()
        => Assert.Contains(nameof(AppSettings.DisabledToolPacks), SettingsImportPreview.SensitiveKeys);

    [Fact]
    public void TheList_IsTransferable_AndHasAPlaceInTheLabelCatalog()
    {
        Assert.Contains(nameof(AppSettings.DisabledToolPacks), SettingsTransfer.TransferableKeys);
        Assert.True(SettingsLabelCatalog.TryGetPlace(nameof(AppSettings.DisabledToolPacks), out SettingsPanelPlace? place));
        Assert.NotNull(place);
        Assert.Empty(SettingsLabelCatalog.Unplaced([nameof(AppSettings.DisabledToolPacks)]));
    }

    [Fact]
    public void TheList_RoundTripsThroughExportAndImport()
    {
        AppSettings source = new() { DisabledToolPacks = [nameof(ToolPack.Audit), "FuturePack"] };

        JsonObject exported = SettingsTransfer.Export(source, includeUserPaths: false, Profile, out _);
        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(new AppSettings(), exported);

        Assert.Equal([nameof(ToolPack.Audit), "FuturePack"], merged.DisabledToolPacks);
        Assert.Contains(changed, change => change.Key == nameof(AppSettings.DisabledToolPacks));
    }

    [Fact]
    public async Task AnImportThatChangesThePacks_IsShownAsSensitive_WithItsLabelAndItsNames()
    {
        LocalizationManager localizer = await LoadLocalizerAsync("en");
        SettingsTransferChange change = new(
            nameof(AppSettings.DisabledToolPacks),
            new JsonArray(nameof(ToolPack.Audit)),
            new JsonArray());

        string preview = SettingsImportPreview.Compose(localizer, [change]);

        Assert.StartsWith(localizer["SettingsImportPreviewSensitive"], preview, StringComparison.Ordinal);
        Assert.Contains(localizer["SettingsSectionToolPacks"], preview, StringComparison.Ordinal);
        Assert.Contains(nameof(ToolPack.Audit), preview, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SettingsSectionToolPacks")]
    [InlineData("SettingsToolPacksIntro")]
    [InlineData("SettingsToolPackAuditWarning")]
    [InlineData("SettingsToolPackSetByPolicy")]
    [InlineData("SettingsToolPackCountOne")]
    [InlineData("SettingsToolPackCount")]
    public async Task EveryBlockText_IsInTheThreeCatalogues(string key)
    {
        foreach (string locale in Locales)
        {
            LocalizationManager localizer = await LoadLocalizerAsync(locale);
            Assert.True(localizer.HasKey(key), $"{locale}: {key}");
            Assert.False(string.IsNullOrWhiteSpace(localizer[key]), $"{locale}: {key} is empty");
        }
    }

    [Fact]
    public async Task EveryOptionalPack_HasItsLabelAndItsDescriptionInTheThreeCatalogues()
    {
        foreach (string locale in Locales)
        {
            LocalizationManager localizer = await LoadLocalizerAsync(locale);
            foreach (ToolPack pack in Enum.GetValues<ToolPack>().Where(pack => pack != ToolPack.Core))
            {
                foreach (string key in new[]
                {
                    SettingsViewModel.ToolPackLabelKey(pack),
                    SettingsViewModel.ToolPackDescriptionKey(pack),
                })
                {
                    Assert.True(localizer.HasKey(key), $"{locale}: {key}");
                    Assert.False(string.IsNullOrWhiteSpace(localizer[key]), $"{locale}: {key} is empty");
                }
            }
        }
    }

    [Fact]
    public async Task ThePackWords_ArePerLanguage_NotCopiesOfTheEnglishOnes()
    {
        LocalizationManager english = await LoadLocalizerAsync("en");
        foreach (string locale in new[] { "fr", "es" })
        {
            LocalizationManager localizer = await LoadLocalizerAsync(locale);
            foreach (string key in new[]
            {
                "SettingsSectionToolPacks",
                "SettingsToolPacksIntro",
                "SettingsToolPackAuditWarning",
                "SettingsToolPackSetByPolicy",
                "SettingsToolPackAuditDescription",
            })
            {
                Assert.NotEqual(english[key], localizer[key]);
            }
        }
    }

    [Fact]
    public async Task ThePreviewLabel_SaysTheListHoldsThePacksTurnedOff_InEveryLanguage()
    {
        Assert.True(SettingsLabelCatalog.TryGetPlace(nameof(AppSettings.DisabledToolPacks), out SettingsPanelPlace? place));
        Assert.Equal("SettingsLabelToolPacksDisabled", place!.LabelKey);

        string[] expected = ["Tool packs turned off", "Packs d'outils d\u00e9sactiv\u00e9s", "Paquetes de herramientas desactivados"];
        for (int index = 0; index < Locales.Length; index++)
        {
            LocalizationManager localizer = await LoadLocalizerAsync(Locales[index]);
            Assert.Equal(expected[index], localizer[place.LabelKey]);
        }
    }

    [Fact]
    public async Task AnImportThatTurnsAPackOn_IsLabelledAsTheListOfPacksTurnedOff()
    {
        LocalizationManager localizer = await LoadLocalizerAsync("en");
        SettingsTransferChange change = new(
            nameof(AppSettings.DisabledToolPacks),
            new JsonArray(nameof(ToolPack.Audit)),
            new JsonArray());

        string preview = SettingsImportPreview.Compose(localizer, [change]);

        Assert.Contains("Tool packs turned off", preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASensitiveListOfNames_IsShownInFull_WhateverItsLength()
    {
        LocalizationManager localizer = await LoadLocalizerAsync("en");
        JsonArray longList = new(
            Enumerable.Range(0, 12).Select(index => (JsonNode?)JsonValue.Create("FuturePack" + index))
                .Append(JsonValue.Create(nameof(ToolPack.Audit)))
                .ToArray());

        string text = SettingsImportPreview.DescribeFullValue(localizer, longList, nameof(AppSettings.DisabledToolPacks));
        Assert.EndsWith(nameof(ToolPack.Audit), text, StringComparison.Ordinal);
        Assert.True(text.Length > 60);

        SettingsTransferChange change = new(nameof(AppSettings.DisabledToolPacks), new JsonArray(), longList);
        Assert.Contains(text, SettingsImportPreview.Compose(localizer, [change]), StringComparison.Ordinal);
    }

    private static async Task<LocalizationManager> LoadLocalizerAsync(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }
}
