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
using System.Text.Json;

namespace Heimdall.App.Services;

internal static class TerminalHtmlLocalizer
{
    // English fallbacks used when the localize callback returns null, whitespace,
    // or the raw key, which is how LocalizationManager signals a missing entry.
    // These constants must stay in sync with the marker text in terminal.html.
    internal const string FallbackLoadingLabel = "Initializing terminal\u2026";
    internal const string FallbackSearchPlaceholder = "Search...";
    internal const string FallbackSessionEnded = "--- Session ended ---";
    internal const string FallbackSearchPrevious = "Previous match";
    internal const string FallbackSearchNext = "Next match";
    internal const string FallbackSearchClose = "Close search";
    internal const string FallbackSearchNoResults = "No results";
    internal const string FallbackSearchCountFormat = "{0} / {1}";

    internal const string KeyLoadingLabel = "TerminalLoadingLabel";
    internal const string KeySearchPlaceholder = "TerminalSearchPlaceholder";
    internal const string KeySessionEnded = "TerminalSessionEnded";
    internal const string KeySearchPrevious = "TerminalSearchPrevious";
    internal const string KeySearchNext = "TerminalSearchNext";
    internal const string KeySearchClose = "TerminalSearchClose";
    internal const string KeySearchNoResults = "TerminalSearchNoResults";
    internal const string KeySearchCountFormat = "TerminalSearchCount";

    internal const string MarkerLoadingLabel = "Initializing terminal&#x2026;";
    internal const string MarkerSearchPlaceholder = "Search...";
    internal const string MarkerSessionEndedLiteral = "/*{{TERMINAL_SESSION_ENDED_LITERAL}}*/";
    internal const string MarkerSearchPrevious = "{{TERMINAL_SEARCH_PREVIOUS_LABEL}}";
    internal const string MarkerSearchNext = "{{TERMINAL_SEARCH_NEXT_LABEL}}";
    internal const string MarkerSearchClose = "{{TERMINAL_SEARCH_CLOSE_LABEL}}";
    internal const string MarkerSearchNoResultsLiteral = "/*{{TERMINAL_SEARCH_NO_RESULTS_LITERAL}}*/";
    internal const string MarkerSearchCountFormatLiteral = "/*{{TERMINAL_SEARCH_COUNT_FORMAT_LITERAL}}*/";

    public static string Localize(string html, Func<string, string?> localize)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(localize);

        string loadingLabel = ResolveOrFallback(localize, KeyLoadingLabel, FallbackLoadingLabel);
        string searchPlaceholder = ResolveOrFallback(localize, KeySearchPlaceholder, FallbackSearchPlaceholder);
        string sessionEnded = ResolveOrFallback(localize, KeySessionEnded, FallbackSessionEnded);

        string localizedHtml = html.Replace(
            MarkerLoadingLabel,
            WebUtility.HtmlEncode(loadingLabel),
            StringComparison.Ordinal);
        localizedHtml = localizedHtml.Replace(
            MarkerSearchPlaceholder,
            WebUtility.HtmlEncode(searchPlaceholder),
            StringComparison.Ordinal);
        localizedHtml = localizedHtml.Replace(
            MarkerSessionEndedLiteral,
            JsonSerializer.Serialize(sessionEnded),
            StringComparison.Ordinal);

        localizedHtml = localizedHtml.Replace(
            MarkerSearchPrevious,
            WebUtility.HtmlEncode(ResolveOrFallback(localize, KeySearchPrevious, FallbackSearchPrevious)),
            StringComparison.Ordinal);
        localizedHtml = localizedHtml.Replace(
            MarkerSearchNext,
            WebUtility.HtmlEncode(ResolveOrFallback(localize, KeySearchNext, FallbackSearchNext)),
            StringComparison.Ordinal);
        localizedHtml = localizedHtml.Replace(
            MarkerSearchClose,
            WebUtility.HtmlEncode(ResolveOrFallback(localize, KeySearchClose, FallbackSearchClose)),
            StringComparison.Ordinal);
        localizedHtml = localizedHtml.Replace(
            MarkerSearchNoResultsLiteral,
            JsonSerializer.Serialize(ResolveOrFallback(localize, KeySearchNoResults, FallbackSearchNoResults)),
            StringComparison.Ordinal);
        localizedHtml = localizedHtml.Replace(
            MarkerSearchCountFormatLiteral,
            JsonSerializer.Serialize(ResolveOrFallback(localize, KeySearchCountFormat, FallbackSearchCountFormat)),
            StringComparison.Ordinal);

        return localizedHtml;
    }

    /// <summary>
    /// The page's localized texts as one JSON object, for the <c>set-labels:</c> message sent
    /// when the interface language changes after the page was built.
    /// </summary>
    public static string BuildLabelsJson(Func<string, string?> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);

        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["placeholder"] = ResolveOrFallback(localize, KeySearchPlaceholder, FallbackSearchPlaceholder),
            ["previous"] = ResolveOrFallback(localize, KeySearchPrevious, FallbackSearchPrevious),
            ["next"] = ResolveOrFallback(localize, KeySearchNext, FallbackSearchNext),
            ["close"] = ResolveOrFallback(localize, KeySearchClose, FallbackSearchClose),
            ["noResults"] = ResolveOrFallback(localize, KeySearchNoResults, FallbackSearchNoResults),
            ["countFormat"] = ResolveOrFallback(localize, KeySearchCountFormat, FallbackSearchCountFormat),
            ["sessionEnded"] = ResolveOrFallback(localize, KeySessionEnded, FallbackSessionEnded),
        });
    }

    private static string ResolveOrFallback(
        Func<string, string?> localize,
        string key,
        string fallback)
    {
        string? value = localize(key);
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, key, StringComparison.Ordinal))
        {
            return fallback;
        }

        return value;
    }
}
