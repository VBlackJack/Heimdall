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

using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Models;

namespace Heimdall.App.ViewModels.Dialogs;

public sealed class CommandLibraryPickerItem : IAccessibleItemViewModel
{
    public required string ActionId { get; init; }
    public required string Title { get; init; }
    /// <summary>The category label the operator reads.</summary>
    public required string Category { get; init; }

    /// <summary>The category as stored (a key or free text); a search matches it too.</summary>
    public string CategoryKey { get; init; } = string.Empty;
    public required bool HasLinuxTemplate { get; init; }
    public required bool HasWindowsTemplate { get; init; }
    public required IReadOnlyList<TemplateParameter> LinuxParameters { get; init; }

    /// <summary>Linux command template (when present), used for the live command preview.</summary>
    public CommandTemplate? LinuxTemplate { get; init; }

    /// <summary>Parameters of the Windows template, used when the profile declares a Windows far end.</summary>
    public IReadOnlyList<TemplateParameter> WindowsParameters { get; init; } = [];

    /// <summary>Windows command template (when present), used when the profile declares a Windows far end.</summary>
    public CommandTemplate? WindowsTemplate { get; init; }

    /// <summary>Resolved presentation (description, risk, platform labels) for this action.</summary>
    public required CommandPresentation Presentation { get; init; }

    /// <summary>
    /// Localized platform badge text, resolved at construction from the same keys the
    /// main view uses (<c>ToolCmdLibPlatformLabelWin</c> / <c>...Lin</c> / <c>...Both</c>).
    /// </summary>
    public required string PlatformBadge { get; init; }

    /// <summary>
    /// What a screen reader announces for this row: the title, the risk, the platform, then
    /// the category, the order the library's own rows use.
    /// </summary>
    /// <remarks>
    /// The row paints these in separate text blocks, and a name written on the template's grid
    /// is inert: UI Automation reads the generated container, so the row announced the item's
    /// class name. The description is left out for the reason the library rows give: it would
    /// bury the risk word at the end of a long utterance.
    /// </remarks>
    public string AccessibleName
    {
        get
        {
            var parts = new List<string>(4) { Title };

            foreach (var part in new[] { Presentation.RiskLabel, PlatformBadge, Category })
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    parts.Add(part);
                }
            }

            return string.Join(", ", parts);
        }
    }

    /// <inheritdoc/>
    public string? AccessibleHelpText => null;
}
