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

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Heimdall.App.Services;

public static partial class SimpleMarkdownConverter
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*([-*_])(?:\s*\1){2,}\s*$")]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"^\s*```(?<lang>[\w+-]+)?\s*$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+\[(?<checked>[ xX])\]\s+(?<text>.+)$")]
    private static partial Regex TaskItemRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+(?<text>.+)$")]
    private static partial Regex BulletItemRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+(?<text>.+)$")]
    private static partial Regex OrderedItemRegex();

    [GeneratedRegex(@"@@TOKEN(?<index>\d+)@@")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^\s*\|?[\s:\-]+\|[\s:\-|]*$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"!\[(.*?)\]\((.+?)\)")]
    private static partial Regex InlineImageRegex();

    [GeneratedRegex(@"\[(.*?)\]\((.+?)\)")]
    private static partial Regex InlineLinkRegex();

    // A backslash may precede each opening bracket: the editor used to save [[X]] as \[\[X]],
    // and notes written that way still link until their next real edit rewrites them.
    [GeneratedRegex(@"\\?\[\\?\[(.+?)\]\]")]
    internal static partial Regex NoteLinkRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldAsteriskRegex();

    [GeneratedRegex(@"__(.+?)__")]
    private static partial Regex BoldUnderscoreRegex();

    [GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)")]
    private static partial Regex ItalicAsteriskRegex();

    [GeneratedRegex(@"(?<!_)_(?!_)(.+?)(?<!_)_(?!_)")]
    private static partial Regex ItalicUnderscoreRegex();

    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex StrikethroughRegex();

    public static string ToHtmlFragment(string? markdown)
    {
        var lines = NormalizeLines(markdown).ToArray();
        var builder = new StringBuilder();

        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                builder.Append("<h").Append(level).Append('>')
                    .Append(ConvertInline(heading.Groups[2].Value.Trim()))
                    .Append("</h").Append(level).AppendLine(">");
                index++;
                continue;
            }

            if (HorizontalRuleRegex().IsMatch(line))
            {
                builder.AppendLine("<hr />");
                index++;
                continue;
            }

            var fence = FenceRegex().Match(line);
            if (fence.Success)
            {
                index = AppendCodeBlock(lines, index, fence.Groups["lang"].Value, builder);
                continue;
            }

            if (IsTableHeader(lines, index))
            {
                index = AppendTable(lines, index, builder);
                continue;
            }

            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                index = AppendBlockQuote(lines, index, builder);
                continue;
            }

            if (TaskItemRegex().IsMatch(line) || BulletItemRegex().IsMatch(line))
            {
                index = AppendList(lines, index, builder, ordered: false);
                continue;
            }

            if (OrderedItemRegex().IsMatch(line))
            {
                index = AppendList(lines, index, builder, ordered: true);
                continue;
            }

            index = AppendParagraph(lines, index, builder);
        }

        return builder.ToString();
    }

    private static int AppendCodeBlock(IReadOnlyList<string> lines, int startIndex, string language, StringBuilder builder)
    {
        var codeBuilder = new StringBuilder();
        var index = startIndex + 1;

        while (index < lines.Count && !FenceRegex().IsMatch(lines[index]))
        {
            if (codeBuilder.Length > 0)
            {
                codeBuilder.Append('\n');
            }

            codeBuilder.Append(lines[index]);
            index++;
        }

        builder.Append("<pre><code");
        if (!string.IsNullOrWhiteSpace(language))
        {
            builder.Append(" class=\"language-")
                .Append(WebUtility.HtmlEncode(language))
                .Append('"');
        }

        builder.Append('>')
            .Append(WebUtility.HtmlEncode(codeBuilder.ToString()))
            .AppendLine("</code></pre>");

        return index < lines.Count ? index + 1 : index;
    }

    private static int AppendTable(IReadOnlyList<string> lines, int startIndex, StringBuilder builder)
    {
        var headerCells = SplitTableRow(lines[startIndex]);
        builder.AppendLine("<table><thead><tr>");
        foreach (var cell in headerCells)
        {
            builder.Append("<th>").Append(ConvertInline(cell)).AppendLine("</th>");
        }
        builder.AppendLine("</tr></thead><tbody>");

        var index = startIndex + 2;
        while (index < lines.Count && lines[index].Contains('|'))
        {
            var rowCells = SplitTableRow(lines[index]);
            builder.AppendLine("<tr>");
            foreach (var cell in rowCells)
            {
                builder.Append("<td>").Append(ConvertInline(cell)).AppendLine("</td>");
            }
            builder.AppendLine("</tr>");
            index++;
        }

        builder.AppendLine("</tbody></table>");
        return index;
    }

    private static int AppendBlockQuote(IReadOnlyList<string> lines, int startIndex, StringBuilder builder)
    {
        var quoteLines = new List<string>();
        var index = startIndex;

        while (index < lines.Count)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                quoteLines.Add(string.Empty);
                index++;
                continue;
            }

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                break;
            }

            quoteLines.Add(trimmed.Length > 1 && trimmed[1] == ' '
                ? trimmed[2..]
                : trimmed[1..]);
            index++;
        }

        builder.Append("<blockquote>")
            .Append(ToHtmlFragment(string.Join('\n', quoteLines)))
            .AppendLine("</blockquote>");

        return index;
    }

    private static int AppendList(IReadOnlyList<string> lines, int startIndex, StringBuilder builder, bool ordered)
    {
        builder.Append(ordered ? "<ol>" : "<ul>");

        var baseIndent = GetIndentLevel(lines[startIndex]);
        var index = startIndex;
        var itemOpen = false;

        while (index < lines.Count)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            var indent = GetIndentLevel(line);
            if (indent < baseIndent)
            {
                break;
            }

            if (indent > baseIndent)
            {
                var nestedOrdered = OrderedItemRegex().IsMatch(line);
                index = AppendList(lines, index, builder, nestedOrdered);
                continue;
            }

            if (itemOpen)
            {
                builder.Append("</li>");
                itemOpen = false;
            }

            var taskMatch = TaskItemRegex().Match(line);
            if (taskMatch.Success)
            {
                var isChecked = taskMatch.Groups["checked"].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
                builder.Append("<li><input type=\"checkbox\" disabled=\"disabled\"");
                if (isChecked)
                {
                    builder.Append(" checked=\"checked\"");
                }

                builder.Append(" /> ")
                    .Append(ConvertInline(taskMatch.Groups["text"].Value.Trim()));
                itemOpen = true;
                index++;
                continue;
            }

            var match = ordered ? OrderedItemRegex().Match(line) : BulletItemRegex().Match(line);
            if (!match.Success)
            {
                match = ordered ? BulletItemRegex().Match(line) : OrderedItemRegex().Match(line);
            }

            if (!match.Success)
            {
                break;
            }

            builder.Append("<li>")
                .Append(ConvertInline(match.Groups["text"].Value.Trim()));
            itemOpen = true;
            index++;
        }

        if (itemOpen)
        {
            builder.Append("</li>");
        }

        builder.AppendLine(ordered ? "</ol>" : "</ul>");
        return index;
    }

    private static int GetIndentLevel(string line)
    {
        var count = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') count++;
            else if (ch == '\t') count += 4;
            else break;
        }

        return count;
    }

    private static int AppendParagraph(IReadOnlyList<string> lines, int startIndex, StringBuilder builder)
    {
        var paragraphLines = new List<string>();
        var index = startIndex;

        while (index < lines.Count && !string.IsNullOrWhiteSpace(lines[index]))
        {
            if (HeadingRegex().IsMatch(lines[index])
                || FenceRegex().IsMatch(lines[index])
                || HorizontalRuleRegex().IsMatch(lines[index])
                || TaskItemRegex().IsMatch(lines[index])
                || BulletItemRegex().IsMatch(lines[index])
                || OrderedItemRegex().IsMatch(lines[index])
                || lines[index].TrimStart().StartsWith(">", StringComparison.Ordinal)
                || IsTableHeader(lines, index))
            {
                break;
            }

            paragraphLines.Add(lines[index]);
            index++;
        }

        builder.Append("<p>")
            .Append(ConvertInline(string.Join('\n', paragraphLines)))
            .AppendLine("</p>");

        return index;
    }

    private static bool IsTableHeader(IReadOnlyList<string> lines, int index)
    {
        if (index + 1 >= lines.Count)
        {
            return false;
        }

        return lines[index].Contains('|')
            && TableSeparatorRegex().IsMatch(lines[index + 1]);
    }

    private static IEnumerable<string> NormalizeLines(string? markdown)
        => (markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static IReadOnlyList<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
    }

    private static string ConvertInline(string text)
    {
        var placeholders = new List<string>();

        string Store(string html)
        {
            placeholders.Add(html);
            return $"@@TOKEN{placeholders.Count - 1}@@";
        }

        var value = text ?? string.Empty;
        value = InlineCodeRegex().Replace(value, match => Store($"<code>{WebUtility.HtmlEncode(match.Groups[1].Value)}</code>"));
        value = InlineImageRegex().Replace(value, match =>
            Store($"""<img src="{WebUtility.HtmlEncode(match.Groups[2].Value)}" alt="{WebUtility.HtmlEncode(match.Groups[1].Value)}" />"""));
        value = NoteLinkRegex().Replace(value, match =>
        {
            var noteReference = match.Groups[1].Value.Trim();
            return Store($"""<a href="#note:{WebUtility.HtmlEncode(noteReference)}" class="note-link">{WebUtility.HtmlEncode(noteReference)}</a>""");
        });
        value = InlineLinkRegex().Replace(value, match =>
            Store($"""<a href="{WebUtility.HtmlEncode(match.Groups[2].Value)}">{ConvertInline(match.Groups[1].Value)}</a>"""));

        value = WebUtility.HtmlEncode(value);
        value = BoldAsteriskRegex().Replace(value, "<strong>$1</strong>");
        value = BoldUnderscoreRegex().Replace(value, "<strong>$1</strong>");
        value = ItalicAsteriskRegex().Replace(value, "<em>$1</em>");
        value = ItalicUnderscoreRegex().Replace(value, "<em>$1</em>");
        value = StrikethroughRegex().Replace(value, "<del>$1</del>");
        value = value.Replace("\n", "<br />", StringComparison.Ordinal);

        return PlaceholderRegex().Replace(value, match =>
        {
            var index = int.Parse(match.Groups["index"].Value);
            return index >= 0 && index < placeholders.Count ? placeholders[index] : match.Value;
        });
    }
}
