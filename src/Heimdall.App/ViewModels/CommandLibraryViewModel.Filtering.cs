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

using System.ComponentModel;
using System.Windows.Data;
using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Enums;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Filtering and search ranking partial of <see cref="CommandLibraryViewModel"/>.
/// Owns the debounced relevance search against <see cref="ISearchService"/>
/// and the synchronous predicate consumed by the view's <c>ICollectionView</c>.
/// </summary>
public sealed partial class CommandLibraryViewModel
{
    /// <summary>
    /// Default debounce delay (ms) before issuing a relevance search after the
    /// user pauses typing. Mirrors the original code-behind value.
    /// </summary>
    private const int SearchDebounceMs = 200;

    /// <summary>
    /// Debounces and runs a relevance search for <paramref name="term"/>. The
    /// VM owns the cancellation token source so rapid typing collapses into a
    /// single executed query. After this returns the view should refresh its
    /// <c>ICollectionView</c> and call <see cref="ResultCountText"/> for the
    /// header line.
    /// </summary>
    /// <param name="term">Raw user input. Whitespace is trimmed.</param>
    public async Task ApplySearchAsync(string term)
    {
        if (!ReferenceEquals(SearchText, term ?? string.Empty) && SearchText != (term ?? string.Empty))
        {
            SearchText = term ?? string.Empty;
        }
        var trimmed = (term ?? string.Empty).Trim();

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            await Task.Delay(SearchDebounceMs, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (trimmed.Length == 0)
        {
            _lastSearchTerm = string.Empty;
            _searchRankedIds = null;
            _searchMatchIds = null;
            ApplyGroupingForSearch(isSearching: false);
            RefreshActionsView();
            return;
        }

        if (trimmed == _lastSearchTerm)
        {
            // Same query - no work needed, but ensure the view still reflects
            // ordering (e.g., after a selection toggled it off and on).
            ApplyGroupingForSearch(isSearching: true);
            RefreshActionsView();
            return;
        }

        if (_searchService is null) return;

        _lastSearchTerm = trimmed;

        try
        {
            var sources = _allEntries.Select(static e => e.Source).ToList();
            var ranked = (await _searchService.SearchAsync(sources, trimmed)).ToList();
            _searchRankedIds = ranked.Select(static r => r.Id).ToList();
            _searchMatchIds = new HashSet<string>(_searchRankedIds, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Search failed: {ex.Message}");
            _searchRankedIds = null;
            _searchMatchIds = null;
        }

        ApplyGroupingForSearch(isSearching: _searchMatchIds is not null);
        RefreshActionsView();
    }

    /// <summary>
    /// Switches the <see cref="ActionsView"/> between grouped-by-category
    /// (no search) and flat-sorted-by-rank (active search). Moved from the
    /// view's imperative path so Phase B XAML does not need to touch the
    /// collection view directly.
    /// </summary>
    private void ApplyGroupingForSearch(bool isSearching)
    {
        if (_actionsView is null) return;

        if (isSearching)
        {
            _actionsView.GroupDescriptions.Clear();
            _actionsView.SortDescriptions.Clear();
            _actionsView.SortDescriptions.Add(
                new SortDescription(
                    nameof(CommandLibraryActionEntry.SearchRank),
                    ListSortDirection.Ascending));
        }
        else if (_actionsView.GroupDescriptions.Count == 0)
        {
            _actionsView.SortDescriptions.Clear();
            _actionsView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(CommandLibraryActionEntry.Category)));
        }
    }

    /// <summary>
    /// Synchronous filter predicate consumed by the view's
    /// <c>CollectionViewSource</c>. Combines platform / risk / category /
    /// favorites toggles and the active relevance search.
    /// </summary>
    /// <param name="entry">The candidate entry.</param>
    /// <returns>True if the entry should be visible.</returns>
    public bool ShouldShowAction(CommandLibraryActionEntry entry)
    {
        // Platform filter (1 = Windows, 2 = Linux, "Both" passes either)
        if (PlatformFilterIndex == 1
            && entry.Source.Platform != Platform.Windows
            && entry.Source.Platform != Platform.Both)
        {
            return false;
        }
        if (PlatformFilterIndex == 2
            && entry.Source.Platform != Platform.Linux
            && entry.Source.Platform != Platform.Both)
        {
            return false;
        }

        // Risk filter (0 = All, 1+ = CriticalityLevel offset by 1)
        if (RiskFilterIndex > 0)
        {
            var expectedLevel = (CriticalityLevel)(RiskFilterIndex - 1);
            if (entry.Source.Level != expectedLevel)
            {
                return false;
            }
        }

        // Favorites toggle (orthogonal to other filters)
        if (FavoritesFilterActive && !_favoriteIds.Contains(entry.Source.Id))
        {
            return false;
        }

        // Category filter (0 = All, 1..N = index into Categories)
        if (CategoryFilterIndex > 0)
        {
            var idx = CategoryFilterIndex - 1;
            if (idx >= _categoryList.Count)
            {
                return false;
            }
            if (!string.Equals(entry.Source.Category, _categoryList[idx], StringComparison.Ordinal))
            {
                return false;
            }
        }

        // Search filter (only consider entries the search service ranked)
        if (_searchMatchIds is not null && !_searchMatchIds.Contains(entry.Source.Id))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Computes the localized "X of Y results" header text. <paramref name="visibleCount"/>
    /// is the number the view's <c>ICollectionView</c> currently renders.
    /// </summary>
    public string FormatResultCount(int visibleCount)
        => string.Format(LocalizeKey("ToolCmdLibResultCountFormat"), visibleCount, _allEntries.Count);

    /// <summary>
    /// True when there are active filter or search constraints that would
    /// hide entries. Used by the view to choose between the "no results" and
    /// "empty library" states.
    /// </summary>
    public bool HasActiveFilters
        => FavoritesFilterActive
        || CategoryFilterIndex > 0
        || PlatformFilterIndex > 0
        || RiskFilterIndex > 0
        || !string.IsNullOrEmpty(_lastSearchTerm);

    private void ResetSearchState()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _searchRankedIds = null;
        _searchMatchIds = null;
        _lastSearchTerm = string.Empty;
        SearchText = string.Empty;
    }
}
