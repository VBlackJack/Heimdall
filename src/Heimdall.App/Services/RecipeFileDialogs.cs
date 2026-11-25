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

using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Services;

/// <summary>
/// The pickers the recipe settings use: the file to import, the file to export to, and a folder
/// recipes may run programs from. A seam, so that no test opens a real dialog.
/// </summary>
public interface IRecipeFileDialogs
{
    /// <summary>Asks for a recipe file to import; null when the user cancelled.</summary>
    string? PickRecipeToImport();

    /// <summary>Asks where to write an exported recipe; null when the user cancelled.</summary>
    /// <param name="suggestedFileName">The file name offered first.</param>
    string? PickRecipeExportTarget(string suggestedFileName);

    /// <summary>Asks for a folder; null when the user cancelled.</summary>
    string? PickProgramFolder();
}

/// <summary>The Windows pickers behind <see cref="IRecipeFileDialogs"/>.</summary>
public sealed class Win32RecipeFileDialogs : IRecipeFileDialogs
{
    private readonly LocalizationManager _localizer;

    /// <summary>Creates the pickers with the localizer that words their titles and filters.</summary>
    /// <param name="localizer">The localizer of the current language.</param>
    public Win32RecipeFileDialogs(LocalizationManager localizer)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <inheritdoc />
    public string? PickRecipeToImport()
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = _localizer["RecipeFilePickerImportTitle"],
            Filter = _localizer["RecipeFilePickerFilter"],
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickRecipeExportTarget(string suggestedFileName)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = _localizer["RecipeFilePickerExportTitle"],
            Filter = _localizer["RecipeFilePickerFilter"],
            DefaultExt = RecipeFormat.FileExtension,
            FileName = suggestedFileName,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickProgramFolder()
    {
        Microsoft.Win32.OpenFolderDialog dialog = new()
        {
            Title = _localizer["RecipeFolderPickerTitle"],
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
