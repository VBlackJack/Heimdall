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
using CommunityToolkit.Mvvm.ComponentModel;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Lightweight view-model for a tool category header in the sidebar Tools tab.
/// Backs a <see cref="System.Windows.Controls.HierarchicalDataTemplate"/> that
/// shows [accent dot] [category name] [count badge] and nests the child tool
/// items. User-driven expansion state is persisted by stable category key.
/// </summary>
public sealed partial class SidebarToolCategoryViewModel : ObservableObject, IAccessibleItemViewModel
{
    /// <summary>Stable i18n category key used to persist expansion state.</summary>
    public required string CategoryKey { get; init; }

    /// <summary>Localized category display name (e.g. "Network").</summary>
    public required string CategoryName { get; init; }

    /// <summary>Theme resource key of the category accent brush (e.g. "ToolNetworkBrush").</summary>
    public required string BrushKey { get; init; }

    /// <summary>Tool items in the category, in sorted display order.</summary>
    public required ObservableCollection<SidebarToolItemViewModel> Tools { get; init; }

    /// <summary>Visible tool count (reflects any active filter).</summary>
    [ObservableProperty]
    private int _visibleCount;

    /// <summary>Expansion state, two-way bound from the TreeViewItem container.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Hide the category header entirely when no child passes the filter.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>
    /// The category name, which the tree container announces. Without it the container fell
    /// back to <c>ToString()</c> and a screen reader read out the view model's type name.
    /// </summary>
    public string AccessibleName => CategoryName;

    /// <inheritdoc/>
    public string? AccessibleHelpText => null;
}

/// <summary>
/// Lightweight view-model for a single tool leaf in the sidebar Tools tab.
/// </summary>
public sealed partial class SidebarToolItemViewModel : ObservableObject, IAccessibleItemViewModel
{
    /// <summary>Registry id, e.g. "PING". Used to resolve the descriptor on click.</summary>
    public required string Id { get; init; }

    /// <summary>Localized tool display name.</summary>
    public required string Name { get; init; }

    /// <summary>Theme resource key of the category brush (used to tint the icon).</summary>
    public required string BrushKey { get; init; }

    /// <summary>XAML resource key for the tool's vector <see cref="System.Windows.Media.Geometry"/>.</summary>
    public string? IconGeometryKey { get; init; }

    /// <summary>
    /// Lowercased searchable blob (name + palette command prefixes) used by the
    /// sidebar filter. Pre-computed so the filter loop is allocation-free.
    /// </summary>
    public required string Searchable { get; init; }

    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>
    /// Declared on the leaf type for binding-source symmetry with
    /// <see cref="SidebarToolCategoryViewModel"/>: the shared TreeViewItem style
    /// binds <c>IsExpanded</c> on every item and WPF would emit a runtime
    /// binding warning if the property were missing on leaves. Leaves have no
    /// children so the value is a no-op visually.
    /// </summary>
    public bool IsExpanded { get; set; }

    /// <summary>The tool's name, which the tree container announces.</summary>
    public string AccessibleName => Name;

    /// <inheritdoc/>
    public string? AccessibleHelpText => null;
}
