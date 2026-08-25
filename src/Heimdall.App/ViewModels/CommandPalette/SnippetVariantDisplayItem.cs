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

namespace Heimdall.App.ViewModels.CommandPalette;

/// <summary>
/// Dumb display model for a single selectable snippet variant shown in the
/// Command Palette drill-in list. The localized <see cref="DisplayLabel"/> is
/// built by the view-model from the underlying <see cref="SnippetVariant"/>.
/// </summary>
public sealed class SnippetVariantDisplayItem : IAccessibleItemViewModel
{
    /// <summary>
    /// The label is what identifies the variant; the preview command below it carries its
    /// own automation peer, so repeating it here would only make the row read twice.
    /// </summary>
    public string AccessibleName => DisplayLabel;

    /// <inheritdoc/>
    public string? AccessibleHelpText => null;

    /// <summary>Localized, human-readable label for this variant.</summary>
    public string DisplayLabel { get; init; } = string.Empty;

    /// <summary>Command string previewed for this variant (template pattern or literal example).</summary>
    public string PreviewCommand { get; init; } = string.Empty;

    /// <summary>The underlying variant this display item represents.</summary>
    public required SnippetVariant Variant { get; init; }
}
