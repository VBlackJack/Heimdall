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

namespace Heimdall.Core.Models;

/// <summary>
/// Single source of truth for a built-in tool's metadata.
/// Used by the tool registry, command palette, menus, and view factory.
/// </summary>
/// <param name="Id">Short identifier, e.g. "PING", "HASH". Used as lookup key.</param>
/// <param name="Category">Grouping for menus and palette.</param>
/// <param name="CategoryLabelKey">i18n key for the category header, e.g. "ToolCategoryNetwork".</param>
/// <param name="LabelKey">i18n key for the tool name, e.g. "PaletteToolPing".</param>
/// <param name="LabelWithArgKey">i18n key for "tool with argument" variant, e.g. "PaletteToolPingWith". Null if not supported.</param>
/// <param name="CommandPrefixes">Palette search aliases, e.g. ["ping"] or ["dns","nslookup","dig"].</param>
/// <param name="IsNetworkTool">True if the tool should prompt for a target host when opened standalone.</param>
/// <param name="IconResourceKey">Optional XAML resource key for the tool's vector geometry icon (e.g. "Geo.Tool.PortScanner"). Null if no icon available.</param>
/// <param name="DescriptionKey">Optional explicit i18n key for the tool description. When null, the convention <c>ToolDesc{Id}</c> is used.</param>
/// <param name="ExternalProviderName">Provider display name for dynamically-detected external tools. Null for built-in tools.</param>
public sealed record ToolDescriptor(
    string Id,
    ToolCategory Category,
    string CategoryLabelKey,
    string LabelKey,
    string? LabelWithArgKey,
    string[] CommandPrefixes,
    bool IsNetworkTool,
    string? IconResourceKey = null,
    string? DescriptionKey = null,
    string? ExternalProviderName = null)
{
    /// <summary>
    /// The prefixed tool type string used in connection type fields, e.g. "TOOL:PING".
    /// </summary>
    public string ToolType => $"TOOL:{Id}";

    /// <summary>
    /// The tool's display name in the language of the session.
    /// </summary>
    /// <param name="localize">Resolves a locale key to its text.</param>
    /// <remarks>
    /// For a built-in tool <see cref="LabelKey"/> is a locale key. For a tool detected on the
    /// machine (<see cref="ExternalProviderName"/> set) it is the product's own name, such as
    /// "Autorunsc", which no locale defines: looked up as a key it would miss, be logged as a
    /// missing translation, and only then fall back to itself. Such a name is returned as is.
    /// </remarks>
    public string ResolveLabel(Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);
        return ExternalProviderName is null ? localize(LabelKey) : LabelKey;
    }
}
