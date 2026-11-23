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
using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.ViewModels.Settings;

/// <summary>
/// One imported recipe in the Settings list: what the user needs to recognise it (name, who wrote
/// it, version, the program it runs) and the two things they can do with it. The text comes from
/// the recipe file, so it is shown as plain text with the characters that could hide something escaped.
/// </summary>
public sealed class ImportedRecipeItemViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private readonly string _author;
    private string _provenanceText = string.Empty;
    private string _versionText = string.Empty;
    private string _programText = string.Empty;
    private string _unavailableText = string.Empty;
    private string _exportName = string.Empty;
    private string _removeName = string.Empty;

    /// <summary>Creates the row of a recipe that loaded.</summary>
    /// <param name="loaded">The recipe and its index entry.</param>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="export">What Export does for this row.</param>
    /// <param name="remove">What Remove does for this row.</param>
    public ImportedRecipeItemViewModel(
        LoadedRecipe loaded,
        LocalizationManager localizer,
        Func<ImportedRecipeItemViewModel, Task> export,
        Func<ImportedRecipeItemViewModel, Task> remove)
        : this(
            loaded.Entry,
            localizer,
            RecipeText.Escape(loaded.Recipe.Name, escapeNonAscii: false),
            RecipeText.Escape(loaded.Recipe.Author, escapeNonAscii: false),
            RecipeText.Escape(loaded.Recipe.Version, escapeNonAscii: false),
            isAvailable: true,
            export,
            remove)
    {
    }

    /// <summary>Creates the row of an index entry whose file did not load: it can still be removed.</summary>
    /// <param name="entry">The index entry.</param>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="export">What Export does for this row.</param>
    /// <param name="remove">What Remove does for this row.</param>
    public ImportedRecipeItemViewModel(
        RecipeStoreEntry entry,
        LocalizationManager localizer,
        Func<ImportedRecipeItemViewModel, Task> export,
        Func<ImportedRecipeItemViewModel, Task> remove)
        : this(entry, localizer, RecipeText.Escape(entry.Id, escapeNonAscii: false), string.Empty, string.Empty, isAvailable: false, export, remove)
    {
    }

    private ImportedRecipeItemViewModel(
        RecipeStoreEntry entry,
        LocalizationManager localizer,
        string name,
        string author,
        string version,
        bool isAvailable,
        Func<ImportedRecipeItemViewModel, Task> export,
        Func<ImportedRecipeItemViewModel, Task> remove)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Id = entry.Id;
        Name = name;
        _author = author;
        Version = version;
        ProgramPath = RecipeText.Escape(entry.ResolvedProgramPath, escapeNonAscii: true);
        IsAvailable = isAvailable;
        ExportCommand = new AsyncRelayCommand(() => export(this), () => IsAvailable);
        RemoveCommand = new AsyncRelayCommand(() => remove(this));
        RefreshTexts();
    }

    /// <summary>The recipe id, which is what the store knows it by.</summary>
    public string Id { get; }

    /// <summary>The recipe name as written in the file, made safe to show; the id when the file did not load.</summary>
    public string Name { get; }

    /// <summary>The version as written in the file; empty when the file did not load.</summary>
    public string Version { get; }

    /// <summary>The program path the recipe resolved to at import, made safe to show.</summary>
    public string ProgramPath { get; }

    /// <summary>False when the file on disk did not load: it can be removed, not exported.</summary>
    public bool IsAvailable { get; }

    /// <summary>True when the row has a line that says why it did not load.</summary>
    public bool HasUnavailableText => !IsAvailable;

    /// <summary>"Unverified recipe by Acme": who wrote it, never vouched for; empty when the file did not load.</summary>
    public string ProvenanceText
    {
        get => _provenanceText;
        private set => SetProperty(ref _provenanceText, value);
    }

    /// <summary>The version line; empty when the file did not load.</summary>
    public string VersionText
    {
        get => _versionText;
        private set => SetProperty(ref _versionText, value);
    }

    /// <summary>The line that names the program path.</summary>
    public string ProgramText
    {
        get => _programText;
        private set => SetProperty(ref _programText, value);
    }

    /// <summary>The sentence that says the file did not load and what to do; empty for a row that did.</summary>
    public string UnavailableText
    {
        get => _unavailableText;
        private set => SetProperty(ref _unavailableText, value);
    }

    /// <summary>The name a screen reader gives the Export button.</summary>
    public string ExportAccessibleName
    {
        get => _exportName;
        private set => SetProperty(ref _exportName, value);
    }

    /// <summary>The name a screen reader gives the Remove button.</summary>
    public string RemoveAccessibleName
    {
        get => _removeName;
        private set => SetProperty(ref _removeName, value);
    }

    /// <summary>Writes the original imported file where the user picks.</summary>
    public IAsyncRelayCommand ExportCommand { get; }

    /// <summary>Removes the recipe once the user confirmed.</summary>
    public IAsyncRelayCommand RemoveCommand { get; }

    /// <summary>Rewords the row after the interface language changed.</summary>
    public void RefreshTexts()
    {
        ProvenanceText = IsAvailable ? RecipeToolText.Provenance(_author, _localizer.GetString) : string.Empty;
        VersionText = IsAvailable ? _localizer.Format("SettingsRecipeItemVersion", Version) : string.Empty;
        ProgramText = _localizer.Format("SettingsRecipeItemProgram", ProgramPath);
        UnavailableText = IsAvailable ? string.Empty : _localizer["SettingsRecipeItemUnavailable"];
        ExportAccessibleName = _localizer.Format("A11yRecipeExport", Name);
        RemoveAccessibleName = _localizer.Format("A11yRecipeRemove", Name);
    }
}
