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

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// Pure, immutable presentation of a Command Library action: everything a UI
/// surface (Ctrl+K palette, picker dialog, main view) needs to render an action
/// without touching the TwinShell model or duplicating risk/platform logic.
/// Produced by <see cref="CommandPresentationResolver"/>.
/// </summary>
public sealed record CommandPresentation
{
    /// <summary>Action title (passthrough).</summary>
    public required string Title { get; init; }

    /// <summary>Action description; empty string when none.</summary>
    public required string Description { get; init; }

    /// <summary>Additional notes; empty string when null or whitespace.</summary>
    public required string Notes { get; init; }

    /// <summary>Criticality level of the action.</summary>
    public required CriticalityLevel Level { get; init; }

    /// <summary>Supported platform(s) of the action.</summary>
    public required Platform Platform { get; init; }

    /// <summary>Command examples exposed for this action.</summary>
    public required IReadOnlyList<CommandExample> Examples { get; init; }

    /// <summary>External documentation links exposed for this action.</summary>
    public required IReadOnlyList<ExternalLink> Links { get; init; }

    /// <summary>Localized long-form risk label.</summary>
    public required string RiskLabel { get; init; }

    /// <summary>Localized short risk badge text.</summary>
    public required string RiskBadge { get; init; }

    /// <summary>Theme resource key for the brush representing the risk level.</summary>
    public required string RiskBrushKey { get; init; }

    /// <summary>Localized platform label (Windows / Linux / Both).</summary>
    public required string PlatformLabel { get; init; }

    /// <summary>
    /// True when the action carries any secondary metadata worth surfacing in a
    /// detail panel: notes, at least one example, or at least one link.
    /// </summary>
    public bool HasMetadata =>
        !string.IsNullOrEmpty(Notes) || Examples.Count > 0 || Links.Count > 0;
}
