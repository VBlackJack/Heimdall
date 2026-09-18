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

using System;
using System.Globalization;
using System.Windows.Data;

namespace Heimdall.App.Converters;

/// <summary>
/// Answers whether a panel is too narrow to be read in two columns, so a layout can fall back to
/// one without anything being hidden.
/// </summary>
/// <remarks>
/// <para>WPF has no way to ask "how wide is the box I am in" from a style, so the width is bound
/// through this converter instead. It is the panel's own width that decides, not the window's: a
/// tool can be opened into a split pane a third of the screen wide, and what matters is the room
/// the tool actually got.</para>
/// <para>The default threshold is the sum of what the two columns need at their narrowest, and it
/// can be overridden per binding with a converter parameter so that a panel with different
/// contents does not have to accept this one.</para>
/// </remarks>
public sealed class NarrowPaneConverter : IValueConverter
{
    /// <summary>
    /// The width under which two columns stop being worth it.
    /// </summary>
    /// <remarks>
    /// It is the sum of what the two columns take when both are satisfied: 560 for the controls,
    /// which is the width of their widest row, 380 for the readouts, and 24 of gutter. A smaller
    /// threshold was tried and measured at a 1120 pixel window: the readouts, being an Auto
    /// column, were served first and the controls were left with 350 pixels, sliders reduced to
    /// stubs and a row running off the edge. Two columns are worth having only when both of them
    /// are.
    /// </remarks>
    public const double DefaultTwoColumnWidth = 980;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double threshold = DefaultTwoColumnWidth;

        if (parameter is not null
            && double.TryParse(
                parameter.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double given)
            && given > 0)
        {
            threshold = given;
        }

        if (value is not double width || double.IsNaN(width))
        {
            // A panel that has not been measured yet is not known to be narrow. Answering "narrow"
            // here would lay the page out in one column for the first frame and shuffle it on the
            // second, which reads as a flicker on every open.
            return false;
        }

        return width < threshold;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
