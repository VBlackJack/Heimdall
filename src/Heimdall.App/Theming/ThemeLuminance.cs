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

using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace Heimdall.App.Theming;

/// <summary>
/// Whether the active theme is a dark one, read from the colour of its background rather than
/// from its name: a ThemeForge palette carries no flag the app could trust across versions.
/// </summary>
internal static class ThemeLuminance
{
    /// <summary>Perceived luminance, from 0 to 1, below which a background counts as dark.</summary>
    internal const double DarkThreshold = 0.5;

    /// <summary>Whether a background colour is dark, by its perceived (Rec. 601) luminance.</summary>
    /// <param name="background">The background colour.</param>
    internal static bool IsDark(Color background) =>
        ((0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B)) / 255.0 < DarkThreshold;

    /// <summary>
    /// Whether the application's current background is dark; true when no theme is loaded, the
    /// Dracula default.
    /// </summary>
    internal static bool IsCurrentThemeDark() =>
        Application.Current?.TryFindResource("BackgroundBrush") is not SolidColorBrush background
        || IsDark(background.Color);
}
