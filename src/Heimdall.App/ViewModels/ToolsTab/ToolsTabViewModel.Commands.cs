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

using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels.ToolsTab;

/// <summary>
/// Commands partial of <see cref="ToolsTabViewModel"/>: card click and
/// favorite toggle invoked from the rendered tool cards via the
/// <c>onCardClick</c> / <c>onPinClick</c> callbacks the host window
/// passes into <see cref="ToolsTabPopulationService.RefreshToolsTabSections"/>.
/// </summary>
public sealed partial class ToolsTabViewModel
{
    /// <summary>
    /// Resolves the inherited <see cref="ToolContext"/> for
    /// <paramref name="descriptor"/>, opens the tool tab via
    /// <see cref="MainViewModel.OpenToolTabAsync"/> and tracks it as
    /// recently used. Tab-strip navigation
    /// (<c>TabSessions.IsChecked = true</c>) is the host window's
    /// responsibility - this command focuses on the tool-launch logic only.
    /// </summary>
    [RelayCommand]
    private async Task LaunchToolAsync(ToolDescriptor? descriptor)
    {
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
            FileLogger.Error($"Tool tab card launch failed: {descriptor.Id}", ex);
        }
    }

    /// <summary>
    /// Toggles a tool's favorite state (persisted via
    /// <see cref="MainViewModel.ToggleFavoriteToolAsync"/>) and triggers
    /// a re-render so the card moves between the Favorites and All sections.
    /// </summary>
    [RelayCommand]
    private async Task ToggleFavoriteAsync(string? toolId)
    {
        if (string.IsNullOrEmpty(toolId))
        {
            return;
        }

        await _main.ToggleFavoriteToolAsync(toolId).ConfigureAwait(true);
        InvalidateSections();
    }
}
