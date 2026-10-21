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
using System.Windows.Data;
using TextBox = System.Windows.Controls.TextBox;

namespace Heimdall.App.Controls;

/// <summary>
/// What a section of the settings rail reports about the fields it holds, and where it sits.
/// </summary>
/// <remarks>
/// <para>The settings had six top tabs with an error badge each, and a rail of sections inside each
/// tab, so the badge said which tab to open and not which section. There is one rail now: every
/// section is listed under the name of its group, and the rail item shows the count of its own
/// fields in error, read with the same predicate the field tooltips use.</para>
/// <para>A section names its group, and so does the group heading above it; a markup guard checks
/// that the two agree, and the content footer of a group is shown from the open section's group.</para>
/// </remarks>
public static class SettingsRail
{
    /// <summary>The settings group a rail section or group heading belongs to.</summary>
    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
        "Group",
        typeof(string),
        typeof(SettingsRail),
        new FrameworkPropertyMetadata(null));

    /// <summary>What the rail shows under the open section, such as the reset of a group's defaults.</summary>
    public static readonly DependencyProperty ContentFooterProperty = DependencyProperty.RegisterAttached(
        "ContentFooter",
        typeof(object),
        typeof(SettingsRail),
        new FrameworkPropertyMetadata(null));

    /// <summary>What the rail shows at its foot, under the list of sections: the legend of the apply glyphs.</summary>
    public static readonly DependencyProperty FooterProperty = DependencyProperty.RegisterAttached(
        "Footer",
        typeof(object),
        typeof(SettingsRail),
        new FrameworkPropertyMetadata(null));

    /// <summary>Reads what the rail shows at its foot.</summary>
    /// <param name="element">The rail.</param>
    public static object? GetFooter(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(FooterProperty);
    }

    /// <summary>Sets what the rail shows at its foot.</summary>
    /// <param name="element">The rail.</param>
    /// <param name="value">The footer content.</param>
    public static void SetFooter(DependencyObject element, object? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(FooterProperty, value);
    }

    /// <summary>Reads the group of a rail item.</summary>
    /// <param name="element">The section's or the group heading's tab item.</param>
    public static string? GetGroup(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(GroupProperty);
    }

    /// <summary>Sets the group of a rail item.</summary>
    /// <param name="element">The section's or the group heading's tab item.</param>
    /// <param name="value">The group name, the suffix of the heading's name.</param>
    public static void SetGroup(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(GroupProperty, value);
    }

    /// <summary>Reads what the rail shows under the open section.</summary>
    /// <param name="element">The rail.</param>
    public static object? GetContentFooter(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ContentFooterProperty);
    }

    /// <summary>Sets what the rail shows under the open section.</summary>
    /// <param name="element">The rail.</param>
    /// <param name="value">The footer content.</param>
    public static void SetContentFooter(DependencyObject element, object? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ContentFooterProperty, value);
    }

    /// <summary>How many fields of the section are in error; the rail item shows it as a badge.</summary>
    public static readonly DependencyProperty ErrorCountProperty = DependencyProperty.RegisterAttached(
        "ErrorCount",
        typeof(int),
        typeof(SettingsRail),
        new FrameworkPropertyMetadata(0));

    /// <summary>Reads the error count of a rail section.</summary>
    /// <param name="element">The section's tab item.</param>
    public static int GetErrorCount(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (int)element.GetValue(ErrorCountProperty);
    }

    /// <summary>Sets the error count of a rail section.</summary>
    /// <param name="element">The section's tab item.</param>
    /// <param name="value">The number of its fields in error.</param>
    public static void SetErrorCount(DependencyObject element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ErrorCountProperty, value);
    }

    /// <summary>
    /// Counts the settings fields in error under <paramref name="root"/>, walking the logical tree
    /// so a section that is not open is counted too.
    /// </summary>
    /// <param name="root">The section content.</param>
    /// <param name="fieldHasError">Whether the settings property a box binds, without its prefix, is in error.</param>
    /// <param name="pathPrefix">The binding path prefix every settings box uses.</param>
    internal static int CountFieldErrors(DependencyObject root, Func<string, bool> fieldHasError, string pathPrefix)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(fieldHasError);

        int count = 0;
        if (root is TextBox box
            && BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.ParentBinding.Path?.Path is { } path
            && path.StartsWith(pathPrefix, StringComparison.Ordinal)
            && fieldHasError(path[pathPrefix.Length..]))
        {
            count++;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject node)
            {
                count += CountFieldErrors(node, fieldHasError, pathPrefix);
            }
        }

        return count;
    }
}
