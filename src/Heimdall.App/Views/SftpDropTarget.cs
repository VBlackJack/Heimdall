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
/// Marks the row of a file list that a drag would land in, so the row can be drawn as the target
/// before the button is released.
/// </summary>
/// <remarks>
/// A drop onto a folder row sends into that folder and a drop anywhere else sends into the current
/// one, and nothing said so until the button was released. The row style reads this property to
/// paint the folder that will receive the drop.
/// </remarks>
public static class SftpDropTarget
{
    /// <summary>Identifies the attached <c>IsDropTarget</c> property.</summary>
    public static readonly DependencyProperty IsDropTargetProperty =
        DependencyProperty.RegisterAttached(
            "IsDropTarget",
            typeof(bool),
            typeof(SftpDropTarget),
            new FrameworkPropertyMetadata(false));

    /// <summary>Gets whether the row is the target of the drag in progress.</summary>
    public static bool GetIsDropTarget(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsDropTargetProperty);
    }

    /// <summary>Sets whether the row is the target of the drag in progress.</summary>
    public static void SetIsDropTarget(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsDropTargetProperty, value);
    }
}
