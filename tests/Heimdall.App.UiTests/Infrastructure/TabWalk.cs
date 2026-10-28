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
using System.Windows.Media;

namespace Heimdall.App.UiTests.Infrastructure;

/// <summary>
/// Performs the Tab gesture on a shown window and reports where the keyboard focus went.
/// </summary>
/// <remarks>
/// The focus is moved with <see cref="TraversalRequest"/> and <see cref="FocusNavigationDirection.Next"/>,
/// which is the request the Tab key makes, so the walk follows every rule the key follows: document
/// order, <c>TabIndex</c>, <c>TabNavigation</c> and the radio group stops. Reading the markup for its
/// <c>TabIndex</c> values instead would pass on a layout whose order is wrong for another reason.
/// </remarks>
internal static class TabWalk
{
    private const int MaxSteps = 400;

    /// <summary>
    /// Moves the focus forward from the first element of <paramref name="window"/> and returns, in the order
    /// reached, the visible controls of <paramref name="root"/> the walk landed on.
    /// </summary>
    public static List<FrameworkElement> Run(Window window, FrameworkElement root, int? limit = null)
    {
        List<FrameworkElement> reached = [];
        HashSet<FrameworkElement> seen = [];
        window.Activate();
        window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        int steps = limit ?? MaxSteps;

        for (int step = 0; step < steps; step++)
        {
            if (Keyboard.FocusedElement is not FrameworkElement focused)
            {
                break;
            }

            if (seen.Contains(focused))
            {
                break;
            }

            seen.Add(focused);
            if (IsInside(focused, root))
            {
                reached.Add(focused);
            }

            focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        return reached;
    }

    /// <summary>
    /// Returns <paramref name="controls"/> in reading order: row by row from the top, left to right inside a
    /// row. Two controls share a row when their vertical centres are closer than half the shorter of them.
    /// </summary>
    public static List<FrameworkElement> InReadingOrder(IEnumerable<FrameworkElement> controls, Visual space)
    {
        var placed = controls
            .Select(c =>
            {
                Rect box = c.TransformToVisual(space).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight));
                return (Control: c, Box: box, Centre: box.Top + (box.Height / 2));
            })
            .OrderBy(p => p.Centre)
            .ToList();

        List<List<(FrameworkElement Control, Rect Box, double Centre)>> rows = [];
        foreach (var item in placed)
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(item.Centre - row[0].Centre) < Math.Min(item.Box.Height, row[0].Box.Height) / 2)
            {
                row.Add(item);
            }
            else
            {
                rows.Add([item]);
            }
        }

        return rows.SelectMany(row => row.OrderBy(p => p.Box.Left)).Select(p => p.Control).ToList();
    }

    /// <summary>The name a failure message shows for a control.</summary>
    public static string NameOf(FrameworkElement element)
        => string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name;

    private static bool IsInside(FrameworkElement element, FrameworkElement root)
    {
        DependencyObject? node = element;
        while (node is not null)
        {
            if (ReferenceEquals(node, root))
            {
                return element.IsVisible && element.ActualHeight > 0;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}
