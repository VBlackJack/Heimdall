/*
 * Copyright 2025 Julien Bombled
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

using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Logging;
using TwinShell.Core.Interfaces;

namespace TwinShell.Core.Services;

/// <summary>
/// Service for managing application localization
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private CultureInfo _currentCulture;
    private readonly ResourceManager _resourceManager;
    private readonly ILogger<LocalizationService>? _logger;

    public LocalizationService(ILogger<LocalizationService>? logger = null)
    {
        _logger = logger;
        // Default to English (en)
        _currentCulture = new CultureInfo("en");

        // Use the correct namespace for the resource files
        _resourceManager = new ResourceManager("TwinShell.Core.Resources.Strings", typeof(LocalizationService).Assembly);
    }

    public CultureInfo CurrentCulture => _currentCulture;

    public CultureInfo[] SupportedCultures => new[]
    {
        new CultureInfo("en"),  // English (default)
        new CultureInfo("fr")   // French
    };

    public event EventHandler? LanguageChanged;

    public void ChangeLanguage(CultureInfo culture)
    {
        if (culture == null)
            throw new ArgumentNullException(nameof(culture));

        if (!SupportedCultures.Any(c => c.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName))
            throw new NotSupportedException($"Culture '{culture.Name}' is not supported");

        _currentCulture = culture;

        // Update thread culture
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        // Update current culture for future threads
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ChangeLanguage(string cultureCode)
    {
        if (string.IsNullOrWhiteSpace(cultureCode))
            throw new ArgumentException("Culture code cannot be null or empty", nameof(cultureCode));

        var culture = new CultureInfo(cultureCode);
        ChangeLanguage(culture);
    }

    public string GetString(string key)
    {
        try
        {
            var value = _resourceManager.GetString(key, _currentCulture);
            return value ?? key; // Return key if not found
        }
        catch (Exception ex)
        {
            // BUGFIX: Log exceptions instead of swallowing them
            _logger?.LogWarning(ex, "Failed to get localized string for key: {Key}", key);
            return key;
        }
    }

    public string GetString(string key, string fallback)
    {
        try
        {
            var value = _resourceManager.GetString(key, _currentCulture);
            return value ?? fallback;
        }
        catch (Exception ex)
        {
            // BUGFIX: Log exceptions instead of swallowing them
            _logger?.LogWarning(ex, "Failed to get localized string for key: {Key}", key);
            return fallback;
        }
    }

    public string GetFormattedString(string key, params object[] args)
    {
        try
        {
            var format = _resourceManager.GetString(key, _currentCulture);
            if (format == null)
                return key;

            return string.Format(format, args);
        }
        catch (Exception ex)
        {
            // BUGFIX: Log exceptions instead of swallowing them
            _logger?.LogWarning(ex, "Failed to get formatted string for key: {Key}", key);
            return key;
        }
    }
}
