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
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// Display-side wrapper around a TwinShell <see cref="ActionModel"/>.
/// Holds a back-reference to the owning <see cref="CommandLibraryViewModel"/>
/// so that derived display properties (favorite icon, search rank, localized
/// risk/platform labels) can be computed from current VM state at bind time.
/// </summary>
/// <remarks>
/// Phase A keeps the imperative <c>ICollectionView.Refresh()</c> trigger that
/// re-evaluates getters; the type only inherits <see cref="ObservableObject"/>
/// to make Phase B's binding migration trivial.
/// </remarks>
public sealed partial class CommandLibraryActionEntry : ObservableObject
{
    private readonly CommandLibraryViewModel _viewModel;

    /// <summary>
    /// Creates a new display entry bound to the given action and owning VM.
    /// </summary>
    /// <param name="source">Underlying TwinShell action model.</param>
    /// <param name="viewModel">VM providing favorite/search/locale state.</param>
    public CommandLibraryActionEntry(ActionModel source, CommandLibraryViewModel viewModel)
    {
        Source = source;
        _viewModel = viewModel;
    }

    /// <summary>The underlying TwinShell action model.</summary>
    public ActionModel Source { get; }

    /// <summary>Action title (passthrough).</summary>
    public string Title => Source.Title;

    /// <summary>Action description; empty string if none.</summary>
    public string Description => Source.Description ?? string.Empty;

    /// <summary>Action category, used for grouping and filtering.</summary>
    public string Category => Source.Category;

    /// <summary>Filled or hollow star glyph reflecting the current favorite state.</summary>
    public string FavoriteIcon => _viewModel.IsFavorite(Source.Id) ? "\u2605" : "\u2606";

    /// <summary>Localized tooltip describing the favorite-toggle action.</summary>
    public string FavoriteTooltip => _viewModel.IsFavorite(Source.Id)
        ? _viewModel.LocalizeKey("ToolCmdLibFavoriteRemove")
        : _viewModel.LocalizeKey("ToolCmdLibFavoriteAdd");

    /// <summary>
    /// Zero-based search rank, or <see cref="int.MaxValue"/> when no search is
    /// active so the default category sort wins.
    /// </summary>
    public int SearchRank => _viewModel.GetSearchRank(Source.Id);

    /// <summary>
    /// Localized platform label (Windows / Linux / Both). Delegates to
    /// <see cref="CommandPresentationResolver.ResolvePlatformLabel"/>, the single
    /// source of truth for this mapping.
    /// </summary>
    public string PlatformLabel =>
        CommandPresentationResolver.ResolvePlatformLabel(Source.Platform, _viewModel.LocalizeKey);

    /// <summary>
    /// Theme resource key representing the action's risk level. Delegates to
    /// <see cref="CommandPresentationResolver.ResolveRiskBrushKey"/>, the single
    /// source of truth for this mapping.
    /// </summary>
    public string RiskBrushKey =>
        CommandPresentationResolver.ResolveRiskBrushKey(Source.Level);

    /// <summary>
    /// Localized long-form risk label, used for tooltips. Delegates to
    /// <see cref="CommandPresentationResolver.ResolveRiskLabel"/>, the single
    /// source of truth for this mapping.
    /// </summary>
    public string RiskLabel =>
        CommandPresentationResolver.ResolveRiskLabel(Source.Level, _viewModel.LocalizeKey);

    /// <summary>
    /// Short risk badge text shown in the action list. Delegates to
    /// <see cref="CommandPresentationResolver.ResolveRiskBadge"/>, the single
    /// source of truth for this mapping.
    /// </summary>
    public string RiskBadge =>
        CommandPresentationResolver.ResolveRiskBadge(Source.Level, _viewModel.LocalizeKey);
}
