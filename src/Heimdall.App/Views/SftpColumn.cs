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

namespace Heimdall.App.Views;

/// <summary>
/// Names the sort key of a file-list column, so a column is told apart by what it is and not by
/// where it sits.
/// </summary>
/// <remarks>
/// The sort handler used to map a header to its key by the column's index, and the width handler
/// assumed column zero was the name. Dragging a column to another place made a click on its header
/// sort another column, with the arrow drawn on the wrong one. The key travels with the column now.
/// </remarks>
public static class SftpColumn
{
    /// <summary>Identifies the attached <c>Key</c> property.</summary>
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(SftpColumn),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary>Gets the sort key of a column.</summary>
    public static string GetKey(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string)element.GetValue(KeyProperty);
    }

    /// <summary>Sets the sort key of a column.</summary>
    public static void SetKey(DependencyObject element, string value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(KeyProperty, value);
    }
}
