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
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace Heimdall.App.Controls;

/// <summary>
/// Lays out a name and the marks that trail it on one line, giving the name the width first.
/// </summary>
/// <remarks>
/// <para>The first child is the name, the second the trailing marks (badges, lock, favorite). The
/// name is measured against the whole width and keeps what it needs; the marks get what is left,
/// right-aligned, and ellipsize inside it. When less than <see cref="MinTrailingWidth"/> is left,
/// the marks are not shown at all: a badge cut down to a sliver says nothing, and each one carries
/// its full text in a tooltip.</para>
/// <para>A grid with the name in a star column and the marks in an auto column did the opposite:
/// the auto column took its full width first, and in a narrow sidebar a long "via user@gateway"
/// badge left the name no width at all.</para>
/// </remarks>
public sealed class NameFirstPanel : Panel
{
    /// <summary>The narrowest the trailing marks are shown at; below it they are hidden.</summary>
    public static readonly DependencyProperty MinTrailingWidthProperty = DependencyProperty.Register(
        nameof(MinTrailingWidth),
        typeof(double),
        typeof(NameFirstPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <inheritdoc cref="MinTrailingWidthProperty"/>
    public double MinTrailingWidth
    {
        get => (double)GetValue(MinTrailingWidthProperty);
        set => SetValue(MinTrailingWidthProperty, value);
    }

    /// <summary>Whether the last layout left the trailing marks enough width to be shown.</summary>
    public bool IsTrailingShown { get; private set; } = true;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        UIElement? name = Child(0);
        UIElement? trailing = Child(1);

        name?.Measure(availableSize);
        double nameWidth = Math.Min(name?.DesiredSize.Width ?? 0, availableSize.Width);
        double remaining = Math.Max(0, availableSize.Width - nameWidth);

        double trailingWidth = 0;
        double trailingHeight = 0;
        if (trailing is not null)
        {
            bool shown = double.IsPositiveInfinity(availableSize.Width) || remaining >= MinTrailingWidth;
            trailing.Measure(new Size(shown ? remaining : 0, availableSize.Height));
            trailingWidth = shown ? Math.Min(trailing.DesiredSize.Width, remaining) : 0;
            trailingHeight = shown ? trailing.DesiredSize.Height : 0;
        }

        return new Size(
            nameWidth + trailingWidth,
            Math.Max(name?.DesiredSize.Height ?? 0, trailingHeight));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        UIElement? name = Child(0);
        UIElement? trailing = Child(1);

        double nameWidth = Math.Min(name?.DesiredSize.Width ?? 0, finalSize.Width);
        name?.Arrange(new Rect(0, 0, nameWidth, finalSize.Height));

        double remaining = Math.Max(0, finalSize.Width - nameWidth);
        IsTrailingShown = remaining >= MinTrailingWidth;
        if (trailing is not null)
        {
            double trailingWidth = IsTrailingShown ? Math.Min(trailing.DesiredSize.Width, remaining) : 0;
            trailing.Arrange(new Rect(finalSize.Width - trailingWidth, 0, trailingWidth, finalSize.Height));
        }

        return finalSize;
    }

    private UIElement? Child(int index) =>
        InternalChildren.Count > index ? InternalChildren[index] : null;
}
