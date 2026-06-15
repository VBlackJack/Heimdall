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

namespace Heimdall.App.ViewModels.CommandPalette;

/// <summary>
/// Pure helper that enumerates every selectable command variant of a TwinShell
/// action. The Command Palette uses this to offer a drill-in list of related
/// commands, while the legacy single-command behaviour is preserved through
/// <see cref="GetDefault"/>.
/// </summary>
/// <remarks>
/// This resolver is UI-agnostic: it never produces localized strings. The UI
/// layer derives labels from <see cref="SnippetVariant.Kind"/>,
/// <see cref="SnippetVariant.Platform"/> and
/// <see cref="SnippetVariant.ExampleIndex"/>.
/// </remarks>
public static class SnippetVariantResolver
{
    /// <summary>
    /// Returns every selectable command variant of the given action, ordered
    /// Windows template, Linux template, Windows examples, Linux examples, then
    /// platform-neutral examples. Examples with a null or whitespace command
    /// are skipped. A null action yields an empty list.
    /// </summary>
    public static IReadOnlyList<SnippetVariant> GetVariants(ActionModel? action)
    {
        if (action is null) return Array.Empty<SnippetVariant>();

        var variants = new List<SnippetVariant>();

        var windowsTemplate = action.WindowsCommandTemplate;
        if (windowsTemplate is not null && !string.IsNullOrWhiteSpace(windowsTemplate.CommandPattern))
        {
            variants.Add(new SnippetVariant(
                SnippetVariantKind.WindowsTemplate,
                Platform.Windows,
                ExampleIndex: -1,
                Template: windowsTemplate,
                LiteralCommand: null,
                PreviewCommand: windowsTemplate.CommandPattern));
        }

        var linuxTemplate = action.LinuxCommandTemplate;
        if (linuxTemplate is not null && !string.IsNullOrWhiteSpace(linuxTemplate.CommandPattern))
        {
            variants.Add(new SnippetVariant(
                SnippetVariantKind.LinuxTemplate,
                Platform.Linux,
                ExampleIndex: -1,
                Template: linuxTemplate,
                LiteralCommand: null,
                PreviewCommand: linuxTemplate.CommandPattern));
        }

        AddExamples(variants, action.WindowsExamples, Platform.Windows);
        AddExamples(variants, action.LinuxExamples, Platform.Linux);
        AddExamples(variants, action.Examples, platform: null);

        return variants;
    }

    /// <summary>
    /// Returns the first variant by the legacy precedence (Windows template,
    /// then Linux template, then the first non-empty example), or <c>null</c>
    /// when the action carries no real command. Callers keep their own title
    /// fallback; this method never substitutes the action title.
    /// </summary>
    public static SnippetVariant? GetDefault(ActionModel? action)
    {
        var variants = GetVariants(action);
        return variants.Count > 0 ? variants[0] : null;
    }

    /// <summary>
    /// Appends one <see cref="SnippetVariant"/> per non-empty example, tagging
    /// each with the supplied platform and its zero-based source index.
    /// </summary>
    private static void AddExamples(
        List<SnippetVariant> variants,
        IReadOnlyList<TwinShell.Core.Models.CommandExample> examples,
        Platform? platform)
    {
        for (var i = 0; i < examples.Count; i++)
        {
            var command = examples[i].Command;
            if (string.IsNullOrWhiteSpace(command)) continue;

            variants.Add(new SnippetVariant(
                SnippetVariantKind.Example,
                platform,
                ExampleIndex: i,
                Template: null,
                LiteralCommand: command,
                PreviewCommand: command));
        }
    }
}
