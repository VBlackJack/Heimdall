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
using System.Windows.Media;
using Heimdall.App.Localization;
using Heimdall.Core.Discovery;

namespace Heimdall.App.Converters;

/// <summary>
/// Maps <see cref="SmbFindingSeverity"/> values to themed brushes.
/// </summary>
public sealed class SmbSeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SmbFindingSeverity severity)
        {
            return DependencyProperty.UnsetValue;
        }

        var resourceKey = severity switch
        {
            SmbFindingSeverity.Success => "SuccessTextBrush",
            SmbFindingSeverity.Warning => "WarningTextBrush",
            SmbFindingSeverity.Critical => "ErrorTextBrush",
            _ => "InfoTextBrush",
        };

        return Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps <see cref="SmbFindingSeverity"/> values to Segoe MDL2 icons.
/// </summary>
public sealed class SmbSeverityToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SmbFindingSeverity severity)
        {
            return string.Empty;
        }

        return severity switch
        {
            SmbFindingSeverity.Success => "\uE73E",
            SmbFindingSeverity.Warning => "\uE7BA",
            SmbFindingSeverity.Critical => "\uE730",
            _ => "\uE946",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Localizes nullable booleans using SMB yes/no keys.
/// </summary>
public sealed class BoolToYesNoConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            true => "ToolSmbYes",
            false => "ToolSmbNo",
            _ => "ToolSmbNotAvailable",
        };

        return LocalizationSource.Instance[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
