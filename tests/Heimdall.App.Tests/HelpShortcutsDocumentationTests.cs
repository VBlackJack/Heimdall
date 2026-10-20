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

using System.IO;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes what the F1 shortcut help lists, in every shipped locale.
/// </summary>
/// <remarks>
/// The help text is the only place the product states which keys it has taken, so a
/// shortcut missing from it is undiscoverable and reads to the user as a swallowed key.
/// The entries are compared token by token rather than by substring: "Ctrl+Shift+S" starts
/// with "Ctrl+S", so a substring search reports the Ctrl+S line as present while it is not.
/// </remarks>
public sealed class HelpShortcutsDocumentationTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task HelpShortcuts_ListSaveSettings(string locale)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        Dictionary<string, string> entries = ParseShortcutEntries(localizer["HelpShortcutsContent"]);

        Assert.True(
            entries.TryGetValue("Ctrl+S", out string? saveDescription),
            $"The {locale} shortcut help lists no Ctrl+S entry. Listed: {string.Join(", ", entries.Keys)}");
        Assert.False(string.IsNullOrWhiteSpace(saveDescription));

        // The screenshot shortcut shares the S key and is one careless edit away from being
        // overwritten by the line added above.
        Assert.True(
            entries.ContainsKey(ScreenshotToken(locale)),
            $"The {locale} shortcut help no longer lists the screenshot shortcut.");
    }

    /// <summary>
    /// The modifier names are themselves localized, so the screenshot entry is not spelled
    /// the same way in both files.
    /// </summary>
    private static string ScreenshotToken(string locale) => ModifiedToken(locale, 'S');

    /// <summary>The Ctrl+Shift chord on a letter, spelled as the locale spells its Shift key.</summary>
    private static string ModifiedToken(string locale, char letter) => locale switch
    {
        "fr" => $"Ctrl+Maj+{letter}",
        "es" => $"Ctrl+May\u00fas+{letter}",
        _ => $"Ctrl+Shift+{letter}",
    };

    /// <summary>
    /// "Copy output, secrets masked" is bound to Ctrl+Shift+M by <c>MainWindow.RegisterMaskedCopyShortcut</c>;
    /// a key the help does not list reads as a swallowed key.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task HelpShortcuts_ListMaskedCopy(string locale)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        Dictionary<string, string> entries = ParseShortcutEntries(localizer["HelpShortcutsContent"]);

        Assert.True(
            entries.TryGetValue(ModifiedToken(locale, 'M'), out string? description),
            $"The {locale} shortcut help lists no masked copy entry. Listed: {string.Join(", ", entries.Keys)}");
        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    /// <summary>
    /// Splits the help text into its key/description pairs. Entries are the indented,
    /// tab-separated lines; the section headings between them have neither.
    /// </summary>
    private static Dictionary<string, string> ParseShortcutEntries(string content)
    {
        Dictionary<string, string> entries = new(StringComparer.Ordinal);

        foreach (string line in content.Split('\n'))
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 2)
            {
                continue;
            }

            string token = parts[0].Trim();
            if (token.Length > 0)
            {
                entries[token] = parts[1].Trim();
            }
        }

        return entries;
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
