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

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Recipes;

namespace Heimdall.App.ViewModels.Settings;

/// <summary>
/// The imported recipes in Settings: import a recipe file after reading its preview, replace one,
/// export the original file, and remove one. Nothing is stored before the user confirmed the
/// preview, and a file that fails any check is refused with its reasons and never stored.
/// </summary>
/// <remarks>
/// Nothing is read from the recipes folder when the view model is built: the list is read the first
/// time the card is shown (see <see cref="EnsureLoaded"/>) and after each import, replacement or
/// removal, so an application start never loads and hashes every recipe file.
/// After each change the recipe tools of the registry are reloaded through the delegate, so the
/// Tools tab, the sidebar and the palette follow without a restart. While the Recipes pack is off the
/// recipe is stored and not registered; while the organisation policy forbids the pack nothing is
/// imported (removing and exporting stay possible, since they only take things away or copy them out).
/// </remarks>
public sealed class RecipeSettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>The visual severity of the preview and of the removal confirmation.</summary>
    private const string ConfirmSeverityWarning = "warning";

    private readonly IRecipeStore _store;
    private readonly RecipeImportService _import;
    private readonly IConfigManager _configManager;
    private readonly IDialogService _dialogs;
    private readonly LocalizationManager _localizer;
    private readonly IRecipeFileDialogs _files;
    private readonly Action _reloadTools;
    private readonly IUiDispatcher? _uiDispatcher;
    private bool _disposed;
    private bool _loaded;

    /// <summary>Creates the view model.</summary>
    /// <param name="store">The imported recipes.</param>
    /// <param name="import">Prepares and stores an import.</param>
    /// <param name="configManager">Gives the folders the user allowed, as saved.</param>
    /// <param name="dialogs">Shows the preview, the refusals and the confirmations.</param>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="files">The file and folder pickers.</param>
    /// <param name="reloadTools">Registers the recipe tools again after a change.</param>
    /// <param name="uiDispatcher">Runs the rewording of the list on the UI thread when the language change arrives on another one; null runs it where it is raised.</param>
    public RecipeSettingsViewModel(
        IRecipeStore store,
        RecipeImportService import,
        IConfigManager configManager,
        IDialogService dialogs,
        LocalizationManager localizer,
        IRecipeFileDialogs files,
        Action reloadTools,
        IUiDispatcher? uiDispatcher = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _reloadTools = reloadTools ?? throw new ArgumentNullException(nameof(reloadTools));
        _uiDispatcher = uiDispatcher;

        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsForbiddenByPolicy);
        EnsureLoadedCommand = new RelayCommand(EnsureLoaded);
        _localizer.LocaleChanged += OnLocaleChanged;
    }

    /// <summary>The imported recipes, in the order of the index.</summary>
    public ObservableCollection<ImportedRecipeItemViewModel> Items { get; } = [];

    /// <summary>True when at least one recipe is imported.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>True when no recipe is imported: the list shows its empty line.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>True while the organisation policy forbids recipes: nothing can be imported.</summary>
    public bool IsForbiddenByPolicy => _import.IsForbiddenByPolicy;

    /// <summary>The line that says the policy forbids recipes; empty otherwise.</summary>
    public string PolicyText => IsForbiddenByPolicy ? _localizer["SettingsToolPackSetByPolicy"] : string.Empty;

    /// <summary>True when <see cref="PolicyText"/> has something to say.</summary>
    public bool HasPolicyText => IsForbiddenByPolicy;

    /// <summary>Picks a recipe file, shows the preview and, once confirmed, stores it.</summary>
    public IAsyncRelayCommand ImportCommand { get; }

    /// <summary>Reads the list the first time it is needed; the view runs it when the card is shown.</summary>
    public IRelayCommand EnsureLoadedCommand { get; }

    /// <summary>Reads the store and builds the list unless that was already done.</summary>
    public void EnsureLoaded()
    {
        if (!_loaded && !_disposed)
        {
            Refresh();
        }
    }

    /// <summary>Rebuilds the list when it was read before, and only says the words again otherwise.</summary>
    public void RefreshIfLoaded()
    {
        if (_loaded)
        {
            Refresh();
            return;
        }

        NotifyPolicy();
    }

    /// <summary>Reads the store again and rebuilds the list.</summary>
    public void Refresh()
    {
        _loaded = true;
        RecipeLoadResult loadResult = _store.LoadAll();
        LoadedRecipe[] loaded = [.. loadResult.Recipes];
        Dictionary<string, RecipeLoadIssueCode> issuesById = new(StringComparer.Ordinal);
        foreach (RecipeLoadIssue issue in loadResult.Issues)
        {
            if (issue.RecipeId is { } recipeId)
            {
                issuesById.TryAdd(recipeId, issue.Code);
            }
        }

        Items.Clear();
        foreach (RecipeStoreEntry entry in _store.List())
        {
            LoadedRecipe? match = loaded.FirstOrDefault(item => string.Equals(item.Recipe.Id, entry.Id, StringComparison.Ordinal));
            if (match is not null)
            {
                Items.Add(new ImportedRecipeItemViewModel(match, _localizer, ExportAsync, RemoveAsync));
                continue;
            }

            RecipeLoadIssueCode? issueCode = issuesById.TryGetValue(entry.Id, out RecipeLoadIssueCode code) ? code : null;
            Items.Add(new ImportedRecipeItemViewModel(entry, issueCode, _localizer, ExportAsync, RemoveAsync));
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        NotifyPolicy();
    }

    private void NotifyPolicy()
    {
        OnPropertyChanged(nameof(IsForbiddenByPolicy));
        OnPropertyChanged(nameof(PolicyText));
        OnPropertyChanged(nameof(HasPolicyText));
        ImportCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localizer.LocaleChanged -= OnLocaleChanged;
    }

    private void OnLocaleChanged(string locale)
    {
        // The localizer raises the change on the thread that finished loading the language file, and
        // the list is bound, so only the UI thread may touch it.
        if (_uiDispatcher is null)
        {
            RewordList();
            return;
        }

        _uiDispatcher.Invoke(RewordList);
    }

    private void RewordList()
    {
        foreach (ImportedRecipeItemViewModel item in Items)
        {
            item.RefreshTexts();
        }

        OnPropertyChanged(nameof(PolicyText));
    }

    private async Task ImportAsync()
    {
        if (IsForbiddenByPolicy)
        {
            return;
        }

        string? path = _files.PickRecipeToImport();
        if (path is null)
        {
            return;
        }

        // The folders as saved: that is what a run reads, so that is what the program must resolve against.
        AppSettings settings = await _configManager.LoadSettingsAsync().ConfigureAwait(true);
        IReadOnlyList<string> folders = settings.RecipeProgramFolders ?? [];
        RecipeImportOutcome outcome = await Task.Run(() => _import.PrepareFile(path, folders)).ConfigureAwait(true);

        if (outcome.Plan is not { } plan)
        {
            _dialogs.ShowError(_localizer[RecipeImportText.RefusedTitleKey], RecipeImportText.Describe(_localizer, outcome));
            return;
        }

        // The preview is the barrier: nothing is written until this returns true, and the button
        // that declines is the one Enter takes.
        bool confirmed = await _dialogs.ShowLargeConfirmAsync(
            _localizer[RecipeImportPreview.TitleKey(plan)],
            RecipeImportPreview.Compose(_localizer, plan),
            ConfirmSeverityWarning,
            _localizer[RecipeImportPreview.ConfirmKey(plan)],
            _localizer[RecipeImportPreview.CancelKey]).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        RecipeStoreResult result = _import.Commit(plan);
        if (!result.Succeeded)
        {
            Refresh();
            _dialogs.ShowError(_localizer[RecipeImportText.RefusedTitleKey], RecipeImportText.Describe(_localizer, result));
            return;
        }

        Refresh();
        _reloadTools();

        string name = RecipeText.Escape(plan.Recipe.Name, escapeNonAscii: false);
        string doneKey = plan.IsReplace ? "RecipeImportDoneReplaced" : "RecipeImportDone";
        string message = _localizer.Format(doneKey, name);
        if (!_import.IsPackEnabled)
        {
            message += Environment.NewLine + Environment.NewLine + _localizer["RecipeImportDonePackOff"];
        }

        _dialogs.ShowInfo(_localizer["RecipeImportDoneTitle"], message);
    }

    private Task ExportAsync(ImportedRecipeItemViewModel item)
    {
        byte[]? bytes = _store.ReadOriginalBytes(item.Id);
        if (bytes is null)
        {
            _dialogs.ShowError(_localizer["RecipeExportTitle"], _localizer["RecipeExportUnavailable"]);
            return Task.CompletedTask;
        }

        string? target = _files.PickRecipeExportTarget(item.Id + RecipeFormat.FileExtension);
        if (target is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            // The original bytes, not a re-serialization: the file that was reviewed is the file handed on.
            File.WriteAllBytes(target, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            FileLogger.Warn($"[Recipes] Recipe {item.Id} could not be exported");
            _dialogs.ShowError(_localizer["RecipeExportTitle"], _localizer["RecipeExportFailed"]);
            return Task.CompletedTask;
        }

        FileLogger.Info($"[Recipes] Recipe {item.Id} exported");
        _dialogs.ShowInfo(_localizer["RecipeExportTitle"], _localizer["RecipeExportDone"]);
        return Task.CompletedTask;
    }

    private async Task RemoveAsync(ImportedRecipeItemViewModel item)
    {
        bool confirmed = await _dialogs.ShowConfirmAsync(
            _localizer["RecipeRemoveTitle"],
            _localizer.Format("RecipeRemoveMessage", item.Name, item.Id),
            ConfirmSeverityWarning,
            _localizer["RecipeRemoveConfirm"],
            _localizer[RecipeImportPreview.CancelKey]).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        RecipeStoreResult result = _store.Remove(item.Id);
        FileLogger.Info($"[Recipes] Recipe {item.Id} removed: {result.Failure}");
        Refresh();
        if (!result.Succeeded)
        {
            _dialogs.ShowError(_localizer["RecipeRemoveTitle"], RecipeImportText.Describe(_localizer, result));
            return;
        }

        _reloadTools();
    }
}
