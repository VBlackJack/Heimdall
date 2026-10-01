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
using System.Windows.Input;
using ListView = System.Windows.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace Heimdall.App.Views;

/// <summary>
/// Turns a press-and-move on a row of a file list into the start of a drag that carries the
/// selected rows, for the remote and the local browser alike.
/// </summary>
/// <remarks>
/// <para>
/// The press has to be handled with care. WPF collapses a multi-selection to the pressed row the
/// moment the button goes down, so a drag started on one row of several would carry that row
/// alone. A press on a row that is part of a multi-selection is therefore held back: the selection
/// is kept while the pointer stays still, collapsed to the row on release (a click), and left alone
/// when the pointer moves past the system's drag threshold (a drag).
/// </para>
/// <para>
/// The helper only decides when a drag starts and what it carries. What the drag is made of, and
/// where it may land, belong to the view that owns the list.
/// </para>
/// </remarks>
internal sealed class FileListDragSource
{
    private readonly ListView _list;
    private readonly Func<Point, object?> _rowAt;
    private readonly Func<bool> _canStart;
    private readonly Action<IReadOnlyList<object>> _start;

    private Point? _origin;
    private object? _collapseSelectionTo;
    private bool _dragging;

    /// <summary>Initializes a new drag source over a list.</summary>
    /// <param name="list">The list the rows are in.</param>
    /// <param name="rowAt">The row under a point in the list's own coordinates, or <see langword="null"/> when there is none.</param>
    /// <param name="canStart">Whether a drag may start now (the session is live, no overlay covers the list).</param>
    /// <param name="start">Starts the drag with the selected rows; runs the drag loop and returns when it ends.</param>
    public FileListDragSource(
        ListView list,
        Func<Point, object?> rowAt,
        Func<bool> canStart,
        Action<IReadOnlyList<object>> start)
    {
        _list = list ?? throw new ArgumentNullException(nameof(list));
        _rowAt = rowAt ?? throw new ArgumentNullException(nameof(rowAt));
        _canStart = canStart ?? throw new ArgumentNullException(nameof(canStart));
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    /// <summary>Whether the pointer must move this far, in either direction, before a press becomes a drag.</summary>
    internal static bool PassesDragThreshold(Point origin, Point position, double horizontal, double vertical)
        => Math.Abs(position.X - origin.X) >= horizontal || Math.Abs(position.Y - origin.Y) >= vertical;

    /// <summary>Whether a press on a row must be held back so a multi-selection can be dragged.</summary>
    internal static bool MustHoldSelection(ModifierKeys modifiers, int selectedCount, bool rowIsSelected)
        => modifiers == ModifierKeys.None && selectedCount > 1 && rowIsSelected;

    /// <summary>Attach to the list's <c>PreviewMouseLeftButtonDown</c>.</summary>
    public void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        _origin = null;
        _collapseSelectionTo = null;
        if (!_canStart() || _rowAt(e.GetPosition(_list)) is not { } row)
        {
            return;
        }

        _origin = e.GetPosition(null);
        if (MustHoldSelection(Keyboard.Modifiers, _list.SelectedItems.Count, _list.SelectedItems.Contains(row)))
        {
            _collapseSelectionTo = row;
            e.Handled = true;
            (_list.ItemContainerGenerator.ContainerFromItem(row) as ListViewItem)?.Focus();
        }
    }

    /// <summary>Attach to the list's <c>PreviewMouseLeftButtonUp</c>.</summary>
    public void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_collapseSelectionTo is { } row)
        {
            _list.SelectedItems.Clear();
            _list.SelectedItem = row;
        }

        _collapseSelectionTo = null;
        _origin = null;
    }

    /// <summary>Attach to the list's <c>PreviewMouseMove</c>.</summary>
    public void OnPreviewMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_origin is not { } origin || e.LeftButton != MouseButtonState.Pressed || _dragging)
        {
            return;
        }

        if (!PassesDragThreshold(
            origin,
            e.GetPosition(null),
            SystemParameters.MinimumHorizontalDragDistance,
            SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        _origin = null;
        _collapseSelectionTo = null;
        List<object> rows = [.. _list.SelectedItems.Cast<object>()];
        if (rows.Count == 0 || !_canStart())
        {
            return;
        }

        _dragging = true;
        try
        {
            _start(rows);
        }
        finally
        {
            _dragging = false;
        }
    }
}
