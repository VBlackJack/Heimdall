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

using System.Globalization;
using System.IO;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;
using AppDialogService = Heimdall.App.Services.IDialogService;

namespace Heimdall.App.Tests;

internal static class CommandLibraryTestHelpers
{
    public static async Task<LocalizationManager> CreateAppLocalizerAsync()
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return manager;
    }

    public static IServiceProvider CreateResolverServiceProvider(params ActionModel[] actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(actions));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();
        return services.BuildServiceProvider();
    }

    public static ActionModel CreateLinuxAction(
        string id,
        string title,
        string pattern,
        params TemplateParameter[] parameters)
    {
        return new ActionModel
        {
            Id = id,
            Title = title,
            Category = "Linux",
            Platform = Platform.Linux,
            LinuxCommandTemplate = new CommandTemplate
            {
                Id = $"{id}-linux",
                Name = title,
                Platform = Platform.Linux,
                CommandPattern = pattern,
                Parameters = [.. parameters]
            }
        };
    }

    public static TemplateParameter RequiredParameter(string name, string label, string type = "string")
        => new()
        {
            Name = name,
            Label = label,
            Type = type,
            Required = true
        };

    public static TemplateParameter OptionalParameter(string name, string label, string? defaultValue = null, string type = "string")
        => new()
        {
            Name = name,
            Label = label,
            Type = type,
            DefaultValue = defaultValue,
            Required = false
        };
}

internal sealed class FakeActionService(IEnumerable<ActionModel> actions) : IActionService
{
    private readonly List<ActionModel> _actions = [.. actions];

    public Task<IEnumerable<ActionModel>> GetAllActionsAsync() =>
        Task.FromResult<IEnumerable<ActionModel>>(_actions);

    public Task<ActionModel?> GetActionByIdAsync(string id) =>
        Task.FromResult(_actions.FirstOrDefault(action => string.Equals(action.Id, id, StringComparison.Ordinal)));

    public Task<ActionModel?> GetActionByPublicIdAsync(Guid publicId) =>
        Task.FromResult(_actions.FirstOrDefault(action => action.PublicId == publicId));

    public Task<IEnumerable<ActionModel>> GetActionsByCategoryAsync(string category) =>
        Task.FromResult<IEnumerable<ActionModel>>(
            _actions.Where(action => string.Equals(action.Category, category, StringComparison.OrdinalIgnoreCase)).ToList());

