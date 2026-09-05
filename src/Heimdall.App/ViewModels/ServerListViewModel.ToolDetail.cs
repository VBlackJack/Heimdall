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

using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels;

/// <summary>
/// What the tool detail pane shows for the selected tool.
/// </summary>
/// <remarks>
/// These texts used to be written into the pane by the window's tree handlers, so a selection
/// that reached the pane by any other path - UI Automation, the palette's reveal - showed the
/// previous tool. They now derive from <see cref="SelectedServer"/> alone.
/// </remarks>
public partial class ServerListViewModel
{
    /// <summary>
    /// Resolves a tool id to its descriptor. Wired by the main view model from the tool
    /// registry; left unset, the pane shows nothing, which is also the test-time default.
    /// </summary>
    internal Func<string, ToolDescriptor?>? ToolDescriptorResolver { get; set; }

    /// <summary>The selected tool's display name, or empty when no tool is selected.</summary>
    public string ToolDetailName =>
        SelectedToolDescriptor is { } tool ? LocalizeOrRaw(tool.LabelKey) : "";

    /// <summary>The selected tool's category label, or empty when no tool is selected.</summary>
    public string ToolDetailCategory =>
        SelectedToolDescriptor is { } tool ? LocalizeOrRaw(tool.CategoryLabelKey) : "";

    /// <summary>The selected tool's description, or empty when it has none.</summary>
    public string ToolDetailDescription
    {
        get
        {
            ToolDescriptor? tool = SelectedToolDescriptor;
            if (tool is null)
            {
                return "";
            }

            string key = tool.DescriptionKey ?? $"ToolDesc{tool.Id}";
            return _localizer.HasKey(key) ? _localizer[key] : "";
        }
    }

    private ToolDescriptor? SelectedToolDescriptor =>
        SelectedServer is { } selected
        && ConnectionTypeCatalog.IsToolConnectionType(selected.ConnectionType)
        && ToolDescriptorResolver is { } resolve
            ? resolve(ConnectionTypeCatalog.StripToolPrefix(selected.ConnectionType))
            : null;

    /// <summary>
    /// External tools carry their display name where built-in tools carry a key; the name is
    /// shown as it is when no key matches.
    /// </summary>
    private string LocalizeOrRaw(string key) => _localizer.HasKey(key) ? _localizer[key] : key;

    private void NotifyToolDetailChanged()
    {
        OnPropertyChanged(nameof(ToolDetailName));
        OnPropertyChanged(nameof(ToolDetailCategory));
        OnPropertyChanged(nameof(ToolDetailDescription));
    }
}
