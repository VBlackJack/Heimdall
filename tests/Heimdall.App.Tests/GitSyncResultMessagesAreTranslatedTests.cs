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
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using TwinShell.Core.Constants;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins that every message a Git sync returns is translated in every locale.
/// </summary>
/// <remarks>
/// <para>
/// The sync service runs in the TwinShell layer and reaches Heimdall's catalogue through the
/// localization bridge, which hands the key back when it is missing. A result message without a
/// translation would show the operator its key in the sync summary, and nothing would fail.
/// </para>
/// <para>
/// The set is read off <see cref="MessageKeys"/> by the <c>GitSyncResult</c> field prefix rather
/// than written out here, so a new result message fails this file until it is translated.
/// </para>
/// </remarks>
public sealed class GitSyncResultMessagesAreTranslatedTests
{
    private const string ResultFieldPrefix = "GitSyncResult";

    /// <summary>The number of result messages on the day they were moved to locale keys.</summary>
    private const int KnownResultMessageCount = 33;

    private static readonly string[] Locales = ["en", "fr", "es"];

    private static readonly Regex s_placeholder = new(@"\{\d+\}", RegexOptions.Compiled);

    private static IReadOnlyList<string> ResultMessageKeys { get; } =
        typeof(MessageKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral
                && field.FieldType == typeof(string)
                && field.Name.StartsWith(ResultFieldPrefix, StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The tests below iterate the set, so a set that came back empty would leave each of them
    /// green over nothing.
    /// </summary>
    [Fact]
    public void TheResultMessagesAreFoundByReflection()
    {
        Assert.True(
            ResultMessageKeys.Count >= KnownResultMessageCount,
            $"Expected at least {KnownResultMessageCount} result messages, found {ResultMessageKeys.Count}: "
                + string.Join(", ", ResultMessageKeys));
    }

    [Fact]
    public void EveryResultMessageIsTranslatedInEveryLocale()
    {
        List<string> missing = [];

        foreach (string locale in Locales)
        {
            Dictionary<string, string> strings = LoadLocale(locale);
            foreach (string key in ResultMessageKeys)
            {
                if (!strings.TryGetValue(key, out string? text) || string.IsNullOrWhiteSpace(text) || text == key)
                {
                    missing.Add($"{locale}: {key}");
                }
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// The service formats each message with its figures; a translation missing a placeholder
    /// drops a figure, and one with an extra placeholder throws when it is formatted.
    /// </summary>
    [Fact]
    public void EveryTranslationCarriesTheEnglishPlaceholders()
    {
        Dictionary<string, string> english = LoadLocale("en");
        List<string> mismatched = [];

        foreach (string locale in Locales)
        {
            Dictionary<string, string> strings = LoadLocale(locale);
            foreach (string key in ResultMessageKeys)
            {
                if (!strings.TryGetValue(key, out string? text))
                {
                    continue;
                }

                if (!Placeholders(text).SetEquals(Placeholders(english[key])))
                {
                    mismatched.Add($"{locale}: {key}");
                }
            }
        }

        Assert.Empty(mismatched);
    }

    private static HashSet<string> Placeholders(string text)
        => s_placeholder.Matches(text).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> LoadLocale(string locale)
    {
        string path = Path.Combine(ViewSource.RepoRoot(), "locales", locale + ".json");
        Assert.True(File.Exists(path), $"Locale file not found: {path}");

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Locale {locale} did not parse.");
    }
}
