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
using System.Windows;
using System.Windows.Data;
using Heimdall.App.ViewModels;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Heimdall.App.Converters;

/// <summary>
/// Turns the error a settings box holds into the localized sentence its tooltip and its accessible
/// help text carry.
/// </summary>
/// <remarks>
/// <para>The error a box holds is a token, not a sentence: a ranged field reports the settings
/// property that bounds it, and a number field reports one English sentence the view model maps to
/// a key. Shown raw, the tooltip would read "MaxEmbeddedSessions". The view model owns that mapping
/// already, for the banner, so the box asks it rather than keeping a second copy.</para>
/// <para>Values: the error content (bound only so the conversion re-runs when the error changes),
/// the settings view model, and the box itself, whose Text binding names the property.</para>
/// </remarks>
public sealed class SettingsFieldErrorConverter : IMultiValueConverter
{
    /// <summary>The binding path prefix every settings box uses.</summary>
    internal const string SettingsPathPrefix = "Settings.";

    /// <inheritdoc />
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: >= 3 })
        {
            return null;
        }

        object? content = values[0] == DependencyProperty.UnsetValue ? null : values[0];
        if (values[1] is SettingsViewModel settings
            && values[2] is WpfTextBox box
            && BindingOperations.GetBindingExpression(box, WpfTextBox.TextProperty)?.ParentBinding.Path?.Path is { } path
            && path.StartsWith(SettingsPathPrefix, StringComparison.Ordinal)
            && settings.DescribeFieldError(path[SettingsPathPrefix.Length..]) is { } message)
        {
            return message;
        }

        return content?.ToString();
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
