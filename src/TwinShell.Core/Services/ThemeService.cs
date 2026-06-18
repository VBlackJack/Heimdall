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

using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;

namespace TwinShell.Core.Services;

/// <summary>
/// Service for managing application themes (Light/Dark mode).
/// Handles dynamic theme switching by merging ResourceDictionaries.
/// BUGFIX: Now listens to Windows theme changes for System mode.
/// </summary>
public sealed class ThemeService : IThemeService, IDisposable
{
    private Theme _currentTheme = Theme.Light;
    private const string LightThemeUri = "/TwinShell.App;component/Themes/LightTheme.xaml";
    private const string DarkThemeUri = "/TwinShell.App;component/Themes/DarkTheme.xaml";
    private const string HighContrastThemeUri = "/TwinShell.App;component/Themes/HighContrastTheme.xaml";
    private readonly ILogger<ThemeService>? _logger;
    private readonly ISettingsService? _settingsService;

    // UI-008: Theme transition animation settings
    private const int ThemeTransitionDurationMs = 150;
    private bool _isFirstThemeApplication = true;

    /// <summary>
    /// Initializes the ThemeService and subscribes to Windows theme changes.
    /// </summary>
    public ThemeService(ILogger<ThemeService>? logger = null, ISettingsService? settingsService = null)
    {
        _logger = logger;
        _settingsService = settingsService;
        _logger?.LogInformation("ThemeService initialized");

        // BUGFIX: Subscribe to Windows theme changes to support dynamic System theme switching
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.UserPreferenceChanged += OnWindowsThemeChanged;
            _logger?.LogDebug("Subscribed to Windows theme changes");
        }
    }

    /// <inheritdoc/>
    public Theme CurrentTheme => _currentTheme;

    /// <inheritdoc/>
    public void ApplyTheme(Theme theme)
    {
        try
        {
            _logger?.LogInformation($"Applying theme: {theme}");

            var effectiveTheme = GetEffectiveTheme(theme);

            // Validation: Ensure Application.Current is available
            if (Application.Current == null)
            {
                _logger?.LogError("Application.Current is null - cannot apply theme");
                throw new InvalidOperationException("Application.Current is null. Theme can only be applied after Application initialization.");
            }

            // UI-008: Skip animation on first application (startup)
            if (_isFirstThemeApplication)
            {
                _isFirstThemeApplication = false;
                ApplyThemeInternal(theme, effectiveTheme);
                return;
            }

            // WCAG 2.3.3: Check for Reduced Motion accessibility setting
            var reducedMotion = _settingsService?.CurrentSettings?.ReducedMotion ?? false;
            if (reducedMotion)
            {
                _logger?.LogDebug("Reduced motion enabled, skipping theme transition animation");
                ApplyThemeInternal(theme, effectiveTheme);
                return;
            }

            // UI-008: Apply theme with smooth transition animation
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow != null)
            {
                ApplyThemeWithTransition(mainWindow, theme, effectiveTheme);
            }
            else
            {
                // Fallback: Apply without animation if no main window
                ApplyThemeInternal(theme, effectiveTheme);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Failed to apply theme: {theme}");
            throw;
        }
    }

    /// <summary>
    /// UI-008: Applies theme with a smooth fade transition animation.
    /// </summary>
    private void ApplyThemeWithTransition(Window window, Theme theme, Theme effectiveTheme)
    {
        _logger?.LogDebug("Applying theme with transition animation");

        var duration = TimeSpan.FromMilliseconds(ThemeTransitionDurationMs);

        // Create fade out animation
        var fadeOut = new DoubleAnimation(1.0, 0.85, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        // When fade out completes, switch theme and fade back in
        fadeOut.Completed += (s, e) =>
        {
            ApplyThemeInternal(theme, effectiveTheme);

            // Create fade in animation
            var fadeIn = new DoubleAnimation(0.85, 1.0, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            window.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };

        window.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    /// <summary>
    /// Internal method that performs the actual theme switch.
    /// </summary>
    private void ApplyThemeInternal(Theme theme, Theme effectiveTheme)
    {
        _currentTheme = theme;
        _logger?.LogDebug($"Effective theme: {effectiveTheme}");

        // Remove existing theme ResourceDictionaries
        RemoveExistingTheme();

        // Get the appropriate theme URI
        var themeUri = effectiveTheme switch
        {
            Theme.Dark => DarkThemeUri,
            Theme.HighContrast => HighContrastThemeUri,
            _ => LightThemeUri
        };
        _logger?.LogDebug($"Loading theme from: {themeUri}");

        // Load and merge the new theme ResourceDictionary
        var themeResourceDictionary = new ResourceDictionary
        {
            Source = new Uri(themeUri, UriKind.Relative)
        };

        Application.Current.Resources.MergedDictionaries.Add(themeResourceDictionary);
        _logger?.LogInformation($"Theme applied successfully: {theme} (effective: {effectiveTheme})");
    }

    /// <inheritdoc/>
    public Theme GetEffectiveTheme(Theme theme)
    {
        if (theme == Theme.System)
        {
            return DetectSystemTheme();
        }

        // HighContrast is its own effective theme
        return theme;
    }

    /// <inheritdoc/>
    public Theme DetectSystemTheme()
    {
        // BUGFIX: Check if running on Windows before accessing registry
        if (!OperatingSystem.IsWindows())
        {
            _logger?.LogDebug("Not running on Windows, defaulting to Light theme");
            return Theme.Light; // Default to Light on non-Windows platforms
        }

        try
        {
            // Check Windows Registry for system theme preference
            // Path: HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
            // Value: AppsUseLightTheme (0 = Dark, 1 = Light)
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            if (key != null)
            {
                var value = key.GetValue("AppsUseLightTheme");
                if (value is int intValue)
                {
                    var detectedTheme = intValue == 0 ? Theme.Dark : Theme.Light;
                    _logger?.LogInformation($"Windows system theme detected: {detectedTheme} (registry value: {intValue})");
                    return detectedTheme;
                }
            }

            _logger?.LogWarning("Could not read AppsUseLightTheme registry value, defaulting to Light");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to detect system theme from registry, defaulting to Light");
        }

        // Default to Light if unable to detect
        return Theme.Light;
    }

    /// <summary>
    /// Removes existing theme ResourceDictionaries from the application resources.
    /// </summary>
    private void RemoveExistingTheme()
    {
        var themesToRemove = Application.Current.Resources.MergedDictionaries
            .Where(d => d.Source != null &&
                       (d.Source.OriginalString.Contains("/Themes/LightTheme.xaml") ||
                        d.Source.OriginalString.Contains("/Themes/DarkTheme.xaml") ||
                        d.Source.OriginalString.Contains("/Themes/HighContrastTheme.xaml")))
            .ToList();

        _logger?.LogDebug($"Removing {themesToRemove.Count} existing theme dictionary/dictionaries");

        foreach (var theme in themesToRemove)
        {
            _logger?.LogTrace($"Removing theme dictionary: {theme.Source?.OriginalString}");
            Application.Current.Resources.MergedDictionaries.Remove(theme);
        }
    }

    /// <summary>
    /// BUGFIX: Handles Windows theme preference changes.
    /// When the user changes Windows theme and the app is in System mode, this updates the UI automatically.
    /// </summary>
    private void OnWindowsThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Only react to General category changes (which includes theme changes)
        // and only if the app is currently using System theme
        if (e.Category == UserPreferenceCategory.General && _currentTheme == Theme.System)
        {
            _logger?.LogInformation("Windows theme changed, reapplying System theme");

            // Use Dispatcher to ensure UI thread safety
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ApplyTheme(Theme.System);
            });
        }
    }

    /// <summary>
    /// Cleans up resources and unsubscribes from Windows theme events.
    /// </summary>
    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.UserPreferenceChanged -= OnWindowsThemeChanged;
            _logger?.LogDebug("Unsubscribed from Windows theme changes");
        }

        _logger?.LogInformation("ThemeService disposed");
    }
}
