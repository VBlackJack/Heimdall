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

using System.Globalization;
using System.Windows.Controls;
using Heimdall.App.Services;
using WpfDataGrid = System.Windows.Controls.DataGrid;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Makes Ctrl+C on a table of a tool copy what the copy button copies: every header and cell neutralized
/// for a spreadsheet, through the one rule of <see cref="TabularOutput.SanitizeCell"/>.
/// </summary>
internal static class TabularGridCopy
{
    /// <summary>Puts the rule on the grid's own copy.</summary>
    /// <param name="grid">A grid that shows the table of a tool.</param>
    public static void Attach(WpfDataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.CopyingRowClipboardContent += (_, e) => Sanitize(e.ClipboardRowContent);
    }

    /// <summary>Rewrites the cells of one copied row (the header row included) with the rule.</summary>
    /// <param name="row">The cells the grid is about to put on the clipboard.</param>
    internal static void Sanitize(IList<DataGridClipboardCellContent> row)
    {
        ArgumentNullException.ThrowIfNull(row);

        for (int index = 0; index < row.Count; index++)
        {
            DataGridClipboardCellContent cell = row[index];
            string text = cell.Content is null ? string.Empty : Convert.ToString(cell.Content, CultureInfo.CurrentCulture) ?? string.Empty;
            row[index] = new DataGridClipboardCellContent(cell.Item, cell.Column, TabularOutput.SanitizeCell(text));
        }
    }
}
