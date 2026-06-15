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

namespace Heimdall.App.ViewModels.CommandPalette;

/// <summary>
/// Identifies the source of a <see cref="SnippetVariant"/> so the UI layer can
/// derive a localized label without inspecting the underlying TwinShell models.
/// </summary>
public enum SnippetVariantKind
{
    /// <summary>The variant comes from the action's Windows command template.</summary>
    WindowsTemplate,

    /// <summary>The variant comes from the action's Linux command template.</summary>
    LinuxTemplate,

    /// <summary>The variant comes from one of the action's command examples.</summary>
    Example
}

/// <summary>
/// A single selectable command variant of a TwinShell action, described in a
/// UI-agnostic way (no localized strings). Both the Command Palette drill-in
/// list and the Command Library can consume this to present the user with a
/// choice between related commands.
/// </summary>
/// <param name="Kind">Where this variant originates (template or example).</param>
/// <param name="Platform">
/// The platform this variant targets, or <c>null</c> for a platform-neutral
/// example.
/// </param>
/// <param name="ExampleIndex">
/// Zero-based index into the originating example list for
/// <see cref="SnippetVariantKind.Example"/> variants; <c>-1</c> for template
/// variants.
/// </param>
/// <param name="Template">
/// The originating command template for template variants; <c>null</c> for
/// example variants.
/// </param>
/// <param name="LiteralCommand">
/// The literal example command for example variants; <c>null</c> for template
/// variants (whose command lives in <see cref="CommandTemplate.CommandPattern"/>).
/// </param>
/// <param name="PreviewCommand">
/// The command string to preview: the template pattern for template variants,
/// or the literal example command for example variants.
/// </param>
public sealed record SnippetVariant(
    SnippetVariantKind Kind,
    Platform? Platform,
    int ExampleIndex,
    CommandTemplate? Template,
    string? LiteralCommand,
    string PreviewCommand);
