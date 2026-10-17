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

using Heimdall.App.Theming;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Heimdall.App.Themes;

/// <summary>
/// Syntax palette applied to AvalonEdit highlighting definitions, supporting both
/// Dracula dark palettes and high-contrast light palettes.
/// </summary>
public static class DraculaSyntaxPalette
{
    /// <summary>
    /// Gets the Dracula dark token-color rules keyed by AvalonEdit color name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Rules { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Comment", "#B3BBD6" },
            { "String", "#F1FA8C" },
            { "Char", "#F1FA8C" },
            { "Preprocessor", "#FF79C6" },
            { "Punctuation", "#F8F8F2" },
            { "MethodCall", "#50FA7B" },
            { "NumberLiteral", "#BD93F9" },
            { "Digits", "#BD93F9" },
            { "Keywords", "#FF79C6" },
            { "GotoKeywords", "#FF79C6" },
            { "AccessKeywords", "#FF79C6" },
            { "ValueTypeKeywords", "#8BE9FD" },
            { "ReferenceTypeKeywords", "#8BE9FD" },
            { "ThisOrBaseReference", "#BD93F9" },
            { "NullOrValueKeywords", "#BD93F9" },
            { "ParameterModifiers", "#FF79C6" },
            { "Modifiers", "#FF79C6" },
            { "Visibility", "#FF79C6" },
            { "NamespaceKeywords", "#FF79C6" },
            { "GetSetAddRemove", "#50FA7B" },
            { "TrueFalse", "#BD93F9" },
            { "TypeKeywords", "#8BE9FD" },
            { "SemanticKeywords", "#FF79C6" },
            { "XmlTag", "#FF79C6" },
            { "XmlComment", "#B3BBD6" },
            { "DocComment", "#B3BBD6" },
            { "XmlString", "#F1FA8C" },
            { "Assignment", "#FF79C6" },
            { "Entities", "#BD93F9" },
            { "Variable", "#F8F8F2" },
            { "Command", "#50FA7B" },
            { "Operator", "#FF79C6" },
            { "Heading", "#BD93F9" },
            { "Bold", "#FFB86C" },
            { "Italic", "#F1FA8C" },
            { "Strikethrough", "#B3BBD6" },
            { "InlineCode", "#FF79C6" },
            { "CodeBlock", "#F8F8F2" },
            { "Link", "#8BE9FD" },
            { "NoteLink", "#50FA7B" },
            { "Image", "#8BE9FD" },
            { "Blockquote", "#B3BBD6" },
            { "ListMarker", "#F1FA8C" },
            { "HRule", "#6272A4" },
            { "MetaTag", "#8BE9FD" },
        };

    /// <summary>
    /// Gets the WCAG AA-compliant light-theme token-color rules keyed by AvalonEdit color name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LightRules { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Comment", "#625842" },
            { "String", "#68680D" },
            { "Char", "#68680D" },
            { "Preprocessor", "#B1165C" },
            { "Punctuation", "#1C1A17" },
            { "MethodCall", "#0F7829" },
            { "NumberLiteral", "#8E18C1" },
            { "Digits", "#8E18C1" },
            { "Keywords", "#B1165C" },
            { "GotoKeywords", "#B1165C" },
            { "AccessKeywords", "#B1165C" },
            { "ValueTypeKeywords", "#0F707B" },
            { "ReferenceTypeKeywords", "#0F707B" },
            { "ThisOrBaseReference", "#8E18C1" },
            { "NullOrValueKeywords", "#8E18C1" },
            { "ParameterModifiers", "#B1165C" },
            { "Modifiers", "#B1165C" },
            { "Visibility", "#B1165C" },
            { "NamespaceKeywords", "#B1165C" },
            { "GetSetAddRemove", "#0F7829" },
            { "TrueFalse", "#8E18C1" },
            { "TypeKeywords", "#0F707B" },
            { "SemanticKeywords", "#B1165C" },
            { "XmlTag", "#B1165C" },
            { "XmlComment", "#625842" },
            { "DocComment", "#625842" },
            { "XmlString", "#68680D" },
            { "Assignment", "#B1165C" },
            { "Entities", "#8E18C1" },
            { "Variable", "#1C1A17" },
            { "Command", "#0F7829" },
            { "Operator", "#B1165C" },
            { "Heading", "#8E18C1" },
            { "Bold", "#954D12" },
            { "Italic", "#68680D" },
            { "Strikethrough", "#625842" },
            { "InlineCode", "#B1165C" },
            { "CodeBlock", "#1C1A17" },
            { "Link", "#0F707B" },
            { "NoteLink", "#0F7829" },
            { "Image", "#0F707B" },
            { "Blockquote", "#625842" },
            { "ListMarker", "#68680D" },
            { "HRule", "#625842" },
            { "MetaTag", "#0F707B" },
        };

    /// <summary>
    /// Applies the syntax palette matching the active application theme (or dark palette by default)
    /// to the given highlighting definition, including nested rule sets.
    /// </summary>
    /// <param name="highlighting">The highlighting definition to mutate, or <see langword="null"/>.</param>
    public static void Apply(IHighlightingDefinition? highlighting) =>
        Apply(highlighting, isLightTheme: !ThemeLuminance.IsCurrentThemeDark());

    /// <summary>
    /// Applies the dark or light syntax palette to the given highlighting definition, including nested rule sets.
    /// </summary>
    /// <param name="highlighting">The highlighting definition to mutate, or <see langword="null"/>.</param>
    /// <param name="isLightTheme">Whether to apply the light-theme palette instead of the dark Dracula palette.</param>
    public static void Apply(IHighlightingDefinition? highlighting, bool isLightTheme)
    {
        if (highlighting is null)
        {
            return;
        }

        IReadOnlyDictionary<string, string> activeRules = isLightTheme ? LightRules : Rules;

        foreach (var color in highlighting.NamedHighlightingColors)
        {
            if (activeRules.TryGetValue(color.Name, out var hex))
            {
                color.Foreground = new SimpleHighlightingBrush(ColorFromHex(hex));
            }
        }

        // A highlighting definition's rule sets form a graph that can contain cycles (a span's rule
        // set may reference an ancestor or itself, e.g. the nested rule sets in MarkDown). Track the
        // rule sets already visited so a cyclic graph cannot drive this walk into infinite recursion
        // (which previously overflowed the stack and crashed the process on editor open).
        ApplyColorsToRuleSet(highlighting.MainRuleSet, activeRules, []);
    }

    private static void ApplyColorsToRuleSet(
        HighlightingRuleSet? ruleSet,
        IReadOnlyDictionary<string, string> activeRules,
        HashSet<HighlightingRuleSet> visited)
    {
        if (ruleSet is null || !visited.Add(ruleSet))
        {
            return;
        }

        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Color?.Name is not null && activeRules.TryGetValue(rule.Color.Name, out var hex))
            {
                rule.Color.Foreground = new SimpleHighlightingBrush(ColorFromHex(hex));
            }
        }

        foreach (var span in ruleSet.Spans)
        {
            if (span.SpanColor?.Name is not null && activeRules.TryGetValue(span.SpanColor.Name, out var hex))
            {
                span.SpanColor.Foreground = new SimpleHighlightingBrush(ColorFromHex(hex));
            }

            ApplyColorsToRuleSet(span.RuleSet, activeRules, visited);
        }
    }

    private static System.Windows.Media.Color ColorFromHex(string hex)
    {
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }
}
