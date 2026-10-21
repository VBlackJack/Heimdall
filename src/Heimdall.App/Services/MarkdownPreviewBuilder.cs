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
using System.Windows;
using System.Windows.Media;

namespace Heimdall.App.Services;

public static class MarkdownPreviewBuilder
{
    public static string BuildHtmlDocument(string markdown, string title)
    {
        var body = SimpleMarkdownConverter.ToHtmlFragment(markdown);

        var background = ResolveBrush("CardBrush", "#10151d");
        var text = ResolveBrush("TextPrimaryBrush", "#eef2f7");
        var textSecondary = ResolveBrush("TextSecondaryBrush", "#aeb7c4");
        var border = ResolveBrush("BorderBrush", "#2b3442");
        var accent = ResolveBrush("AccentBrush", "#4aa3ff");
        var accentText = ResolveBrush("AccentTextBrush", "#4aa3ff");
        var highlight = ResolveBrush("HighlightBrush", "#182131");
        var surface = ResolveBrush("SurfaceBrush", "#0f141c");

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"" />
<meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
<title>{WebUtility.HtmlEncode(title)}</title>
<style>
    :root {{
        color-scheme: dark light;
        --bg: {background};
        --surface: {surface};
        --text: {text};
        --text-secondary: {textSecondary};
        --border: {border};
        --accent: {accent};
        --accent-text: {accentText};
        --highlight: {highlight};
    }}

    html, body {{
        margin: 0;
        padding: 0;
        background: var(--bg);
        color: var(--text);
        font-family: ""Segoe UI"", Arial, sans-serif;
        line-height: 1.6;
    }}

    body {{
        padding: 20px 24px 40px;
    }}

    .note {{
        max-width: 980px;
        margin: 0 auto;
    }}

    h1, h2, h3, h4, h5, h6 {{
        color: var(--text);
        margin-top: 1.4em;
        margin-bottom: 0.45em;
        line-height: 1.25;
    }}

    h1 {{
        border-bottom: 1px solid var(--border);
        padding-bottom: 0.3em;
    }}

    p, li, blockquote, td, th {{
        color: var(--text);
    }}

    a {{
        color: var(--accent-text);
    }}

    .note-link {{
        color: var(--accent-text);
        font-style: italic;
    }}

    code {{
        background: var(--highlight);
        border: 1px solid var(--border);
        border-radius: 6px;
        padding: 0.1em 0.35em;
        font-family: Consolas, ""Cascadia Code"", monospace;
        font-size: 0.95em;
    }}

    pre {{
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 10px;
        overflow: auto;
        padding: 14px 16px;
    }}

    pre code {{
        background: transparent;
        border: 0;
        padding: 0;
    }}

    blockquote {{
        margin: 1em 0;
        padding: 0.75em 1em;
        border-left: 4px solid var(--accent);
        background: var(--highlight);
        color: var(--text-secondary);
    }}

    table {{
        width: 100%;
        border-collapse: collapse;
        margin: 1em 0;
        border: 1px solid var(--border);
    }}

    th, td {{
        border: 1px solid var(--border);
        padding: 8px 10px;
        vertical-align: top;
    }}

    th {{
        background: var(--highlight);
        text-align: left;
    }}

    hr {{
        border: 0;
        border-top: 1px solid var(--border);
        margin: 1.5em 0;
    }}

    img {{
        max-width: 100%;
        border-radius: 8px;
    }}
</style>
</head>
<body>
    <div class=""note"">
        {body}
    </div>
</body>
</html>";
    }

    private static string ResolveBrush(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            var color = brush.Color;
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        return fallback;
    }
}
