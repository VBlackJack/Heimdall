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

public sealed class CommandLibraryPickerItem
{
    public required string ActionId { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required bool HasLinuxTemplate { get; init; }
    public required bool HasWindowsTemplate { get; init; }
    public required IReadOnlyList<TemplateParameter> LinuxParameters { get; init; }

    /// <summary>Linux command template (when present), used for the live command preview.</summary>
    public CommandTemplate? LinuxTemplate { get; init; }

    /// <summary>Resolved presentation (description, risk, platform labels) for this action.</summary>
    public required CommandPresentation Presentation { get; init; }

    public string PlatformBadgeText => HasLinuxTemplate && HasWindowsTemplate
        ? "WIN/LIN"
        : HasLinuxTemplate
            ? "LIN"
            : "WIN";
}
