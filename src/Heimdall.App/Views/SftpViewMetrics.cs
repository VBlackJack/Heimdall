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

namespace Heimdall.App.Views;

/// <summary>
/// The timings and thresholds of the SFTP browser view, named once so the markup and the
/// code-behind share them instead of repeating bare numbers.
/// </summary>
public static class SftpViewMetrics
{
    /// <summary>
    /// How long the filter box waits after a keystroke before filtering. Filtering a directory of
    /// tens of thousands of entries on every keystroke rebuilt the list at typing speed.
    /// </summary>
    public const int FilterInputDelayMilliseconds = 200;

    /// <summary>
    /// How often the transfer bar redraws. A browser raises a progress event per buffer, thousands
    /// a second at a good rate; the bar only needs to move a few times a second.
    /// </summary>
    public const int ProgressRefreshMilliseconds = 100;

    /// <summary>
    /// How often the connection is probed. Distinct from the operation timeout it used to borrow:
    /// a dropped session was noticed up to thirty seconds late.
    /// </summary>
    public const int HealthCheckIntervalSeconds = 5;

    /// <summary>Toolbar width in pixels under which the labelled actions collapse into the overflow menu.</summary>
    public const double ToolbarCompactThresholdPx = 780;

    /// <summary>The size of the glyph in a bookmark menu entry.</summary>
    public const double MenuIconFontSize = 14;

    /// <summary>The icon font the glyphs of this view are drawn with.</summary>
    public const string IconFontFamilyName = "Segoe MDL2 Assets";

    /// <summary>The glyph of a folder, shown on a bookmark.</summary>
    public const string FolderGlyph = "";
}
