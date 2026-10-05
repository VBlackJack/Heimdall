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

namespace Heimdall.App.Tests;

/// <summary>
/// The portable settings file carries preferences and never a secret or this machine's state.
/// </summary>
public sealed class SettingsTransferTests
{
    private const string Profile = @"C:\Users\someone";

    [Fact]
    public void AnExportCarriesNoSecretAndNoMachineState()
    {
        AppSettings settings = new()
        {
            DefaultTheme = "Tarn",
            PinHash = "pin-hash",
            PinSalt = "pin-salt",
            VaultEnabled = true,
            CmdLibGitSyncToken = "token",
            CredentialProviderUnlockSecretEncrypted = "unlock",
            ShowToolsPanel = true,
        };
        settings.SshGateways.Add(new SshGatewayDto { Id = "gw", SshPasswordEncrypted = "secret" });

        JsonObject exported = SettingsTransfer.Export(settings, includeUserPaths: false, Profile, out _);
        JsonObject carried = exported[SettingsTransfer.SettingsProperty]!.AsObject();

        Assert.Equal("Tarn", carried[nameof(AppSettings.DefaultTheme)]!.GetValue<string>());
        foreach (string withheld in new[]
        {
            nameof(AppSettings.PinHash), nameof(AppSettings.PinSalt), nameof(AppSettings.VaultEnabled),
            nameof(AppSettings.CmdLibGitSyncToken), nameof(AppSettings.CredentialProviderUnlockSecretEncrypted),
            nameof(AppSettings.ShowToolsPanel), nameof(AppSettings.SshGateways), nameof(AppSettings.ExtensionData),
        })
        {
            Assert.False(carried.ContainsKey(withheld), withheld + " must not travel");
        }

        string text = exported.ToJsonString();
        Assert.DoesNotContain("pin-hash", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.True(SettingsTransfer.TransferableKeys.Count >= 60, "the allow-list is not reading the panel");
    }

    [Fact]
    public void APathInTheUserProfileStaysBehindUnlessAskedFor()
    {
        AppSettings settings = new() { PlinkPath = Profile + @"\tools\plink.exe", PuttyPath = @"D:\tools\putty.exe" };

        JsonObject withheld = SettingsTransfer.Export(settings, includeUserPaths: false, Profile, out int heldBack);
        JsonObject included = SettingsTransfer.Export(settings, includeUserPaths: true, Profile, out _);

        Assert.True(heldBack >= 1);
        Assert.False(withheld[SettingsTransfer.SettingsProperty]!.AsObject().ContainsKey(nameof(AppSettings.PlinkPath)));
        Assert.True(withheld[SettingsTransfer.SettingsProperty]!.AsObject().ContainsKey(nameof(AppSettings.PuttyPath)));
        Assert.Equal(Profile + @"\tools\plink.exe", included[SettingsTransfer.SettingsProperty]![nameof(AppSettings.PlinkPath)]!.GetValue<string>());
    }

    [Fact]
    public void AnImportTakesOnlyTransferableKeysAndNamesWhatChanges()
    {
        AppSettings current = new() { DefaultTheme = "Drakul", PinHash = "mine" };
        JsonObject file = new()
        {
            [SettingsTransfer.FormatProperty] = SettingsTransfer.FormatName,
            [SettingsTransfer.VersionProperty] = SettingsTransfer.FormatVersion,
            [SettingsTransfer.SettingsProperty] = new JsonObject
            {
                [nameof(AppSettings.DefaultTheme)] = "Tarn",
                [nameof(AppSettings.MaxEmbeddedSessions)] = current.MaxEmbeddedSessions,
                [nameof(AppSettings.PinHash)] = "theirs",
            },
        };

        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(current, file);

        Assert.Equal("Tarn", merged.DefaultTheme);
        Assert.Equal("mine", merged.PinHash);
        SettingsTransferChange change = Assert.Single(changed);
        Assert.Equal(nameof(AppSettings.DefaultTheme), change.Key);
        Assert.Equal("Drakul", change.Before!.GetValue<string>());
        Assert.Equal("Tarn", change.After!.GetValue<string>());
    }

    // The preview names a change by its panel label; a transferable setting without one would
    // reach the user under its property name, so every one must have a place in the catalog.
    [Fact]
    public async Task EveryTransferableSetting_HasAPanelLabel_WhoseKeysExistInEveryLanguage()
    {
        Assert.True(SettingsTransfer.TransferableKeys.Count >= 60, "the allow-list is not reading the panel");

        Assert.Empty(SettingsLabelCatalog.Unplaced(SettingsTransfer.TransferableKeys));

        foreach (string locale in new[] { "en", "fr", "es" })
        {
            LocalizationManager localizer = await LoadLocalizerAsync(locale);
            foreach (SettingsPanelPlace place in SettingsLabelCatalog.AllPlaces)
            {
                Assert.True(localizer.HasKey(place.LabelKey), $"{locale}: {place.LabelKey}");
                Assert.True(localizer.HasKey(place.TabKey), $"{locale}: {place.TabKey}");
                Assert.True(localizer.HasKey(place.AreaKey), $"{locale}: {place.AreaKey}");
            }
        }
    }

    // Positive control for the test above: the check does report a setting the catalog lacks.
    [Fact]
    public void TheLabelCheck_ReportsASettingTheCatalogLacks()
    {
        Assert.Equal(
            ["NotAPanelSetting"],
            SettingsLabelCatalog.Unplaced([nameof(AppSettings.DefaultTheme), "NotAPanelSetting"]));
    }

    [Fact]
    public async Task APreviewLine_FallsBackOnThePropertyName_OnlyForASettingWithoutAPlace()
    {
        LocalizationManager localizer = await LoadLocalizerAsync("en");

        string labelled = SettingsImportPreview.DescribeChange(
            localizer,
            new SettingsTransferChange(nameof(AppSettings.DefaultTheme), JsonValue.Create("Drakul"), JsonValue.Create("Tarn")));
        string unlabelled = SettingsImportPreview.DescribeChange(
            localizer,
            new SettingsTransferChange("NotAPanelSetting", JsonValue.Create(1), JsonValue.Create(2)));

        Assert.Equal("General > Appearance > Theme: Drakul -> Tarn", labelled);
        Assert.Equal("NotAPanelSetting: 1 -> 2", unlabelled);
    }

    [Fact]
    public async Task PreviewValues_AreWordedForTheReader()
    {
        LocalizationManager localizer = await LoadLocalizerAsync("en");

        Assert.Equal("On", SettingsImportPreview.DescribeValue(localizer, JsonValue.Create(true)));
        Assert.Equal("Off", SettingsImportPreview.DescribeValue(localizer, JsonValue.Create(false)));
        Assert.Equal("(empty)", SettingsImportPreview.DescribeValue(localizer, JsonValue.Create("")));
        Assert.Equal("(empty)", SettingsImportPreview.DescribeValue(localizer, null));
        Assert.Equal("1920x1080, 1280x720", SettingsImportPreview.DescribeValue(localizer, new JsonArray("1920x1080", "1280x720")));
        Assert.Equal("1 item", SettingsImportPreview.DescribeValue(localizer, new JsonArray(new JsonObject { ["Name"] = "tool" })));
        Assert.Equal("0 items", SettingsImportPreview.DescribeValue(localizer, new JsonArray()));
        string longValue = new('x', SettingsImportPreview.MaxValueLength + 10);
        Assert.Equal(SettingsImportPreview.MaxValueLength, SettingsImportPreview.DescribeValue(localizer, JsonValue.Create(longValue)).Length);
    }

    [Theory]
    [InlineData("en", 1, "1 setting will change.")]
    [InlineData("en", 2, "2 settings will change.")]
    [InlineData("fr", 1, "1 param\u00E8tre va changer.")]
    [InlineData("fr", 2, "2 param\u00E8tres vont changer.")]
    public async Task ThePreviewHeader_AgreesWithTheNumberOfChanges(string locale, int count, string expectedStart)
    {
        LocalizationManager localizer = await LoadLocalizerAsync(locale);
        SettingsTransferChange[] changes = Enumerable
            .Range(0, count)
            .Select(index => new SettingsTransferChange(nameof(AppSettings.TerminalFontSize), JsonValue.Create(index), JsonValue.Create(index + 1)))
            .ToArray();

        Assert.StartsWith(expectedStart, SettingsImportPreview.Compose(localizer, changes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePreview_ShowsACommandFirst_WholeEvenBehindTwentyHarmlessChanges()
    {
        LocalizationManager localizer = await LoadLocalizerAsync("en");
        string payload = "powershell -NoProfile -Command \"" + new string('x', 120) + "\"";
        List<SettingsTransferChange> changes = Enumerable.Range(0, 21)
            .Select(index => new SettingsTransferChange(nameof(AppSettings.TerminalFontSize), JsonValue.Create(index), JsonValue.Create(index + 1)))
            .Append(new SettingsTransferChange(nameof(AppSettings.CredentialProviderCommand), JsonValue.Create(""), JsonValue.Create(payload)))
            .ToList();

        string preview = SettingsImportPreview.Compose(localizer, changes);

        // The file's author chose the order, and the command used to land in "and 2 more".
        Assert.Contains(payload, preview, StringComparison.Ordinal);
        Assert.StartsWith(localizer["SettingsImportPreviewSensitive"], preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePreview_NamesEachImportedToolAndItsExecutable()
    {
        LocalizationManager localizer = await LoadLocalizerAsync("en");
        JsonArray tools = new(
            new JsonObject { ["Name"] = "Ping", ["ExecutablePath"] = @"C:\Windows\ping.exe" },
            new JsonObject { ["Name"] = "Helper", ["ExecutablePath"] = @"\\share\x.exe", ["RunAsAdministrator"] = true });

        string preview = SettingsImportPreview.Compose(
            localizer,
            [new SettingsTransferChange(nameof(AppSettings.ExternalTools), new JsonArray(), tools)]);

        Assert.Contains(@"Ping (C:\Windows\ping.exe)", preview, StringComparison.Ordinal);
        Assert.Contains(@"Helper (\\share\x.exe, as administrator)", preview, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://alice:ghp_secret@github.com/team/commands.git", "https://github.com/team/commands.git")]
    [InlineData("https://github.com/team/commands.git", "https://github.com/team/commands.git")]
    public void AnExport_TakesCredentialsOutOfTheRepositoryAddress(string typed, string exported)
    {
        AppSettings settings = new() { CmdLibGitSyncUrl = typed };

        JsonObject file = SettingsTransfer.Export(settings, includeUserPaths: true, Profile, out _);

        Assert.Equal(exported, file[SettingsTransfer.SettingsProperty]![nameof(AppSettings.CmdLibGitSyncUrl)]!.GetValue<string>());
    }

    private static async Task<LocalizationManager> LoadLocalizerAsync(string locale)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }

    [Fact]
    public void AnythingElseIsRefusedAsAFile()
    {
        AppSettings current = new();

        Assert.Throws<FormatException>(() => SettingsTransfer.Import(current, new JsonObject { ["servers"] = new JsonArray() }));
        Assert.Throws<FormatException>(() => SettingsTransfer.Import(current, new JsonObject
        {
            [SettingsTransfer.FormatProperty] = SettingsTransfer.FormatName,
            [SettingsTransfer.VersionProperty] = SettingsTransfer.FormatVersion + 1,
            [SettingsTransfer.SettingsProperty] = new JsonObject(),
        }));
    }
}