    public Task<IEnumerable<string>> GetAllCategoriesAsync() =>
        Task.FromResult<IEnumerable<string>>(_actions.Select(action => action.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    public Task<IEnumerable<ActionModel>> FilterActionsAsync(IEnumerable<ActionModel> actions, Platform? platform = null, CriticalityLevel? level = null)
    {
        var query = actions;
        if (platform is not null)
        {
            query = query.Where(action => action.Platform == platform.Value || action.Platform == Platform.Both);
        }

        if (level is not null)
        {
            query = query.Where(action => action.Level == level.Value);
        }

        return Task.FromResult<IEnumerable<ActionModel>>(query.ToList());
    }

    public Task<ActionModel> CreateActionAsync(ActionModel action)
    {
        _actions.Add(action);
        return Task.FromResult(action);
    }

    public Task UpdateActionAsync(ActionModel action)
    {
        var index = _actions.FindIndex(existing => string.Equals(existing.Id, action.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            _actions[index] = action;
        }

        return Task.CompletedTask;
    }

    public Task DeleteActionAsync(string id)
    {
        _actions.RemoveAll(action => string.Equals(action.Id, id, StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    public Task<int> GetActionCountByCategoryAsync(string category) =>
        Task.FromResult(_actions.Count(action => string.Equals(action.Category, category, StringComparison.OrdinalIgnoreCase)));

    public Task<bool> RenameCategoryAsync(string oldName, string newName)
    {
        var matched = false;
        foreach (var action in _actions.Where(action => string.Equals(action.Category, oldName, StringComparison.OrdinalIgnoreCase)))
        {
            action.Category = newName;
            matched = true;
        }

        return Task.FromResult(matched);
    }

    public Task<bool> DeleteCategoryAsync(string categoryName)
    {
        var matched = false;
        foreach (var action in _actions.Where(action => string.Equals(action.Category, categoryName, StringComparison.OrdinalIgnoreCase)))
        {
            action.Category = string.Empty;
            matched = true;
        }

        return Task.FromResult(matched);
    }
}

internal sealed class FakeFavoritesService : IFavoritesService
{
    private readonly HashSet<string> _favorites = new(StringComparer.Ordinal);

    public Task<(bool Success, string? ErrorMessage)> AddFavoriteAsync(string actionId, string? userId = null)
    {
        _favorites.Add(actionId);
        return Task.FromResult((true, (string?)null));
    }

    public Task RemoveFavoriteAsync(string actionId, string? userId = null)
    {
        _favorites.Remove(actionId);
        return Task.CompletedTask;
    }

    public Task<bool> ToggleFavoriteAsync(string actionId, string? userId = null)
    {
        if (!_favorites.Add(actionId))
        {
            _favorites.Remove(actionId);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public Task<bool> IsFavoriteAsync(string actionId, string? userId = null)
        => Task.FromResult(_favorites.Contains(actionId));

    public Task<IEnumerable<UserFavorite>> GetAllFavoritesAsync(string? userId = null)
        => Task.FromResult<IEnumerable<UserFavorite>>(
            _favorites.Select((actionId, index) => new UserFavorite
            {
                ActionId = actionId,
                DisplayOrder = index
            }).ToList());

    public Task<int> GetFavoriteCountAsync(string? userId = null)
        => Task.FromResult(_favorites.Count);

    public Task ReorderFavoriteAsync(string favoriteId, int newOrder)
        => Task.CompletedTask;

    public Task ClearAllFavoritesAsync(string? userId = null)
    {
        _favorites.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class FakeTwinShellLocalizationService : ILocalizationService
{
    public CultureInfo CurrentCulture => CultureInfo.InvariantCulture;

    public CultureInfo[] SupportedCultures => [CultureInfo.InvariantCulture];

    public event EventHandler? LanguageChanged;

    public void ChangeLanguage(CultureInfo culture) => LanguageChanged?.Invoke(this, EventArgs.Empty);

    public void ChangeLanguage(string cultureCode) => LanguageChanged?.Invoke(this, EventArgs.Empty);

    public string GetString(string key) => key;

    public string GetString(string key, string fallback) => fallback;

    public string GetFormattedString(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, key, args);
}

/// <summary>
/// Dialog service double that answers every prompt without any UI and records which
/// message boxes were raised, so a test can assert what the user would have been told.
/// </summary>
internal sealed class SilentDialogService : AppDialogService
{
    /// <summary>Answer every confirmation prompt with this value.</summary>
    public bool ConfirmResult { get; init; }

    /// <summary>
    /// Every message box raised, as "<c>kind:title</c>" in the order it was shown.
    /// </summary>
    public List<string> Shown { get; } = [];

    /// <summary>Every confirmation body, in the order it was shown.</summary>
    public List<string> ConfirmMessages { get; } = [];

    public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
    {
        Shown.Add($"confirm:{title}");
        ConfirmMessages.Add(message);
        return Task.FromResult(ConfirmResult);
    }

    public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
        => Task.FromResult<bool?>(false);

    public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
        => Task.FromResult<string?>(defaultValue);

    public Task<string?> ShowPasswordInputAsync(
        string title,
        string prompt,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
        => Task.FromResult<ServerDialogResult?>(null);

    public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
        => Task.FromResult<GatewayDialogResult?>(null);

    public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
        => Task.FromResult<ProjectDialogResult?>(null);

    public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
        => Task.FromResult<ScheduledTaskDialogResult?>(null);

    public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
        => Task.CompletedTask;

    public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
        => Task.FromResult<PinSetupResult?>(null);

    public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
        => Task.FromResult<SnapshotRestoreDialogResult?>(null);

    public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
        => Task.FromResult<RdpImportSelection?>(null);

    public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
        => Task.FromResult<ImportOutcome?>(null);

    public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
        => Task.FromResult<ImportOutcome?>(null);

    public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
        => Task.FromResult<KnownHostsImportOutcome?>(null);

    public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
        => Task.CompletedTask;

    public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
        ImportKnownHostsConflictDialogViewModel viewModel)
        => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

    public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
        CommandLibraryPickerDialogViewModel viewModel,
        AutoPrefillContext? prefillContext = null,
        string? existingActionId = null,
        IReadOnlyDictionary<string, string>? existingValues = null)
        => Task.FromResult<CommandLibraryPickerResult?>(null);

    public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
        => Task.FromResult<int?>(null);

    public Task<string?> ShowBulkEditUsernameAsync(
        int count,
        string? initialUsername,
        CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public void ShowError(string title, string message) => Shown.Add($"error:{title}");

    public void ShowInfo(string title, string message) => Shown.Add($"info:{title}");

    public void ShowWarning(string title, string message) => Shown.Add($"warning:{title}");
}
