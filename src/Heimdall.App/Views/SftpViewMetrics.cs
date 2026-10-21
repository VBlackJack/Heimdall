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

    /// <summary>The resource key of the icon font the glyphs of this view are drawn with.</summary>
    public const string IconFontFamilyKey = "FontFamilyIcons";

    /// <summary>
    /// The width a file-browser row adds around its columns: the one-pixel border of each side and
    /// the four-pixel padding of each side from <c>FileBrowserStyles.xaml</c>, plus the two pixels
    /// <see cref="System.Windows.Controls.GridViewRowPresenter"/> reserves after the last column.
    /// Counting ten left the rows two pixels wider than the space computed for them.
    /// </summary>
    public const double FileListRowChromeWidth = 12;

    /// <summary>The narrowest the name column is stretched down to before the list scrolls sideways.</summary>
    public const double MinimumNameColumnWidth = 200;

    /// <summary>
    /// The width that makes the name column fill a file list, so the rows fit its viewport and the
    /// list scrolls sideways only when even the narrowest name column does not fit.
    /// </summary>
    /// <param name="listWidth">The rendered width of the list.</param>
    /// <param name="otherColumnsWidth">The sum of the widths of every column but the name column.</param>
    /// <param name="verticalScrollBarWidth">The width reserved for the vertical scrollbar.</param>
    /// <returns>The name column width, never under <see cref="MinimumNameColumnWidth"/>.</returns>
    public static double FitNameColumnWidth(double listWidth, double otherColumnsWidth, double verticalScrollBarWidth) =>
        Math.Max(
            MinimumNameColumnWidth,
            listWidth - otherColumnsWidth - verticalScrollBarWidth - FileListRowChromeWidth);

    /// <summary>The glyph of a folder, shown on a bookmark.</summary>
    public const string FolderGlyph = "";
}
