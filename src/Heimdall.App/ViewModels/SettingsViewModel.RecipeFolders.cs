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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Recipes;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The editor of the folders recipes may run programs from, besides Windows and Program Files.
/// </summary>
/// <remarks>
/// <para>The pending edit is <see cref="RecipeProgramFolders"/>, bound to the settings member of the
/// same name, so it travels with Save, Restore defaults, the default marker and the settings import.
/// A folder is added through a folder picker only and has to pass <see cref="RecipeFolderRule"/>: a
/// network, relative or device path is refused with its reason, shown under the list.</para>
/// <para>The list is read each time a recipe runs, from the saved settings: an added folder counts
/// once the panel is saved.</para>
/// </remarks>
public partial class SettingsViewModel
{
    private IRecipeFileDialogs? _recipeFileDialogs;

    /// <summary>The pending list of folders recipes may run programs from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecipeProgramFolders))]
    [NotifyPropertyChangedFor(nameof(IsRecipeProgramFoldersEmpty))]
    private string[] _recipeProgramFolders = [];

    /// <summary>The reason the last folder the user tried to add was refused; empty when it was accepted.</summary>
    [ObservableProperty]
    private string _recipeProgramFolderStatus = string.Empty;

    /// <summary>
    /// The notice shown after a folder that touches Windows or Program Files was added; empty otherwise. It is
    /// not a refusal, so it has a text of its own and does not borrow the warning colour of the status.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecipeProgramFolderNotice))]
    private string _recipeProgramFolderNotice = string.Empty;

    /// <summary>What the folder checks ask of the machine: the roots a folder may touch. Replaceable for tests.</summary>
    internal IRecipeMachine RecipeFolderMachine { get; set; } = RecipeMachine.Instance;

    /// <summary>The imported recipes, their import, export and removal; null where no recipe store was supplied (tests).</summary>
    public RecipeSettingsViewModel? Recipes { get; private set; }

    /// <summary>True when at least one folder was added.</summary>
    public bool HasRecipeProgramFolders => RecipeProgramFolders.Length > 0;

    /// <summary>True when no folder was added: the list shows its empty line.</summary>
    public bool IsRecipeProgramFoldersEmpty => RecipeProgramFolders.Length == 0;

    /// <summary>True when <see cref="RecipeProgramFolderNotice"/> has something to say.</summary>
    public bool HasRecipeProgramFolderNotice => !string.IsNullOrEmpty(RecipeProgramFolderNotice);

    /// <summary>True when <see cref="RecipeProgramFolderStatus"/> has something to say.</summary>
    public bool HasRecipeProgramFolderStatus => !string.IsNullOrEmpty(RecipeProgramFolderStatus);

    /// <summary>Supplies the recipe import list and the pickers; called by the composition root.</summary>
    /// <param name="recipes">The imported recipes.</param>
    /// <param name="files">The folder picker.</param>
    internal void AttachRecipes(RecipeSettingsViewModel? recipes, IRecipeFileDialogs? files)
    {
        Recipes = recipes;
        _recipeFileDialogs = files;
        OnPropertyChanged(nameof(Recipes));
    }

    partial void OnRecipeProgramFolderStatusChanged(string value) => OnPropertyChanged(nameof(HasRecipeProgramFolderStatus));

    /// <summary>Asks for a folder and adds it when it passes the rule.</summary>
    [RelayCommand]
    private void AddRecipeProgramFolder()
    {
        string? picked = _recipeFileDialogs?.PickProgramFolder();
        if (picked is null)
        {
            return;
        }

        TryAddRecipeProgramFolder(picked);
    }

    /// <summary>
    /// Adds a folder to the pending list when it passes <see cref="RecipeFolderRule"/>; otherwise
    /// says why in <see cref="RecipeProgramFolderStatus"/> and changes nothing.
    /// </summary>
    /// <param name="folder">The folder the user chose.</param>
    /// <returns>True when the folder was added.</returns>
    internal bool TryAddRecipeProgramFolder(string folder)
    {
        RecipeProgramFolderNotice = string.Empty;
        RecipeFolderRefusal refusal = RecipeFolderRule.Check(folder, RecipeProgramFolders);
        if (refusal != RecipeFolderRefusal.None)
        {
            RecipeProgramFolderStatus = RecipeImportText.Describe(_localizer, refusal, RecipeFolderRule.MaxFolders);
            return false;
        }

        RecipeProgramFolderStatus = string.Empty;
        string normalized = RecipeFolderRule.Normalize(folder);
        RecipeProgramFolders = [.. RecipeProgramFolders, normalized];
        if (RecipeFolderRule.TouchesRoot(normalized, BuiltInRecipeRoots()))
        {
            RecipeProgramFolderNotice = _localizer["RecipeFolderNoticeBuiltInRoot"];
        }

        return true;
    }

    /// <summary>Takes a folder out of the pending list.</summary>
    /// <param name="folder">The folder as listed.</param>
    [RelayCommand]
    private void RemoveRecipeProgramFolder(string? folder)
    {
        RecipeProgramFolderStatus = string.Empty;
        RecipeProgramFolderNotice = string.Empty;
        RecipeProgramFolders = [.. RecipeProgramFolders.Where(item => !string.Equals(item, folder, StringComparison.OrdinalIgnoreCase))];
    }

    private string[] BuiltInRecipeRoots()
        => [RecipeFolderMachine.WindowsDirectory, RecipeFolderMachine.ProgramFilesDirectory, RecipeFolderMachine.ProgramFilesX86Directory];

    /// <summary>Rewords the recipe list after the interface language changed; it does not read the folder if the list was never shown.</summary>
    private void RefreshRecipeTexts()
    {
        // The notice was worded when it was shown; a language change takes it away rather than leave it stale.
        RecipeProgramFolderNotice = string.Empty;
        Recipes?.RefreshIfLoaded();
    }
}
