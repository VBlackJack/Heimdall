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

using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>A machine that only knows where folders lead, for the Settings that checks a folder before it adds it.</summary>
internal sealed class FolderFinalFormMachine : IRecipeMachine
{
    public Dictionary<string, string?> Finals { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string WindowsDirectory => @"C:\Windows";

    public string ProgramFilesDirectory => @"C:\Program Files";

    public string ProgramFilesX86Directory => @"C:\Program Files (x86)";

    public IReadOnlyList<string> PathEntries => [];

    public bool FileExists(string path) => false;

    public string? ResolveFinalPath(string path) => Finals.TryGetValue(path, out string? final) ? final : null;

    public string? ReadOriginalFileName(string path) => null;

    public RecipeItemAccess? ReadAccess(string path) => null;
}

public sealed partial class SettingsViewModelTests
{
    private static async Task<(SettingsViewModel ViewModel, FolderFinalFormMachine Machine, LocalizationManager Localizer)> FolderPanelAsync()
    {
        LocalizationManager localizer = await LoadLocalizerForToolPacksAsync("en");
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);
        FolderFinalFormMachine machine = new();
        viewModel.RecipeFolderMachine = machine;
        return (viewModel, machine, localizer);
    }

    [Fact]
    public async Task AFolderThatLeadsToADriveRoot_IsRefusedWithItsOwnTextAndAddsNothing()
    {
        (SettingsViewModel viewModel, FolderFinalFormMachine machine, LocalizationManager localizer) = await FolderPanelAsync();
        machine.Finals[@"D:\link"] = @"C:\";

        bool added = viewModel.TryAddRecipeProgramFolder(@"D:\link");

        Assert.False(added);
        Assert.Empty(viewModel.RecipeProgramFolders);
        Assert.Equal(localizer["RecipeFolderRefusalLeadsToDriveRoot"], viewModel.RecipeProgramFolderStatus);
        Assert.StartsWith("This folder leads to the root of a drive", viewModel.RecipeProgramFolderStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\srv\share\x")]
    [InlineData(@"\\srv\share")]
    public async Task AFolderThatLeadsToANetworkLocation_IsRefusedWithItsOwnTextAndAddsNothing(string finalForm)
    {
        (SettingsViewModel viewModel, FolderFinalFormMachine machine, LocalizationManager localizer) = await FolderPanelAsync();
        machine.Finals[@"Z:\tools"] = finalForm;

        bool added = viewModel.TryAddRecipeProgramFolder(@"Z:\tools");

        Assert.False(added);
        Assert.Empty(viewModel.RecipeProgramFolders);
        Assert.Equal(localizer["RecipeFolderRefusalLeadsToNetwork"], viewModel.RecipeProgramFolderStatus);
        Assert.Contains("local drive", viewModel.RecipeProgramFolderStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderWhoseFinalFormCannotBeResolved_IsAcceptedAsWritten()
    {
        (SettingsViewModel viewModel, _, _) = await FolderPanelAsync();

        Assert.True(viewModel.TryAddRecipeProgramFolder(@"D:\not-there-yet"));

        Assert.Equal([@"D:\not-there-yet"], viewModel.RecipeProgramFolders);
        Assert.Equal(string.Empty, viewModel.RecipeProgramFolderStatus);
    }

    [Fact]
    public async Task ALinkToAnOrdinaryLocalFolder_IsAccepted_WithoutANotice()
    {
        (SettingsViewModel viewModel, FolderFinalFormMachine machine, _) = await FolderPanelAsync();
        machine.Finals[@"D:\link"] = @"E:\real\tools";

        Assert.True(viewModel.TryAddRecipeProgramFolder(@"D:\link"));

        Assert.False(viewModel.HasRecipeProgramFolderNotice);
    }

    [Fact]
    public async Task TheNoticeAlsoConsidersWhereTheFolderLeads()
    {
        (SettingsViewModel viewModel, FolderFinalFormMachine machine, _) = await FolderPanelAsync();
        machine.Finals[@"D:\link"] = @"C:\Windows\System32\Tools";

        Assert.True(viewModel.TryAddRecipeProgramFolder(@"D:\link"));

        Assert.True(viewModel.HasRecipeProgramFolderNotice);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task TheTwoNewRefusalsHaveTheirTextInTheThreeLanguages(string locale)
    {
        LocalizationManager localizer = await LoadLocalizerForToolPacksAsync(locale);

        foreach (string key in new[] { "RecipeFolderRefusalLeadsToDriveRoot", "RecipeFolderRefusalLeadsToNetwork" })
        {
            Assert.True(localizer.HasKey(key), $"{locale}: {key}");
            Assert.NotEqual(key, localizer[key]);
        }
    }

    public static TheoryData<string, bool> FinalFormsOfALink => new()
    {
        { @"E:\real\tools", true },
        { @"E:\" + new string('a', 250), false },
        { @"E:\real\a*b", false },
        { @"E:\real\a|b", false },
        { @"C:\", false },
        { @"\\srv\share\x", false },
    };

    [Theory]
    [MemberData(nameof(FinalFormsOfALink))]
    public async Task SettingsJudgesAFinalFormTheSameWayAsTheResolver(string finalForm, bool accepted)
    {
        (SettingsViewModel viewModel, FolderFinalFormMachine machine, LocalizationManager localizer) = await FolderPanelAsync();
        machine.Finals[@"D:\link"] = finalForm;

        bool added = viewModel.TryAddRecipeProgramFolder(@"D:\link");

        Assert.Equal(accepted, added);
        Assert.Equal(accepted, RecipeFolderRule.CheckFinalForm(finalForm) == RecipeFolderRefusal.None);
        Assert.Equal(accepted, viewModel.RecipeProgramFolders.Length == 1);
        if (!accepted)
        {
            Assert.False(string.IsNullOrEmpty(viewModel.RecipeProgramFolderStatus));
        }
    }

    [Fact]
    public async Task ATooLongFinalForm_IsRefusedAtAddTimeWithTheGenericText()
    {
        (SettingsViewModel viewModel, FolderFinalFormMachine machine, LocalizationManager localizer) = await FolderPanelAsync();
        machine.Finals[@"D:\link"] = @"E:\" + new string('a', 250);

        bool added = viewModel.TryAddRecipeProgramFolder(@"D:\link");

        Assert.False(added);
        Assert.Empty(viewModel.RecipeProgramFolders);
        Assert.Equal(localizer["RecipeFolderRefusalLeadsToUnallowedLocation"], viewModel.RecipeProgramFolderStatus);
        Assert.StartsWith("This folder leads to a location that cannot be allowed", viewModel.RecipeProgramFolderStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task TheGenericRefusalHasItsTextInTheThreeLanguages(string locale)
    {
        LocalizationManager localizer = await LoadLocalizerForToolPacksAsync(locale);

        Assert.True(localizer.HasKey("RecipeFolderRefusalLeadsToUnallowedLocation"));
        Assert.NotEqual("RecipeFolderRefusalLeadsToUnallowedLocation", localizer["RecipeFolderRefusalLeadsToUnallowedLocation"]);
    }
}
