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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Heimdall.App.Behaviors;

/// <summary>
/// Attached behavior that renders a <see cref="TextBlock"/> from a source string
/// while emphasizing the first case-insensitive occurrence of a query substring
/// (bold + accent). The full source text remains the block's readable content,
/// so screen-reader output is unchanged.
/// </summary>
public static class HighlightTextBehavior
{
    /// <summary>The full text to display.</summary>
    public static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.RegisterAttached(
            "SourceText", typeof(string), typeof(HighlightTextBehavior),
            new PropertyMetadata(string.Empty, OnChanged));

    /// <summary>The query whose first contiguous occurrence is emphasized.</summary>
    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.RegisterAttached(
            "Query", typeof(string), typeof(HighlightTextBehavior),
            new PropertyMetadata(string.Empty, OnChanged));

    public static void SetSourceText(DependencyObject element, string value)
        => element.SetValue(SourceTextProperty, value);

    public static string GetSourceText(DependencyObject element)
        => (string)element.GetValue(SourceTextProperty);

    public static void SetQuery(DependencyObject element, string value)
        => element.SetValue(QueryProperty, value);

    public static string GetQuery(DependencyObject element)
        => (string)element.GetValue(QueryProperty);

    /// <summary>
    /// Pure split of <paramref name="source"/> around the first case-insensitive
    /// occurrence of <paramref name="query"/>. When the query is empty or absent,
    /// the whole source is returned as <c>Before</c> with empty match/after.
    /// </summary>
    internal static (string Before, string Match, string After) HighlightSplit(string? source, string? query)
    {
        var text = source ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            return (text, string.Empty, string.Empty);
        }

        var index = text.IndexOf(query, System.StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return (text, string.Empty, string.Empty);
        }

        return (text[..index], text.Substring(index, query.Length), text[(index + query.Length)..]);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
        {
            return;
        }

        var source = GetSourceText(textBlock) ?? string.Empty;
        var query = GetQuery(textBlock) ?? string.Empty;
        var (before, match, after) = HighlightSplit(source, query);

        textBlock.Inlines.Clear();

        if (match.Length == 0)
        {
            textBlock.Inlines.Add(new Run(source));
            return;
        }

        if (before.Length > 0)
        {
            textBlock.Inlines.Add(new Run(before));
        }

        var matchRun = new Run(match) { FontWeight = FontWeights.Bold };
        if (Application.Current?.TryFindResource("AccentBrush") is Brush accent)
        {
            matchRun.Foreground = accent;
        }
        textBlock.Inlines.Add(matchRun);

        if (after.Length > 0)
        {
            textBlock.Inlines.Add(new Run(after));
        }
    }
}
