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

namespace Heimdall.App.Theming;

/// <summary>
/// The brush each semantic colour is painted with as text. The raw brushes (<c>ErrorBrush</c>,
/// <c>AccentBrush</c>...) carry the theme's colour as it is, tuned for fills, dots and borders; as
/// text they fall under 4.5 to 1 on several themes (the Drakul error red is 2.91 on a card). The
/// <c>*TextBrush</c> keys are the colours <see cref="ThemeTextColours"/> derives to read on every
/// ground text is drawn on.
/// </summary>
internal static class SemanticTextBrushes
{
    private static readonly FrozenDictionary<string, string> TextKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ErrorBrush"] = "ErrorTextBrush",
        ["WarningBrush"] = "WarningTextBrush",
        ["SuccessBrush"] = "SuccessTextBrush",
        ["InfoBrush"] = "InfoTextBrush",
        ["AccentBrush"] = "AccentTextBrush",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The raw semantic brush keys, which must not paint text.</summary>
    internal static IReadOnlyCollection<string> RawKeys => TextKeys.Keys;

    /// <summary>
    /// The key to paint text with where a site holds one key for a fill and for the text drawn with
    /// it: the derived text brush of a raw semantic brush, any other key unchanged.
    /// </summary>
    /// <param name="brushKey">The resource key the site resolved.</param>
    internal static string TextKeyFor(string brushKey) =>
        TextKeys.TryGetValue(brushKey, out string? textKey) ? textKey : brushKey;
}
