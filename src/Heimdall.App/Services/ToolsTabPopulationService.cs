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

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Heimdall.App.ViewModels;
using Heimdall.Core.Models;
using Control = System.Windows.Controls.Control;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Heimdall.App.Services;

/// <summary>
/// Builds and refreshes the full-page Tools tab and the sidebar Tools panel.
/// Owns card creation, section layout, category grouping, favorites/recents
/// display, search filtering, and tool-context inheritance logic. Extracted
/// from <c>MainWindow.xaml.cs</c> to reduce code-behind size.
/// </summary>
public sealed class ToolsTabPopulationService
{
    private readonly ToolRegistry _toolRegistry;

    public sealed record ExternalToolProviderGroup(
        string ProviderName,
        IReadOnlyList<ToolDescriptor> Tools);

    /// <summary>
    /// Initialises a new <see cref="ToolsTabPopulationService"/>.
    /// </summary>
    /// <param name="toolRegistry">
    /// Single source of truth for the list of available tools (built-in +
    /// dynamically-registered external providers).
    /// </param>
    public ToolsTabPopulationService(ToolRegistry toolRegistry)
    {
        _toolRegistry = toolRegistry;
    }

    // ── Sidebar Tools panel ─────────────────────────────────────────────

    /// <summary>
    /// Builds the category/tool hierarchy from <see cref="ToolRegistry"/> for
    /// the sidebar Tools <see cref="TreeView"/>. The resulting collection is
    /// returned so the caller can bind it to the view and keep a reference
    /// for later filtering.
    /// </summary>
    public ObservableCollection<SidebarToolCategoryViewModel> BuildSidebarToolsData(MainViewModel vm)
    {
        var grouped = _toolRegistry.All
            .GroupBy(d => d.Category)
            .OrderBy(g => g.Key);

        var categories = new ObservableCollection<SidebarToolCategoryViewModel>();
        categories.Add(CreateSidebarFavoritesCategory(vm));

        foreach (var group in grouped)
        {
            var categoryKey = group.First().CategoryLabelKey;
            var categoryName = vm.Localize(categoryKey);
            var brushKey = GetCategoryBrushKey(group.Key);
            var tools = new ObservableCollection<SidebarToolItemViewModel>(
                group.Select(descriptor => CreateSidebarToolItem(vm, descriptor))
                    .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase));

            categories.Add(CreateSidebarCategory(vm, categoryKey, categoryName, brushKey, tools));
        }

