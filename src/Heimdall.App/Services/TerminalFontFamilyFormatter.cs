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


using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// Turns the free-text terminal font setting into a safe CSS font list and injects it into the
/// terminal page, once as raw CSS and once as a JSON string literal.
/// </summary>
internal static class TerminalFontFamilyFormatter
{
    internal const string MarkerCss = "/*{{TERMINAL_FONT_FAMILY_CSS}}*/";
    internal const string MarkerJson = "/*{{TERMINAL_FONT_FAMILY_JSON}}*/";

    private const char NameSeparator = ',';
    private const char DoubleQuote = '"';
    private const char SingleQuote = '\'';
    private const string ListSeparator = ", ";
    private const string FallbackFontName = "Courier New";
    private const string GenericFamily = "monospace";

    // CSS generic family keywords are not font names: quoted, they would stop meaning the generic.
    private static readonly FrozenSet<string> GenericFamilies = new[]
    {
        "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui", "ui-monospace",
        "ui-serif", "ui-sans-serif", "ui-rounded", "math", "emoji", "fangsong",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Characters that could end a CSS string, open markup, or start a comment or block.
    private const string ForbiddenCharacters = "\"\\<>;{}/";

    /// <summary>The font stack: each accepted name quoted, then the fallbacks.</summary>
    public static string BuildCssFontList(string? fontFamily)
    {
        var names = new List<string>();
        foreach (string part in (fontFamily ?? string.Empty).Split(NameSeparator))
        {
            string name = Unquote(part.Trim());
            if (name.Length > 0 && IsAcceptable(name))
            {
                names.Add(name);
            }
        }

        if (names.Count == 0)
        {
            names.Add(AppSettings.DefaultTerminalFontFamily);
        }

        var builder = new StringBuilder();
        foreach (string name in names)
        {
            if (GenericFamilies.Contains(name))
            {
                builder.Append(name).Append(ListSeparator);
            }
            else
            {
                builder.Append(DoubleQuote).Append(name).Append(DoubleQuote).Append(ListSeparator);
            }

        }

        builder.Append(DoubleQuote).Append(FallbackFontName).Append(DoubleQuote)
            .Append(ListSeparator).Append(GenericFamily);
        return builder.ToString();
    }

    /// <summary>Replaces both font placeholders of the terminal page.</summary>
    public static string Apply(string html, string? fontFamily)
    {
        ArgumentNullException.ThrowIfNull(html);

        string list = BuildCssFontList(fontFamily);
        return html
            .Replace(MarkerCss, list, StringComparison.Ordinal)
            .Replace(MarkerJson, JsonSerializer.Serialize(list), StringComparison.Ordinal);
    }

    private static string Unquote(string name)
    {
        while (name.Length >= 2
            && (name[0] == DoubleQuote || name[0] == SingleQuote)
            && name[^1] == name[0])
        {
            name = name[1..^1].Trim();
        }

        return name;
    }

    private static bool IsAcceptable(string name)
    {
        foreach (char c in name)
        {
            if (char.IsControl(c) || ForbiddenCharacters.Contains(c))
            {
                return false;
            }
        }

        return true;
    }
}
