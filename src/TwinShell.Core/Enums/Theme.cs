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

namespace TwinShell.Core.Enums;

/// <summary>
/// Represents the application theme options.
/// </summary>
public enum Theme
{
    /// <summary>
    /// Light theme with bright colors and dark text.
    /// </summary>
    Light = 0,

    /// <summary>
    /// Dark theme with dark colors and light text (WCAG AA compliant).
    /// </summary>
    Dark = 1,

    /// <summary>
    /// Follows the system theme preference (Windows theme).
    /// </summary>
    System = 2,

    /// <summary>
    /// High contrast theme for accessibility (WCAG AAA+ compliant, 10:1+ contrast).
    /// </summary>
    HighContrast = 3
}