        return categories;
    }

    /// <summary>
    /// Builds the dedicated Favorites category for the sidebar Tools tree.
    /// Favorite items keep their original tool brush/icon while the category
    /// header uses the warning accent to stand out at the top of the tree.
    /// </summary>
    public SidebarToolCategoryViewModel CreateSidebarFavoritesCategory(MainViewModel vm)
    {
        var favoriteTools = vm.FavoriteToolIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(toolId => CreateSidebarToolItem(vm, toolId))
            .Where(tool => tool is not null)
            .Cast<SidebarToolItemViewModel>()
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        return CreateSidebarCategory(
            vm,
            categoryKey: "ToolsFavoritesHeader",
            categoryName: vm.Localize("ToolsFavoritesHeader"),
            brushKey: "WarningBrush",
            tools: new ObservableCollection<SidebarToolItemViewModel>(favoriteTools));
    }

    /// <summary>
    /// Creates a sidebar tool leaf from a registry descriptor. Reused by the
    /// initial tree build and the targeted favorites refresh path.
    /// </summary>
    public SidebarToolItemViewModel CreateSidebarToolItem(MainViewModel vm, ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(descriptor);

        var name = vm.Localize(descriptor.LabelKey);
        var aliases = string.Join(' ', descriptor.CommandPrefixes);
        return new SidebarToolItemViewModel
        {
            Id = descriptor.Id,
            Name = name,
            BrushKey = GetCategoryBrushKey(descriptor.Category),
            IconGeometryKey = descriptor.IconResourceKey,
            Searchable = $"{name} {aliases}".ToLowerInvariant()
        };
    }

    /// <summary>
    /// Resolves a tool descriptor by ID and builds the corresponding sidebar leaf.
    /// Returns <c>null</c> when the tool no longer exists in the registry.
    /// </summary>
    public SidebarToolItemViewModel? CreateSidebarToolItem(MainViewModel vm, string toolId)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        var descriptor = _toolRegistry.All.FirstOrDefault(
            d => string.Equals(d.Id, toolId, StringComparison.OrdinalIgnoreCase));
        return descriptor is null ? null : CreateSidebarToolItem(vm, descriptor);
    }

    private static SidebarToolCategoryViewModel CreateSidebarCategory(
        MainViewModel vm,
        string categoryKey,
        string categoryName,
        string brushKey,
        ObservableCollection<SidebarToolItemViewModel> tools)
    {
        var isExpanded = vm.CurrentSettings?.SidebarExpandedCategories.TryGetValue(
            categoryKey,
            out var persistedExpanded) == true
                ? persistedExpanded
                : true;

        return new SidebarToolCategoryViewModel
        {
            CategoryKey = categoryKey,
            CategoryName = categoryName,
            BrushKey = brushKey,
            Tools = tools,
            VisibleCount = tools.Count,
            IsExpanded = isExpanded
        };
    }

    /// <summary>
    /// Applies a search filter to the sidebar Tools <see cref="TreeView"/>.
    /// Toggles visibility on individual tools and categories, updates the
    /// per-category visible count, and auto-expands matching categories.
    /// Returns <c>true</c> if at least one category is still visible after
    /// filtering (useful for showing/hiding the "no results" indicator).
    /// </summary>
    public bool FilterSidebarTools(
        ObservableCollection<SidebarToolCategoryViewModel> categories,
        string? filter)
    {
        var trimmed = (filter ?? string.Empty).Trim();
        var hasFilter = !string.IsNullOrEmpty(trimmed);
        var filterLower = trimmed.ToLowerInvariant();
        var anyVisibleTool = false;

        foreach (var category in categories)
        {
            var visibleInCategory = 0;
            foreach (var tool in category.Tools)
            {
                // tool.Searchable is pre-lowercased in BuildSidebarToolsData; plain Contains is fine.
                var searchable = tool.Searchable;
                var matches = !hasFilter || searchable.Contains(filterLower);
                tool.IsVisible = matches;
                if (matches)
                {
                    visibleInCategory++;
                }
            }

            category.VisibleCount = hasFilter ? visibleInCategory : category.Tools.Count;
            category.IsVisible = !hasFilter || visibleInCategory > 0;
            if (hasFilter)
            {
                category.IsExpanded = visibleInCategory > 0;
            }

            if (category.IsVisible)
            {
                anyVisibleTool = true;
            }
        }

        return anyVisibleTool;
    }

    // ── Full-page Tools tab ─────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the contents of the full-page Tools tab into
    /// <paramref name="target"/>: Favorites section, Recents section, and all
    /// tools grouped by category. Honours <paramref name="searchFilter"/> by
    /// filtering tools and hiding the Favorites/Recents headers when a query
    /// is active. Returns the number of tools rendered (filtered count when
    /// a search is active, total count otherwise) so the caller can update
    /// its count label.
    /// </summary>
    public int RefreshToolsTabSections(
        System.Windows.Controls.Panel target,
        MainViewModel vm,
        string? searchFilter,
        Action<ToolDescriptor> onCardClick,
        Action<string> onPinClick)
    {
        target.Children.Clear();
        var filter = (searchFilter ?? string.Empty).Trim();
        var hasFilter = !string.IsNullOrEmpty(filter);
        var matchingToolCount = 0;

        // ── Favorites section ──
        if (!hasFilter)
        {
            var favIds = vm.FavoriteToolIds;
            if (favIds.Count > 0)
            {
                AddToolsTabSectionHeader(target, vm.Localize("ToolsFavoritesHeader"), "WarningBrush");
                var favPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
                foreach (var favId in favIds)
                {
                    var desc = _toolRegistry.All.FirstOrDefault(
                        d => string.Equals(d.Id, favId, StringComparison.OrdinalIgnoreCase));
                    if (desc is not null)
                    {
                        favPanel.Children.Add(CreateToolsTabCard(desc, vm, onCardClick, onPinClick));
                    }
                }
                target.Children.Add(favPanel);
            }
            else
            {
                AddToolsTabSectionHeader(target, vm.Localize("ToolsFavoritesHeader"), "WarningBrush");
                var emptyFav = new TextBlock
                {
                    Text = vm.Localize("ToolsEmptyFavorites"),
                    FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
                    Margin = new Thickness(4, 0, 0, 16)
                };
                emptyFav.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
                target.Children.Add(emptyFav);
            }

            // ── Recent section ──
            var recentIds = vm.RecentToolIds;
            if (recentIds.Count > 0)
            {
                AddToolsTabSectionHeader(target, vm.Localize("ToolsRecentHeader"), "AccentBrush");
                var recentPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
                foreach (var rid in recentIds)
                {
                    var desc = _toolRegistry.All.FirstOrDefault(
                        d => string.Equals(d.Id, rid, StringComparison.OrdinalIgnoreCase));
                    if (desc is not null)
                    {
                        recentPanel.Children.Add(CreateToolsTabCard(desc, vm, onCardClick, onPinClick));
                    }
                }
                target.Children.Add(recentPanel);
            }
        }

        // ── All tools by category ──
        if (!hasFilter)
        {
            AddToolsTabSectionHeader(target, vm.Localize("ToolsAllHeader"), "TextPrimaryBrush");
        }

        string? lastCategory = null;
        var sorted = _toolRegistry.All
            .OrderBy(d => d.Category)
            .ThenBy(d => vm.Localize(d.LabelKey), StringComparer.OrdinalIgnoreCase);

        WrapPanel? currentWrap = null;

        var externalDescriptors = new List<ToolDescriptor>();

        foreach (var descriptor in sorted)
        {
            if (hasFilter)
            {
                var label = vm.Localize(descriptor.LabelKey);
                var aliases = string.Join(" ", descriptor.CommandPrefixes);
                var descKey = descriptor.DescriptionKey ?? $"ToolDesc{descriptor.Id}";
                var descText = vm.Localize(descKey);
                var searchable = $"{label} {aliases} {descText}";
                if (!searchable.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            matchingToolCount++;

            if (descriptor.Category == ToolCategory.External)
            {
                externalDescriptors.Add(descriptor);
                continue;
            }

            if (!string.Equals(descriptor.CategoryLabelKey, lastCategory, StringComparison.Ordinal))
            {
                if (currentWrap is not null)
                {
                    target.Children.Add(currentWrap);
                }

                var brushKey = GetCategoryBrushKey(descriptor.Category);
                AddToolsTabCategoryHeader(target, vm.Localize(descriptor.CategoryLabelKey), brushKey);
                currentWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                lastCategory = descriptor.CategoryLabelKey;
            }

            currentWrap?.Children.Add(CreateToolsTabCard(descriptor, vm, onCardClick, onPinClick));
        }
        if (currentWrap is not null)
        {
            target.Children.Add(currentWrap);
        }

        if (externalDescriptors.Count > 0)
        {
            RenderToolsTabExternalCategory(target, vm, externalDescriptors, onCardClick, onPinClick);
        }

        if (hasFilter && matchingToolCount == 0)
        {
            var emptyState = new StackPanel
            {
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var noResultsTitle = new TextBlock
            {
                Text = vm.Localize("ToolsNoResults"),
                FontSize = (double)Application.Current.FindResource("FontSizeBodyLarge"),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };
            noResultsTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            emptyState.Children.Add(noResultsTitle);

            var noResultsHint = new TextBlock
            {
                Text = vm.Localize("ToolsNoResultsHint"),
                FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            noResultsHint.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
            emptyState.Children.Add(noResultsHint);

            target.Children.Add(emptyState);
        }

        return hasFilter ? matchingToolCount : _toolRegistry.All.Count;
    }

    private void RenderToolsTabExternalCategory(
        System.Windows.Controls.Panel target,
        MainViewModel vm,
        IReadOnlyList<ToolDescriptor> descriptors,
        Action<ToolDescriptor> onCardClick,
        Action<string> onPinClick)
    {
        AddToolsTabCategoryHeader(
            target,
            vm.Localize(descriptors[0].CategoryLabelKey),
            GetCategoryBrushKey(ToolCategory.External));

        var nativeTools = descriptors
            .Where(descriptor => string.IsNullOrEmpty(descriptor.ExternalProviderName))
            .ToList();
        if (nativeTools.Count > 0)
        {
            var nativeWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var descriptor in nativeTools)
            {
                nativeWrap.Children.Add(CreateToolsTabCard(descriptor, vm, onCardClick, onPinClick));
            }
            target.Children.Add(nativeWrap);
        }

        foreach (var group in GroupExternalToolsByProvider(descriptors))
        {
            AddToolsTabProviderSubHeader(target, group.ProviderName);
            var providerWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var descriptor in group.Tools)
            {
                providerWrap.Children.Add(CreateToolsTabCard(descriptor, vm, onCardClick, onPinClick));
            }
            target.Children.Add(providerWrap);
        }
    }

    /// <summary>
    /// Appends a "Favorites" / "Recents" / "All Tools" section header to
    /// <paramref name="target"/>, themed with the given brush resource key.
    /// </summary>
    public void AddToolsTabSectionHeader(System.Windows.Controls.Panel target, string text, string brushKey)
    {
        var sectionHeader = new TextBlock
        {
            Text = text,
            FontSize = (double)Application.Current.FindResource("FontSizeBodyLarge"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 8)
        };
        sectionHeader.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        target.Children.Add(sectionHeader);
    }

    /// <summary>
    /// Appends a category header (accent bar + uppercase label) to
    /// <paramref name="target"/>, coloured with the category brush.
    /// </summary>
    public void AddToolsTabCategoryHeader(System.Windows.Controls.Panel target, string text, string brushKey)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 4)
        };
        var accentBar = new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        accentBar.SetResourceReference(Border.BackgroundProperty, brushKey);
        header.Children.Add(accentBar);

        var label = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        header.Children.Add(label);
        target.Children.Add(header);
    }

    public void AddToolsTabProviderSubHeader(System.Windows.Controls.Panel target, string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 6, 0, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        target.Children.Add(label);
    }

    /// <summary>
    /// Creates a wide (280px) card for the Tools tab grid layout, complete
    /// with icon, name, description, and a pin/unpin button. The card invokes
    /// <paramref name="onCardClick"/> when launched and
    /// <paramref name="onPinClick"/> when the pin button is toggled.
    /// </summary>
    public FrameworkElement CreateToolsTabCard(
        ToolDescriptor descriptor,
        MainViewModel vm,
        Action<ToolDescriptor> onCardClick,
        Action<string> onPinClick)
    {
        var categoryBrushKey = GetCategoryBrushKey(descriptor.Category);
        const string DefaultBorderKey = "BorderBrush";
        const string ActiveBorderKey = "AccentBrush";
        const string DefaultBackgroundKey = "CardBrush";
        const string ActiveBackgroundKey = "HighlightBrush";

        // Icon
        Path? iconPath = null;
        if (descriptor.IconResourceKey is not null
            && Application.Current.TryFindResource(descriptor.IconResourceKey) is Geometry geo)
        {
            iconPath = new Path
            {
                Data = geo,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconPath.SetResourceReference(Shape.FillProperty, categoryBrushKey);
        }

        var nameBlock = new TextBlock
        {
            Text = vm.Localize(descriptor.LabelKey),
            FontSize = (double)Application.Current.FindResource("FontSizeBody"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var descKey = descriptor.DescriptionKey ?? $"ToolDesc{descriptor.Id}";
        var descText = vm.Localize(descKey);
        var descBlock = new TextBlock
        {
            Text = descText != descKey ? descText : "",
            FontSize = (double)Application.Current.FindResource("FontSizeSmallCaption"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        descBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        // Pin/Unpin button
        var isFav = vm.FavoriteToolIds.Contains(descriptor.Id, StringComparer.OrdinalIgnoreCase);
        var pinBtn = new Button
        {
            Content = isFav ? "\uE735" : "\uE734",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Style = (Style)Application.Current.FindResource("GhostButtonStyle"),
            Padding = new Thickness(2),
            Opacity = isFav ? 1.0 : 0.4,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = descriptor.Id,
            ToolTip = isFav ? vm.Localize("ToolsUnpinTooltip") : vm.Localize("ToolsPinTooltip")
        };
        pinBtn.SetResourceReference(Control.ForegroundProperty,
            isFav ? "WarningBrush" : "TextSecondaryBrush");
        AutomationProperties.SetName(pinBtn,
            isFav ? vm.Localize("A11yUnpinTool") : vm.Localize("A11yPinTool"));
        pinBtn.Click += (_, e) =>
        {
            e.Handled = true;
            onPinClick(descriptor.Id);
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(nameBlock);
        if (!string.IsNullOrEmpty(descBlock.Text))
        {
            textStack.Children.Add(descBlock);
        }

        var content = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 0, 24, 0)
        };
        if (iconPath is not null)
        {
            var iconBorder = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(6),
                Opacity = 0.12,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = iconPath
            };
            iconBorder.SetResourceReference(Border.BackgroundProperty, categoryBrushKey);
            content.Children.Add(iconBorder);
            DockPanel.SetDock(iconBorder, Dock.Left);
        }
        content.Children.Add(textStack);

        // Use a bare template so the button has no hover chrome -
        // all visual feedback comes from the outer cardBorder.
        var btnTemplate = new ControlTemplate(typeof(Button));
        var btnPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        btnPresenter.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 8, 10, 8));
        btnPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        btnPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        btnTemplate.VisualTree = btnPresenter;

        var launchButton = new Button
        {
            Content = content,
            Tag = descriptor,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = descBlock.Text.Length > 0 ? descBlock.Text : null,
            Template = btnTemplate
        };
        AutomationProperties.SetName(launchButton, vm.Localize(descriptor.LabelKey));
        launchButton.Click += (_, _) => onCardClick(descriptor);

        var cardBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = launchButton
        };
        cardBorder.SetResourceReference(Border.BorderBrushProperty, DefaultBorderKey);
        cardBorder.SetResourceReference(Border.BackgroundProperty, DefaultBackgroundKey);

        void UpdateCardVisualState()
        {
            var isActive = launchButton.IsMouseOver
                || launchButton.IsKeyboardFocusWithin
                || pinBtn.IsMouseOver
                || pinBtn.IsKeyboardFocusWithin;

            // Resolve via SetResourceReference so the hover-state brushes track
            // runtime theme swaps without needing a manual rebuild.
            cardBorder.SetResourceReference(Border.BorderBrushProperty,
                isActive ? ActiveBorderKey : DefaultBorderKey);
            cardBorder.SetResourceReference(Border.BackgroundProperty,
                isActive ? ActiveBackgroundKey : DefaultBackgroundKey);
        }

        launchButton.MouseEnter += (_, _) => UpdateCardVisualState();
        launchButton.MouseLeave += (_, _) => UpdateCardVisualState();
        launchButton.GotKeyboardFocus += (_, _) => UpdateCardVisualState();
        launchButton.LostKeyboardFocus += (_, _) => UpdateCardVisualState();
        pinBtn.MouseEnter += (_, _) => UpdateCardVisualState();
        pinBtn.MouseLeave += (_, _) => UpdateCardVisualState();
        pinBtn.GotKeyboardFocus += (_, _) => UpdateCardVisualState();
        pinBtn.LostKeyboardFocus += (_, _) => UpdateCardVisualState();

        var card = new Grid
        {
            Width = 280,
            Margin = new Thickness(0, 0, 8, 8)
        };
        card.Children.Add(cardBorder);

        pinBtn.HorizontalAlignment = HorizontalAlignment.Right;
        pinBtn.VerticalAlignment = VerticalAlignment.Top;
        pinBtn.Margin = new Thickness(0, 6, 6, 0);
        card.Children.Add(pinBtn);

        return card;
    }

    // ── Pure helpers ────────────────────────────────────────────────────

    public static IReadOnlyList<ExternalToolProviderGroup> GroupExternalToolsByProvider(
        IEnumerable<ToolDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        return descriptors
            .Where(descriptor => descriptor.Category == ToolCategory.External)
            .Where(descriptor => !string.IsNullOrEmpty(descriptor.ExternalProviderName))
            .GroupBy(descriptor => descriptor.ExternalProviderName!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ExternalToolProviderGroup(group.Key, group.ToList()))
            .ToList();
    }

    /// <summary>
    /// Maps a <see cref="ToolCategory"/> to the resource key of the brush
    /// used for accents and icons in the sidebar and Tools tab.
    /// </summary>
    public static string GetCategoryBrushKey(ToolCategory category)
        => category switch
        {
            ToolCategory.Network => "ToolNetworkBrush",
            ToolCategory.Security => "ToolSecurityBrush",
            ToolCategory.Encoding => "ToolEncodingBrush",
            ToolCategory.System => "ToolSystemBrush",
            ToolCategory.External => "ToolExternalBrush",
            _ => "TextSecondaryBrush"
        };

    /// <summary>
    /// Returns the hostname of the currently-selected server, trimmed, or
    /// <c>null</c> if no server is selected (or its remote address is empty).
    /// </summary>
    public static string? GetInheritedToolTargetHost(MainViewModel vm)
    {
        var host = vm.ServerList.SelectedServer?.RemoteServer;
        return string.IsNullOrWhiteSpace(host) ? null : host.Trim();
    }

    /// <summary>
    /// Builds a <see cref="ToolContext"/> pre-populated with the currently
    /// selected server's hostname, for network tools that accept a target.
    /// Returns <c>null</c> for non-network tools or when no host is available.
    /// </summary>
    public static ToolContext? CreateInheritedToolContext(ToolDescriptor descriptor, MainViewModel vm)
    {
        if (!descriptor.IsNetworkTool)
        {
            return null;
        }

        var host = GetInheritedToolTargetHost(vm);
        return host is null ? null : new ToolContext(TargetHost: host);
    }

    /// <summary>
    /// Resolves the tab title for a tool: uses the "with argument" localised
    /// variant when a host is inherited and the descriptor provides one,
    /// otherwise falls back to the plain label.
    /// </summary>
    public static string ResolveToolTabTitle(ToolDescriptor descriptor, ToolContext? context, MainViewModel vm)
    {
        if (context?.TargetHost is not null && descriptor.LabelWithArgKey is not null)
        {
            return string.Format(vm.Localize(descriptor.LabelWithArgKey), context.TargetHost);
        }

        return vm.Localize(descriptor.LabelKey);
    }
}
