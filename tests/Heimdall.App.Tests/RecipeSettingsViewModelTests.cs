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
using System.Text;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// Import, replace, export and remove in Settings: nothing is stored before the user confirmed the
/// preview, a refused file is never stored, and the recipe tools are reloaded after every change.
/// </summary>
public sealed class RecipeSettingsViewModelTests
{
    private static async Task ImportAsync(RecipeImportHarness harness, byte[]? bytes = null, bool confirm = true)
    {
        harness.Offer(bytes ?? RecipeImportFiles.Bytes());
        harness.Dialogs.ConfirmResult = confirm;
        await harness.ViewModel.ImportCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task AConfirmedPreviewStoresTheRecipeListsItAndReloadsTheTools()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();

        await ImportAsync(harness);

        RecipeStoreEntry entry = Assert.Single(harness.Store.List());
        Assert.Equal("acme.ping", entry.Id);
        Assert.Equal(1, harness.ReloadCount);
        ImportedRecipeItemViewModel item = Assert.Single(harness.ViewModel.Items);
        Assert.Equal("Ping", item.Name);
        Assert.Equal("Unverified recipe by Acme", item.ProvenanceText);
        Assert.Equal("Version 1.0.0", item.VersionText);
        Assert.Contains(FakeRecipeProgramLocator.DefaultPath, item.ProgramText, StringComparison.Ordinal);
        Assert.True(harness.ViewModel.HasItems);
        Assert.False(harness.ViewModel.IsEmpty);
        Assert.Single(harness.Dialogs.Infos);
        Assert.Contains("The recipe \"Ping\" was imported.", harness.Dialogs.Infos[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePreviewNamesItsButtonsAndTheCancelOneIsTheDefault()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();

        await ImportAsync(harness);

        RecipeRecordingDialogs.ConfirmCall call = Assert.Single(harness.Dialogs.Confirms);
        Assert.Equal("Import recipe", call.Title);
        Assert.Equal("Import", call.ConfirmLabel);
        Assert.Equal("Cancel", call.DeclineLabel);
        Assert.Equal("warning", call.Severity);
        Assert.Contains("Author (unverified): Acme", call.Message, StringComparison.Ordinal);
        Assert.Contains(FakeRecipeProgramLocator.DefaultPath, call.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsStoredWhileThePreviewIsOnScreen()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        int storedWhileShown = -1;
        int reloadedWhileShown = -1;
        harness.Dialogs.OnConfirm = _ =>
        {
            storedWhileShown = harness.Store.List().Count;
            reloadedWhileShown = harness.ReloadCount;
            return Task.CompletedTask;
        };

        await ImportAsync(harness);

        Assert.Equal(0, storedWhileShown);
        Assert.Equal(0, reloadedWhileShown);
    }

    [Fact]
    public async Task ADeclinedPreviewStoresNothingAndReloadsNothing()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();

        await ImportAsync(harness, confirm: false);

        Assert.Empty(harness.Store.List());
        Assert.Empty(harness.ViewModel.Items);
        Assert.Equal(0, harness.ReloadCount);
        Assert.Empty(harness.Dialogs.Infos);
        Assert.Empty(harness.Dialogs.Errors);
    }

    [Fact]
    public async Task ACancelledFilePickerDoesNothing()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        harness.Files.ImportPath = null;

        await harness.ViewModel.ImportCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Files.ImportAsked);
        Assert.Empty(harness.Dialogs.Confirms);
        Assert.Empty(harness.Dialogs.Errors);
        Assert.Empty(harness.Store.List());
    }

    [Fact]
    public async Task AFileThatIsNotARecipeIsRefusedInPlainWordsAndNeverStored()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();

