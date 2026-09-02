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
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels.Sidebar;

/// <summary>
/// View-model backing the left-hand sidebar panel of <c>MainWindow</c>:
/// Sessions/Tools tab toggle, persistence of the active tab, lazy build
/// of the Tools tree, filter, network-context label and tool launch.
/// </summary>
/// <remarks>
/// <para>
/// Composition: instantiated inside <see cref="MainViewModel"/>'s constructor
/// (<see cref="MainViewModel.Sidebar"/>) - there is no DI registration. This
/// avoids a cyclic dependency between the sub-VM and its host.
/// </para>
/// <para>
/// Persistence guard: <see cref="EnablePersistence"/> must be called once
/// from the host window's <c>Loaded</c> handler, after the saved tab choice
/// has been restored. Tab changes that happen before that call (XAML default
/// <c>IsChecked="True"</c> on the Sessions radio button) do not write to
/// <see cref="AppSettings.ShowToolsPanel"/>.
/// </para>
/// </remarks>
public sealed partial class SidebarViewModel : ObservableObject, IDisposable
{
    private const string FavoritesCategoryKey = "ToolsFavoritesHeader";

    private readonly MainViewModel _main;
    private readonly LocalizationManager _localizer;
    private readonly IConfigManager _configManager;
    private readonly ToolsTabPopulationService _toolsTabPopulation;
    private readonly IToolContextProvider _toolContext;
    private readonly IUiDispatcher _uiDispatcher;

    private bool _isToolsPopulated;
    private bool _persistenceEnabled;
    private bool _suppressCategoryExpansionPersistence;
    private bool _disposed;

    /// <summary>
    /// Creates a new sidebar VM bound to the given host.
    /// </summary>
    public SidebarViewModel(
        MainViewModel main,
        LocalizationManager localizer,
        IConfigManager configManager,
        ToolsTabPopulationService toolsTabPopulation,
        IToolContextProvider toolContext,
        IUiDispatcher uiDispatcher)
    {
        _main = main;
        _localizer = localizer;
        _configManager = configManager;
        _toolsTabPopulation = toolsTabPopulation;
        _toolContext = toolContext;
        _uiDispatcher = uiDispatcher;
        _toolContext.PropertyChanged += OnToolContextPropertyChanged;
        _main.FavoritesChanged += OnFavoritesChanged;
    }

    /// <summary>
    /// True when the Tools tab is the active sidebar tab. The setter is
    /// driven exclusively through <see cref="SetActiveTab(bool)"/> so that
    /// the lazy populate + persistence side effects run consistently.
    /// </summary>
    [ObservableProperty]
    private bool _isToolsTabActive;

    /// <summary>Current filter text (two-way bound to the sidebar Tools filter TextBox).</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>Localized "Network context: &lt;host&gt;" label shown above the Tools tree.</summary>
    public string ContextLabel => _toolContext.ContextLabel;

    /// <summary>
    /// Same text as <see cref="ContextLabel"/>, exposed separately so the
    /// truncated TextBlock's ToolTip stays in sync.
    /// </summary>
    public string ContextTooltip => _toolContext.ContextTooltip;

    /// <summary>True when the active filter hides every tool - drives the "no results" hint.</summary>
    [ObservableProperty]
    private bool _hasNoResults;

    /// <summary>Localized "no tools match your filter" hint text.</summary>
    [ObservableProperty]
    private string _noResultsText = string.Empty;

    /// <summary>
    /// Tool category hierarchy bound to the sidebar Tools <c>TreeView</c>.
    /// Built lazily on the first <see cref="SetActiveTab(bool)"/> with
    /// <c>isTools=true</c>, refreshed on
    /// <see cref="OnExternalToolsChanged"/>.
    /// </summary>
    public ObservableCollection<SidebarToolCategoryViewModel> ToolsCategories { get; } = new();

    /// <summary>
    /// Unified entry point for tab changes. Drives lazy population on the
    /// first transition into the Tools tab, refreshes the context label,
    /// and persists the choice (once persistence has been enabled).
    /// </summary>
    public void SetActiveTab(bool isTools)
    {
        IsToolsTabActive = isTools;

        if (isTools)
        {
            EnsurePopulated();
        }

        PersistTabChoice(isTools);
    }

