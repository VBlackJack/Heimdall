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

using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// Stateless resolver turning a TwinShell action into a UI-agnostic
/// <see cref="CommandPresentation"/>. This is the single source of truth for the
/// risk/platform localized strings and brush key: <see cref="CommandLibraryActionEntry"/>
/// delegates to the public mapping helpers here so every surface yields byte-identical
/// labels. Localization fallbacks are resolved through the supplied <c>localize</c>
/// delegate (same pattern as the sync result mapper); the resolver has no WPF or
/// service dependencies.
/// </summary>
public static class CommandPresentationResolver
{
    /// <summary>
    /// The glyph and the locale key of the label for each shipped category key. The glyph
    /// is data, the same in every language, so it lives here rather than in the catalogues.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Glyph, string LabelKey)> KnownCategories =
        new Dictionary<string, (string Glyph, string LabelKey)>(StringComparer.Ordinal)
        {
            [CommandCategories.Security] = ("🔒", "ToolCmdLibCategorySecurity"),
            [CommandCategories.ActiveDirectory] = ("🏢", "ToolCmdLibCategoryActiveDirectory"),
            [CommandCategories.ServicesAutomation] = ("⚙️", "ToolCmdLibCategoryServicesAutomation"),
            [CommandCategories.WindowsUpdates] = ("🔄", "ToolCmdLibCategoryWindowsUpdates"),
            [CommandCategories.MonitoringLogs] = ("📊", "ToolCmdLibCategoryMonitoringLogs"),
            [CommandCategories.FilesStorage] = ("📁", "ToolCmdLibCategoryFilesStorage"),
            [CommandCategories.NetworkDns] = ("🌐", "ToolCmdLibCategoryNetworkDns"),
            [CommandCategories.PackageManagement] = ("📦", "ToolCmdLibCategoryPackageManagement"),
            [CommandCategories.Development] = ("🔧", "ToolCmdLibCategoryDevelopment"),
            [CommandCategories.Performance] = ("⚡", "ToolCmdLibCategoryPerformance"),
            [CommandCategories.ContainersVms] = ("🐳", "ToolCmdLibCategoryContainersVms"),
            [CommandCategories.Database] = ("💽", "ToolCmdLibCategoryDatabase"),
            [CommandCategories.WindowsDebloat] = ("🧹", "ToolCmdLibCategoryWindowsDebloat"),
        };

    /// <summary>
    /// The label an operator reads for <paramref name="category"/>: the glyph and the
    /// localized name for a shipped category key, the string itself for anything else.
    /// </summary>
    /// <remarks>
    /// Single source of truth for every surface that shows a category: the group header and
    /// the filter combo of the library, the editor's category combo, the row's accessible
    /// name, the picker and the palette. A category an operator created is free text and is
    /// shown as typed.
    /// </remarks>
    public static string ResolveCategoryLabel(string? category, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);

        if (string.IsNullOrEmpty(category))
        {
            return string.Empty;
        }

        return KnownCategories.TryGetValue(category, out var known)
            ? known.Glyph + " " + localize(known.LabelKey)
            : category;
    }

    /// <summary>
    /// The category to store for what an operator chose or typed in a category combo: the
    /// key when <paramref name="labelOrCategory"/> is the label of a shipped category in the
    /// current language, the text itself otherwise.
    /// </summary>
    public static string ResolveCategoryKey(string? labelOrCategory, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);

        var text = (labelOrCategory ?? string.Empty).Trim();
        foreach (var key in KnownCategories.Keys)
        {
            if (string.Equals(ResolveCategoryLabel(key, localize), text, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return text;
    }

    /// <summary>
    /// Builds the presentation for the given action, resolving localized risk
    /// and platform text through <paramref name="localize"/>.
    /// </summary>
    public static CommandPresentation Resolve(ActionModel action, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(localize);

        return new CommandPresentation
        {
            Title = action.Title,
            Description = action.Description ?? string.Empty,
            Notes = string.IsNullOrWhiteSpace(action.Notes) ? string.Empty : action.Notes,
            Level = action.Level,
            Platform = action.Platform,
            Examples = action.Examples,
            Links = action.Links,
            RiskLabel = ResolveRiskLabel(action.Level, localize),
            RiskBadge = ResolveRiskBadge(action.Level, localize),
            RiskBrushKey = ResolveRiskBrushKey(action.Level),
            PlatformLabel = ResolvePlatformLabel(action.Platform, localize)
        };
    }

    /// <summary>
    /// Localized platform label (Windows / Linux / Both). Single source of truth shared
    /// with <see cref="CommandLibraryActionEntry.PlatformLabel"/>.
    /// </summary>
    public static string ResolvePlatformLabel(Platform platform, Func<string, string> localize) => platform switch
    {
        Platform.Windows => localize("ToolCmdLibPlatformLabelWin"),
        Platform.Linux => localize("ToolCmdLibPlatformLabelLin"),
        _ => localize("ToolCmdLibPlatformLabelBoth")
    };

    /// <summary>
    /// Risk brush resource key. Single source of truth shared with
    /// <see cref="CommandLibraryActionEntry.RiskBrushKey"/>.
    /// </summary>
    /// <remarks>
    /// See <see cref="ResolveRiskLabel"/> for why the default arm reads as dangerous.
    /// </remarks>
    public static string ResolveRiskBrushKey(CriticalityLevel level) => level switch
    {
        CriticalityLevel.Info => "TextSecondaryBrush",
        CriticalityLevel.Run => "WarningBrush",
        _ => "ErrorBrush"
    };

    /// <summary>
    /// Localized long-form risk label. Single source of truth shared with
    /// <see cref="CommandLibraryActionEntry.RiskLabel"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default arm resolves to the <see cref="CriticalityLevel.Dangerous"/> text
    /// rather than an empty string, so the display agrees with
    /// <c>DangerousCommandGuard</c>, which prompts for anything at or above
    /// <see cref="CriticalityLevel.Dangerous"/>. An action carrying a level outside the
    /// declared enum - reachable through an import file, since the JSON enum converter
    /// accepts raw numbers - would otherwise prompt on Send while rendering in the list
    /// with no badge and the informational colour. Both halves of that one decision now
    /// fail in the same direction.
    /// </para>
    /// </remarks>
    public static string ResolveRiskLabel(CriticalityLevel level, Func<string, string> localize) => level switch
    {
        CriticalityLevel.Info => localize("ToolCmdLibRiskInfo"),
        CriticalityLevel.Run => localize("ToolCmdLibRiskRun"),
        _ => localize("ToolCmdLibRiskDangerous")
    };

    /// <summary>
    /// Localized short risk badge. Single source of truth shared with
    /// <see cref="CommandLibraryActionEntry.RiskBadge"/>.
    /// </summary>
    /// <remarks>
    /// See <see cref="ResolveRiskLabel"/> for why the default arm reads as dangerous.
    /// </remarks>
    public static string ResolveRiskBadge(CriticalityLevel level, Func<string, string> localize) => level switch
    {
        CriticalityLevel.Info => localize("ToolCmdLibRiskBadgeInfo"),
        CriticalityLevel.Run => localize("ToolCmdLibRiskBadgeRun"),
        _ => localize("ToolCmdLibRiskBadgeDanger")
    };
}
