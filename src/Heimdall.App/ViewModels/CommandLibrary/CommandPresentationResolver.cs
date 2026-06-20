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
    public static string ResolveRiskBrushKey(CriticalityLevel level) => level switch
    {
        CriticalityLevel.Info => "TextSecondaryBrush",
        CriticalityLevel.Run => "WarningBrush",
        CriticalityLevel.Dangerous => "ErrorBrush",
        _ => "TextSecondaryBrush"
    };

    /// <summary>
    /// Localized long-form risk label. Single source of truth shared with
    /// <see cref="CommandLibraryActionEntry.RiskLabel"/>.
    /// </summary>
    public static string ResolveRiskLabel(CriticalityLevel level, Func<string, string> localize) => level switch
    {
        CriticalityLevel.Info => localize("ToolCmdLibRiskInfo"),
        CriticalityLevel.Run => localize("ToolCmdLibRiskRun"),
        CriticalityLevel.Dangerous => localize("ToolCmdLibRiskDangerous"),
        _ => string.Empty
    };

    /// <summary>
    /// Localized short risk badge. Single source of truth shared with
    /// <see cref="CommandLibraryActionEntry.RiskBadge"/>.
    /// </summary>
    public static string ResolveRiskBadge(CriticalityLevel level, Func<string, string> localize) => level switch
    {
        CriticalityLevel.Info => localize("ToolCmdLibRiskBadgeInfo"),
        CriticalityLevel.Run => localize("ToolCmdLibRiskBadgeRun"),
        CriticalityLevel.Dangerous => localize("ToolCmdLibRiskBadgeDanger"),
        _ => string.Empty
    };
}