    /// <summary>
    /// Releases the persistence guard. Called by the host window after the
    /// startup XAML default (<c>SidebarTabSessions IsChecked="True"</c>)
    /// has been overridden with the restored value from
    /// <see cref="AppSettings.ShowToolsPanel"/>.
    /// </summary>
    public void EnablePersistence() => _persistenceEnabled = true;

    /// <summary>
    /// Launches a sidebar tool: resolves the descriptor in
    /// <see cref="MainViewModel.ToolRegistry"/>, builds the inherited
    /// <see cref="ToolContext"/>, opens the tool tab via
    /// <see cref="MainViewModel.OpenToolTabAsync"/> and tracks it as
    /// recent. View-layer tab-strip navigation
    /// (<c>TabSessions.IsChecked = true</c>) stays in the host window.
    /// </summary>
    public async Task LaunchToolAsync(SidebarToolItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var descriptor = _main.ToolRegistry.All.FirstOrDefault(
            d => string.Equals(d.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return;
        }

        try
        {
            var context = ToolsTabPopulationService.CreateInheritedToolContext(descriptor, _main);
            var title = ToolsTabPopulationService.ResolveToolTabTitle(descriptor, context, _main);
            await _main.OpenToolTabAsync(descriptor.Id, title, context).ConfigureAwait(true);
            _main.TrackRecentTool(descriptor.Id);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Sidebar tool launch failed: {descriptor.Id}", ex);
            _main.StatusText = string.Format(_main.Localize("ErrorToolLaunchFailed"), descriptor.Id, ex.Message);
        }
    }

    /// <summary>
    /// Invalidates the cached Tools tree. If the Tools tab is currently
    /// active the tree is rebuilt immediately; otherwise the rebuild is
    /// deferred until the user next switches to it. Called by the host
    /// window when the external tools provider scan reports new tools.
    /// </summary>
    public void OnExternalToolsChanged()
    {
        _isToolsPopulated = false;
        if (IsToolsTabActive)
        {
            EnsurePopulated();
        }
    }

    /// <summary>
    /// Re-runs the active filter against the cached
    /// <see cref="ToolsCategories"/>, updating
    /// <see cref="HasNoResults"/> and <see cref="NoResultsText"/>.
    /// Invoked automatically whenever <see cref="FilterText"/> changes.
    /// </summary>
    public void InvalidateFilter()
    {
        if (!_isToolsPopulated)
        {
            HasNoResults = false;
            return;
        }

        var hasFilter = !string.IsNullOrWhiteSpace(FilterText);

        // The filter transiently expands/collapses categories; those UI-only
        // mutations must not overwrite the user's saved expansion choices.
        _suppressCategoryExpansionPersistence = true;
        bool anyVisibleTool;
        try
        {
            anyVisibleTool = _toolsTabPopulation.FilterSidebarTools(ToolsCategories, FilterText);
            if (!hasFilter)
            {
                RestoreCategoryExpansionState();
            }
        }
        finally
        {
            _suppressCategoryExpansionPersistence = false;
        }

        NoResultsText = _localizer["ToolsNoResults"];
        HasNoResults = hasFilter && !anyVisibleTool;
    }

    /// <summary>
    /// Generated partial: re-runs the filter whenever the user types in the
    /// filter TextBox. The two-way binding pushes every keystroke (the XAML
    /// uses <c>UpdateSourceTrigger=PropertyChanged</c>).
    /// </summary>
    partial void OnFilterTextChanged(string value)
    {
        InvalidateFilter();
    }

    private void EnsurePopulated()
    {
        if (_isToolsPopulated)
        {
            return;
        }

        var built = _toolsTabPopulation.BuildSidebarToolsData(_main);
        UnsubscribeCategoryExpansionHandlers();
        ToolsCategories.Clear();
        foreach (var category in built)
        {
            category.PropertyChanged += OnCategoryPropertyChanged;
            ToolsCategories.Add(category);
        }

        _isToolsPopulated = true;

        // Re-apply the current filter so any pre-existing FilterText
        // (typed before the tree was built) takes effect immediately.
        InvalidateFilter();
    }

    private void OnFavoritesChanged(string toolId)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _ = _uiDispatcher.InvokeAsync(() => OnFavoritesChanged(toolId));
            return;
        }

        if (!_isToolsPopulated)
        {
            return;
        }

