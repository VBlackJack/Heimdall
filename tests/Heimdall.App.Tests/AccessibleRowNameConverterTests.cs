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
using Heimdall.App.Converters;

namespace Heimdall.App.Tests;

public sealed class AccessibleRowNameConverterTests
{
    private static readonly AccessibleRowNameConverter Converter = new();

    private static object Convert(params object?[] values) =>
        Converter.Convert(values!, typeof(string), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_FillsTheFormatSlotsInOrder()
    {
        Assert.Equal(
            "Port 22, service OpenSSH",
            Convert("Port {0}, service {1}", 22, "OpenSSH"));
    }

    /// <summary>
    /// A container being recycled hands its bindings <see cref="DependencyProperty.UnsetValue" />.
    /// Left alone it renders as "{DependencyProperty.UnsetValue}" in front of a screen reader.
    /// </summary>
    [Fact]
    public void Convert_UnsetValue_BecomesEmpty_NotItsToString()
    {
        string name = (string)Convert("Host {0}, name {1}", "10.0.0.5", DependencyProperty.UnsetValue);

        Assert.DoesNotContain("UnsetValue", name, StringComparison.Ordinal);
        Assert.Equal("Host 10.0.0.5, name ", name);
    }

    [Fact]
    public void Convert_NullValue_BecomesEmpty()
    {
        Assert.Equal("Host 10.0.0.5, name ", Convert("Host {0}, name {1}", "10.0.0.5", null));
    }

    /// <summary>
    /// A key that is missing from the locale file resolves to nothing. The row must still be
    /// identifiable: announcing the values without their labels is poor, announcing the
    /// view-model type name - which is what no name at all produces - is the defect.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Convert_NoFormat_StillNamesTheRowFromItsValues(string? format)
    {
        Assert.Equal("22, OpenSSH", Convert(format, 22, "OpenSSH"));
    }

    /// <summary>
    /// A format asking for more slots than the XAML supplies would throw inside a binding, where
    /// the exception is swallowed and the row silently goes back to announcing its type.
    /// </summary>
    [Fact]
    public void Convert_FormatWantsMoreSlotsThanSupplied_FallsBackInsteadOfThrowing()
    {
        Assert.Equal("22, OpenSSH", Convert("Port {0}, service {1}, banner {2}", 22, "OpenSSH"));
    }

    [Fact]
    public void Convert_NoValuesAtAll_IsEmpty()
    {
        Assert.Equal(string.Empty, Convert());
        Assert.Equal(
            string.Empty,
            Converter.Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The fallback drops empty slots rather than emitting ", , ".
    /// </summary>
    [Fact]
    public void Convert_NoFormat_SkipsEmptyValues()
    {
        Assert.Equal("22", Convert(null, 22, null, string.Empty));
    }

    [Fact]
    public void ConvertBack_IsInert()
    {
        object[] back = Converter.ConvertBack(
            "anything", [typeof(string), typeof(string)], null!, CultureInfo.InvariantCulture);

        Assert.All(back, v => Assert.Same(Binding.DoNothing, v));
    }
}
