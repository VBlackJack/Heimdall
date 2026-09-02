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
using Heimdall.App.Services;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.ToolsTab;

/// <summary>
/// View-model backing the dedicated full-page Tools tab in <c>MainWindow</c>:
/// header text, search input, count label, network-context line and the
/// commands invoked by the rendered tool cards.
/// </summary>
/// <remarks>
/// <para>
/// Composition: instantiated inside <see cref="MainViewModel"/>'s constructor
/// (<see cref="MainViewModel.ToolsTab"/>) - there is no DI registration. This
/// matches the sidebar/onboarding sub-VM pattern.
/// </para>
/// <para>
/// Rendering split: the view-model owns state and commands; the actual card
/// layout is rendered programmatically into the <c>ToolsTabContent</c>
/// <see cref="System.Windows.Controls.Panel"/> by
/// <see cref="ToolsTabPopulationService.RefreshToolsTabSections"/>. The view
/// listens to <see cref="SectionsInvalidated"/> and re-runs that rendering.
/// This intentionally leaves the service untouched - refactoring the
/// imperative card rendering into a templated <c>ItemsControl</c> is tracked
/// as separate post-refactor tech debt.
/// </para>
/// </remarks>
public sealed partial class ToolsTabViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly LocalizationManager _localizer;
    private readonly IToolContextProvider _toolContext;
    private bool _disposed;

    /// <summary>
    /// Creates a new full-page Tools tab VM bound to the given host.
    /// </summary>
    public ToolsTabViewModel(
        MainViewModel main,
        LocalizationManager localizer,
        IToolContextProvider toolContext)
    {
        _main = main;
        _localizer = localizer;
        _toolContext = toolContext;
        _toolContext.PropertyChanged += OnToolContextPropertyChanged;
    }

    /// <summary>Current search text (two-way bound to the page's search TextBox).</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Localized page header (e.g. "Tools" / "Outils").</summary>
    [ObservableProperty]
    private string _headerTitle = string.Empty;

    /// <summary>Localized placeholder text bound to the search TextBox <c>Tag</c> (drives the watermark style).</summary>
    [ObservableProperty]
    private string _searchPlaceholder = string.Empty;

    /// <summary>Localized "{count} tools" label updated after each card rendering pass.</summary>
    [ObservableProperty]
    private string _countLabel = string.Empty;

    /// <summary>Localized "Network context: &lt;host&gt;" line shown above the cards.</summary>
    public string ContextLabel => _toolContext.ContextLabel;

    /// <summary>Same text as <see cref="ContextLabel"/>, used for the truncation tooltip.</summary>
    public string ContextTooltip => _toolContext.ContextTooltip;

    /// <summary>
    /// Resource key resolved into a <see cref="System.Windows.Media.Brush"/>
    /// at bind time via <c>ResourceKeyToBrushConverter</c>. Toggled between
    /// <c>"AccentBrush"</c> (a target host is inherited) and
    /// <c>"TextDisabledBrush"</c> (no target host) to mirror the previous
    /// imperative <c>SetResourceReference</c> behavior.
    /// </summary>
    public string ContextBrushKey => _toolContext.ContextBrushKey;

    /// <summary>
    /// Raised whenever the rendered card layout needs a full re-render. The
    /// host window listens to this and re-invokes
    /// <see cref="ToolsTabPopulationService.RefreshToolsTabSections"/>.
    /// </summary>
    public event EventHandler? SectionsInvalidated;

    /// <summary>
    /// Re-reads <see cref="HeaderTitle"/> and <see cref="SearchPlaceholder"/>
    /// from the active locale. Call after a locale switch.
    /// </summary>
    public void RefreshHeaderText()
    {
        HeaderTitle = _localizer["ToolsTabTitle"];
        SearchPlaceholder = _localizer["ToolsTabSearchPlaceholder"];
    }

    /// <summary>
    /// Updates <see cref="CountLabel"/> from the visible-count returned by
    /// the rendering service after a section refresh.
    /// </summary>
    public void UpdateVisibleCount(int visibleCount)
    {
        CountLabel = _localizer["ToolsToolCount"].Replace("{0}", visibleCount.ToString());
    }

    /// <summary>
    /// Raises <see cref="SectionsInvalidated"/>. Callers in the host window
    /// re-render the cards in response.
    /// </summary>
    public void InvalidateSections() => SectionsInvalidated?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Invalidates the header, count and rendered cards after the external
    /// tool provider scan reports new tools. Also re-localizes the header
    /// in case the count format string changed locale between scans.
    /// </summary>
    public void OnExternalToolsChanged()
    {
        RefreshHeaderText();
        InvalidateSections();
    }

    /// <summary>
    /// Generated partial: re-renders the card sections live as the user
    /// types in the search box. The two-way binding pushes every keystroke
    /// (the XAML uses <c>UpdateSourceTrigger=PropertyChanged</c>).
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        InvalidateSections();
    }

    private void OnToolContextPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(IToolContextProvider.ContextLabel), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ContextLabel));
        }
        else if (string.Equals(e.PropertyName, nameof(IToolContextProvider.ContextTooltip), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ContextTooltip));
        }
        else if (string.Equals(e.PropertyName, nameof(IToolContextProvider.ContextBrushKey), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ContextBrushKey));
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
        _toolContext.PropertyChanged -= OnToolContextPropertyChanged;
    }
}