        var isFavorited = _main.FavoriteToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase);
        var favoritesCategory = isFavorited ? EnsureFavoritesCategory() : FindFavoritesCategory();
        if (favoritesCategory is null)
        {
            return;
        }

        if (isFavorited)
        {
            AddFavoriteTool(favoritesCategory, toolId);
        }
        else
        {
            RemoveFavoriteTool(favoritesCategory, toolId);
        }

        InvalidateFilter();
    }

    private async void PersistTabChoice(bool isTools)
    {
        if (!_persistenceEnabled) return;
        if (_main.CurrentSettings is null) return;
        if (_main.CurrentSettings.ShowToolsPanel == isTools) return;

        _main.CurrentSettings.ShowToolsPanel = isTools;
        await _configManager.MergeSettingAsync(s => s.ShowToolsPanel = isTools).ConfigureAwait(true);
    }

    private async void OnCategoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(SidebarToolCategoryViewModel.IsExpanded), StringComparison.Ordinal))
        {
            return;
        }

        if (_suppressCategoryExpansionPersistence) return;
        if (!_persistenceEnabled) return;
        if (_main.CurrentSettings is null) return;
        if (sender is not SidebarToolCategoryViewModel category) return;

        var categoryKey = category.CategoryKey;
        var isExpanded = category.IsExpanded;
        if (_main.CurrentSettings.SidebarExpandedCategories.TryGetValue(categoryKey, out var current)
            && current == isExpanded)
        {
            return;
        }

        _main.CurrentSettings.SidebarExpandedCategories[categoryKey] = isExpanded;
        await _configManager.MergeSettingAsync(
            settings => settings.SidebarExpandedCategories[categoryKey] = isExpanded).ConfigureAwait(true);
    }

    private void OnToolContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(IToolContextProvider.ContextLabel), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ContextLabel));
        }
        else if (string.Equals(e.PropertyName, nameof(IToolContextProvider.ContextTooltip), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ContextTooltip));
        }
    }

    private void RestoreCategoryExpansionState()
    {
        var persisted = _main.CurrentSettings?.SidebarExpandedCategories;
        foreach (var category in ToolsCategories)
        {
            category.IsExpanded = persisted?.TryGetValue(category.CategoryKey, out var expanded) == true
                ? expanded
                : true;
        }
    }

    private void UnsubscribeCategoryExpansionHandlers()
    {
        foreach (var category in ToolsCategories)
        {
            category.PropertyChanged -= OnCategoryPropertyChanged;
        }
    }

    private SidebarToolCategoryViewModel EnsureFavoritesCategory()
    {
        var existing = FindFavoritesCategory();
        if (existing is not null)
        {
            return existing;
        }

        var category = _toolsTabPopulation.CreateSidebarFavoritesCategory(_main);
        category.PropertyChanged += OnCategoryPropertyChanged;
        ToolsCategories.Insert(0, category);
        return category;
    }

    private SidebarToolCategoryViewModel? FindFavoritesCategory()
    {
        return ToolsCategories.FirstOrDefault(
            category => string.Equals(category.CategoryKey, FavoritesCategoryKey, StringComparison.Ordinal));
    }

    private void AddFavoriteTool(SidebarToolCategoryViewModel favoritesCategory, string toolId)
    {
        if (favoritesCategory.Tools.Any(
            tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var toolItem = _toolsTabPopulation.CreateSidebarToolItem(_main, toolId);
        if (toolItem is null)
        {
            return;
        }

        var insertIndex = 0;
        while (insertIndex < favoritesCategory.Tools.Count
               && StringComparer.OrdinalIgnoreCase.Compare(
                   favoritesCategory.Tools[insertIndex].Name,
                   toolItem.Name) < 0)
        {
            insertIndex++;
        }

        favoritesCategory.Tools.Insert(insertIndex, toolItem);
    }

    private static void RemoveFavoriteTool(
        SidebarToolCategoryViewModel favoritesCategory,
        string toolId)
    {
        var existing = favoritesCategory.Tools.FirstOrDefault(
            tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            favoritesCategory.Tools.Remove(existing);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _main.FavoritesChanged -= OnFavoritesChanged;
        _toolContext.PropertyChanged -= OnToolContextPropertyChanged;
        UnsubscribeCategoryExpansionHandlers();
    }
}
