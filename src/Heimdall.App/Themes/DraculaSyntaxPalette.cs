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

using ICSharpCode.AvalonEdit.Highlighting;

namespace Heimdall.App.Themes;

/// <summary>
/// Fixed Dracula syntax palette applied to AvalonEdit highlighting definitions.
/// All theme variants share the same token colors while the editor chrome changes.
/// </summary>
public static class DraculaSyntaxPalette
{
    /// <summary>
    /// Gets the Dracula token-color rules keyed by AvalonEdit color name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Rules { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Comment", "#6272A4" },
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
            { "XmlComment", "#6272A4" },
            { "DocComment", "#6272A4" },
            { "XmlString", "#F1FA8C" },
            { "Assignment", "#FF79C6" },
            { "Entities", "#BD93F9" },
            { "Variable", "#F8F8F2" },
            { "Command", "#50FA7B" },
            { "Operator", "#FF79C6" },
        };

    /// <summary>
    /// Applies the Dracula palette to the given highlighting definition, including nested rule sets.
    /// </summary>
    /// <param name="highlighting">The highlighting definition to mutate, or <see langword="null"/>.</param>
    public static void Apply(IHighlightingDefinition? highlighting)
    {
        if (highlighting is null)
        {
            return;
        }

        foreach (var color in highlighting.NamedHighlightingColors)
        {
            if (Rules.TryGetValue(color.Name, out var hex))
            {
                color.Foreground = new SimpleHighlightingBrush(ColorFromHex(hex));
            }
        }

        // A highlighting definition's rule sets form a graph that can contain cycles (a span's rule
        // set may reference an ancestor or itself, e.g. the nested rule sets in MarkDown). Track the
        // rule sets already visited so a cyclic graph cannot drive this walk into infinite recursion
        // (which previously overflowed the stack and crashed the process on editor open).
        ApplyColorsToRuleSet(highlighting.MainRuleSet, []);
    }

    private static void ApplyColorsToRuleSet(HighlightingRuleSet? ruleSet, HashSet<HighlightingRuleSet> visited)
    {
        if (ruleSet is null || !visited.Add(ruleSet))
        {
            return;
        }

        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Color?.Name is not null && Rules.TryGetValue(rule.Color.Name, out var hex))
            {
                rule.Color.Foreground = new SimpleHighlightingBrush(ColorFromHex(hex));
            }
        }

        foreach (var span in ruleSet.Spans)
        {
            if (span.SpanColor?.Name is not null && Rules.TryGetValue(span.SpanColor.Name, out var hex))
            {
                span.SpanColor.Foreground = new SimpleHighlightingBrush(ColorFromHex(hex));
            }

            ApplyColorsToRuleSet(span.RuleSet, visited);
        }
    }

    private static System.Windows.Media.Color ColorFromHex(string hex)
    {
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }
}
