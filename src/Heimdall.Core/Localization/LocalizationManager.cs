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

using System.Text.Json;

namespace Heimdall.Core.Localization;

/// <summary>
/// Manages localized string resources loaded from JSON locale files.
/// Supports runtime locale switching and format string interpolation.
/// </summary>
public sealed class LocalizationManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private Dictionary<string, string> _strings = new();
    private string _currentLocale = "en";
    private string _localesPath = string.Empty;

    /// <summary>
    /// Raised after the locale has been switched. Subscribers should
    /// re-apply localized strings to their UI elements.
    /// </summary>
    public event Action<string>? LocaleChanged;

    /// <summary>
    /// Gets the current locale identifier (e.g., "en", "fr").
    /// </summary>
    public string CurrentLocale => _currentLocale;

    /// <summary>
    /// Gets a localized string by key. Returns the key itself if not found.
    /// </summary>
    public string this[string key] => GetString(key);

    /// <summary>
    /// Gets a localized string by key. Returns the key itself if not found.
    /// </summary>
    /// <param name="key">The locale key (e.g., "StatusReady", "ErrorPlinkNotFound").</param>
    /// <returns>The localized string, or the key if no translation exists.</returns>
    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _strings.TryGetValue(key, out var value) ? value : key;
    }

    /// <summary>
    /// Gets a localized format string and applies <see cref="string.Format(string, object[])"/> with the given arguments.
    /// </summary>
    /// <param name="key">The locale key containing a format template (e.g., "StatusConnecting" with {0} placeholder).</param>
    /// <param name="args">Format arguments to interpolate into the template.</param>
    /// <returns>The formatted localized string, or the key if no translation exists.</returns>
    public string Format(string key, params object[] args)
    {
        var template = GetString(key);

        if (args.Length == 0 || template == key)
        {
            return template;
        }

        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            // Malformed format string in locale file; return template as-is
            return template;
        }
    }

    /// <summary>
    /// Formats a count with the wording its number takes in the current language.
    /// </summary>
    /// <param name="count">The number the wording agrees with.</param>
    /// <param name="oneKey">The key of the singular wording; it names its number with a placeholder.</param>
    /// <param name="otherKey">The key of the plural wording.</param>
    /// <param name="args">Format arguments, the count included where the wording places it.</param>
    /// <returns>The formatted string from the key the language's plural rule picks.</returns>
    /// <remarks>
    /// The project words counts with a key pair, "X" and "XOne", instead of a "(s)" hack. Which of
    /// the two a number takes is the language's decision, not the caller's: French uses the
    /// singular for 0 as well as 1, so a caller testing <c>count == 1</c> wrote "0 sessions" in
    /// French. Both keys are passed by name so a reader, and the dead-key guard, can find them.
    /// </remarks>
    public string FormatCount(long count, string oneKey, string otherKey, params object[] args)
        => Format(PluralRules.IsOne(_currentLocale, count) ? oneKey : otherKey, args);

    /// <summary>
    /// Loads locale strings from a JSON file in the specified locales directory.
    /// </summary>
    /// <param name="localesPath">Directory containing locale JSON files (e.g., "locales/").</param>
    /// <param name="locale">Locale identifier (e.g., "en", "fr").</param>
    /// <exception cref="FileNotFoundException">Thrown if the locale file does not exist.</exception>
    public async Task LoadAsync(string localesPath, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        var filePath = Path.Combine(localesPath, $"{locale}.json");

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"Locale file not found: {filePath}", filePath);
        }

        var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);

        _strings = parsed ?? new Dictionary<string, string>();
        _currentLocale = locale;
        _localesPath = localesPath;
    }

    /// <summary>
    /// Switches to a different locale at runtime, reloading strings from disk.
    /// </summary>
    /// <param name="locale">The new locale identifier.</param>
    /// <exception cref="InvalidOperationException">Thrown if no locales path has been configured via <see cref="LoadAsync"/>.</exception>
    public async Task SwitchLocaleAsync(string locale)
    {
        if (string.IsNullOrWhiteSpace(_localesPath))
        {
            throw new InvalidOperationException(
                "Cannot switch locale before initial load. Call LoadAsync first.");
        }

        await LoadAsync(_localesPath, locale).ConfigureAwait(false);
        LocaleChanged?.Invoke(locale);
    }

    /// <summary>
    /// Returns the list of available locale identifiers by scanning the locales directory.
    /// </summary>
    public IReadOnlyList<string> GetAvailableLocales()
    {
        if (string.IsNullOrWhiteSpace(_localesPath) || !Directory.Exists(_localesPath))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(_localesPath, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Checks whether a given key exists in the current locale.
    /// </summary>
    public bool HasKey(string key)
    {
        return !string.IsNullOrWhiteSpace(key) && _strings.ContainsKey(key);
    }

    /// <summary>
    /// Returns the total number of loaded locale keys.
    /// </summary>
    public int KeyCount => _strings.Count;
}
