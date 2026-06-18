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

using TwinShell.Core.Enums;

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Service for managing application themes (Light/Dark mode).
/// BUGFIX: Now implements IDisposable to properly clean up Windows theme event subscriptions.
/// </summary>
public interface IThemeService : IDisposable
{
    /// <summary>
    /// Gets the currently active theme.
    /// </summary>
    Theme CurrentTheme { get; }

    /// <summary>
    /// Applies the specified theme to the application.
    /// This merges the appropriate ResourceDictionary into Application.Current.Resources.
    /// </summary>
    /// <param name="theme">The theme to apply (Light, Dark, or System).</param>
    void ApplyTheme(Theme theme);

    /// <summary>
    /// Gets the effective theme based on the specified theme and system settings.
    /// If theme is System, returns the current Windows theme (Light or Dark).
    /// </summary>
    /// <param name="theme">The theme preference.</param>
    /// <returns>The effective theme (Light or Dark).</returns>
    Theme GetEffectiveTheme(Theme theme);

    /// <summary>
    /// Detects the current Windows system theme.
    /// </summary>
    /// <returns>Light or Dark based on Windows theme settings.</returns>
    Theme DetectSystemTheme();
}