        await ImportAsync(harness, Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"colour\": 2 }"));

        (string title, string message) = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal("Recipe not imported", title);
        Assert.StartsWith("This file cannot be imported:", message, StringComparison.Ordinal);
        Assert.Contains("Unknown property \"colour\"", message, StringComparison.Ordinal);
        Assert.Empty(harness.Dialogs.Confirms);
        Assert.Empty(harness.Store.List());
        Assert.Equal(0, harness.ReloadCount);
    }

    [Fact]
    public async Task AFileThatBreaksAnImportRuleIsRefusedAndNeverStored()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();

        await ImportAsync(harness, RecipeImportFiles.Bytes(argumentsJson: "[\"\\\\\\\\server\\\\share\"]"));

        (_, string message) = Assert.Single(harness.Dialogs.Errors);
        Assert.Contains("A double backslash (a network path) is not allowed", message, StringComparison.Ordinal);
        Assert.Empty(harness.Dialogs.Confirms);
        Assert.Empty(harness.Store.List());
    }

    [Fact]
    public async Task AShellProgramIsRefusedAndNeverStored()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        harness.Locator.Refuse(RecipeProgramRefusal.ShellOrInterpreter, "cmd.exe");

        await ImportAsync(harness, RecipeImportFiles.Bytes(program: "cmd.exe"));

        (_, string message) = Assert.Single(harness.Dialogs.Errors);
        Assert.Contains("The program \"cmd.exe\" is a shell or an interpreter, which recipes may not run.", message, StringComparison.Ordinal);
        Assert.Empty(harness.Dialogs.Confirms);
        Assert.Empty(harness.Store.List());
    }

    [Fact]
    public async Task AProgramOutsideTheAllowedFoldersTellsTheUserWhatToDo()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        harness.Locator.Refuse(RecipeProgramRefusal.OutsideAllowedRoots, @"D:\Tools\probe.exe");

        await ImportAsync(harness);

        (_, string message) = Assert.Single(harness.Dialogs.Errors);
        Assert.Contains(@"D:\Tools\probe.exe", message, StringComparison.Ordinal);
        Assert.Contains("Add its folder", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProgramIsResolvedAgainstTheFoldersAsSaved()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        AppSettings settings = await harness.Config.LoadSettingsAsync();
        settings.RecipeProgramFolders = [@"D:\Tools"];
        await harness.Config.SaveSettingsAsync(settings);

        await ImportAsync(harness, confirm: false);

        Assert.Equal([@"D:\Tools"], Assert.Single(harness.Locator.FolderCalls));
    }

    [Fact]
    public async Task ReplacingARecipeShowsWhatChangesAndReplacesOnlyOnConfirmation()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        byte[] first = RecipeImportFiles.Bytes(version: "1.0.0");
        await ImportAsync(harness, first);
        harness.Dialogs.Confirms.Clear();
        harness.Locator.ResolveTo(@"C:\Windows\SysWOW64\ping.exe");
        byte[] second = RecipeImportFiles.Bytes(version: "2.0.0");

        await ImportAsync(harness, second, confirm: false);

        RecipeRecordingDialogs.ConfirmCall call = Assert.Single(harness.Dialogs.Confirms);
        Assert.Equal("Replace recipe", call.Title);
        Assert.Equal("Replace", call.ConfirmLabel);
        Assert.Contains("Version: 1.0.0 becomes 2.0.0", call.Message, StringComparison.Ordinal);
        Assert.Contains(@"Program: C:\Windows\System32\ping.exe becomes C:\Windows\SysWOW64\ping.exe", call.Message, StringComparison.Ordinal);
        Assert.Equal(first, harness.Store.ReadOriginalBytes("acme.ping"));
        Assert.Equal(1, harness.ReloadCount);

        await ImportAsync(harness, second, confirm: true);

        Assert.Equal(second, harness.Store.ReadOriginalBytes("acme.ping"));
        Assert.Equal(@"C:\Windows\SysWOW64\ping.exe", Assert.Single(harness.Store.List()).ResolvedProgramPath);
        Assert.Equal(2, harness.ReloadCount);
        Assert.Contains("was replaced", harness.Dialogs.Infos[^1].Message, StringComparison.Ordinal);
        Assert.Equal("Version 2.0.0", Assert.Single(harness.ViewModel.Items).VersionText);
    }

    [Fact]
    public async Task WhileThePackIsOffTheRecipeIsStoredAndTheUserIsToldWhyItIsNotListed()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        harness.Availability.PackEnabled = false;

        await ImportAsync(harness);

        Assert.Single(harness.Store.List());
        (_, string message) = Assert.Single(harness.Dialogs.Infos);
        Assert.Contains("The Recipes pack is turned off", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileThePolicyForbidsRecipesImportIsNotOfferedAndNothingIsAsked()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        harness.Availability.PackForbidden = true;
        harness.ViewModel.Refresh();

        Assert.False(harness.ViewModel.ImportCommand.CanExecute(null));
        Assert.True(harness.ViewModel.HasPolicyText);
        Assert.False(string.IsNullOrEmpty(harness.ViewModel.PolicyText));

        await ImportAsync(harness);

        Assert.Equal(0, harness.Files.ImportAsked);
        Assert.Empty(harness.Store.List());
    }

    [Fact]
    public async Task RemovingARecipeAsksFirstWithItsOwnButtonsAndRemovesOnlyOnConfirmation()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        await ImportAsync(harness);
        harness.Dialogs.Confirms.Clear();
        ImportedRecipeItemViewModel item = Assert.Single(harness.ViewModel.Items);

        harness.Dialogs.ConfirmResult = false;
        await item.RemoveCommand.ExecuteAsync(null);

        RecipeRecordingDialogs.ConfirmCall call = Assert.Single(harness.Dialogs.Confirms);
        Assert.Equal("Remove recipe", call.Title);
        Assert.Equal("Remove", call.ConfirmLabel);
        Assert.Equal("Cancel", call.DeclineLabel);
        Assert.Contains("acme.ping", call.Message, StringComparison.Ordinal);
        Assert.Single(harness.Store.List());
        Assert.Equal(1, harness.ReloadCount);

        harness.Dialogs.ConfirmResult = true;
        await item.RemoveCommand.ExecuteAsync(null);

        Assert.Empty(harness.Store.List());
        Assert.Empty(harness.ViewModel.Items);
        Assert.True(harness.ViewModel.IsEmpty);
        Assert.Equal(2, harness.ReloadCount);
    }

    [Fact]
    public async Task ExportWritesTheOriginalImportedBytesNotAReSerialization()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        byte[] original = [.. RecipeImportFiles.Bytes(), .. Encoding.UTF8.GetBytes("   \r\n\r\n  ")];
        await ImportAsync(harness, original);
        string target = Path.Combine(harness.Root, "out", "copy.heimdall-recipe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        harness.Files.ExportPath = target;

        await Assert.Single(harness.ViewModel.Items).ExportCommand.ExecuteAsync(null);

        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal("acme.ping.heimdall-recipe.json", harness.Files.SuggestedExportName);
        Assert.Equal("The recipe was exported.", harness.Dialogs.Infos[^1].Message);
    }

    [Fact]
    public async Task ACancelledExportWritesNothing()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        await ImportAsync(harness);
        int infosBefore = harness.Dialogs.Infos.Count;
        harness.Files.ExportPath = null;

        await Assert.Single(harness.ViewModel.Items).ExportCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Files.ExportAsked);
        Assert.Equal(infosBefore, harness.Dialogs.Infos.Count);
        Assert.Empty(harness.Dialogs.Errors);
    }

    [Fact]
    public async Task AFileChangedSinceTheImportIsNotHandedOutAsTheImportedOne()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        await ImportAsync(harness);
        File.AppendAllText(Path.Combine(harness.Root, "data", "recipes", RecipeStore.FileNameFor("acme.ping")), " ");
        harness.Files.ExportPath = Path.Combine(harness.Root, "never.heimdall-recipe.json");

        await Assert.Single(harness.ViewModel.Items).ExportCommand.ExecuteAsync(null);

        Assert.False(File.Exists(harness.Files.ExportPath));
        Assert.Equal(0, harness.Files.ExportAsked);
        (_, string message) = Assert.Single(harness.Dialogs.Errors);
        Assert.Contains("no longer matches what was imported", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecipeWhoseFileChangedStaysListedAsUnavailableAndCanBeRemoved()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        await ImportAsync(harness);
        File.AppendAllText(Path.Combine(harness.Root, "data", "recipes", RecipeStore.FileNameFor("acme.ping")), " ");

        harness.ViewModel.Refresh();

        ImportedRecipeItemViewModel item = Assert.Single(harness.ViewModel.Items);
        Assert.False(item.IsAvailable);
        Assert.True(item.HasUnavailableText);
        Assert.Equal("acme.ping", item.Name);
        Assert.False(item.ExportCommand.CanExecute(null));

        harness.Dialogs.ConfirmResult = true;
        await item.RemoveCommand.ExecuteAsync(null);

        Assert.Empty(harness.Store.List());
    }

    [Fact]
    public async Task TheListFollowsTheStoreWhenItIsRefreshed()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        Assert.True(harness.ViewModel.IsEmpty);

        harness.Store.Add(RecipeImportFiles.Bytes(id: "acme.one", name: "One"), FakeRecipeProgramLocator.DefaultPath);
        harness.Store.Add(RecipeImportFiles.Bytes(id: "acme.two", name: "Two"), FakeRecipeProgramLocator.DefaultPath);
        harness.ViewModel.Refresh();

        Assert.Equal(["One", "Two"], harness.ViewModel.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task TheRowsAreWordedInTheLanguageOfTheInterface()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        await ImportAsync(harness);

        await harness.Localizer.SwitchLocaleAsync("fr");

        ImportedRecipeItemViewModel item = Assert.Single(harness.ViewModel.Items);
        Assert.Equal("Recette non v\u00e9rifi\u00e9e de Acme", item.ProvenanceText);
        Assert.Equal("Version 1.0.0", item.VersionText);
    }
}
