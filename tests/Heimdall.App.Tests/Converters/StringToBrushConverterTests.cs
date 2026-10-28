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
using System.Windows.Media;
using Heimdall.App.Converters;

namespace Heimdall.App.Tests.Converters;

public sealed class StringToBrushConverterTests
{
    private const string Red = "#FF0000";

    private static object Convert(StringToBrushConverter converter, object? value) =>
        converter.Convert(value, typeof(Brush), null, CultureInfo.InvariantCulture);

    [Fact]
    public void TheSameColour_ReturnsTheSameFrozenBrush()
    {
        StringToBrushConverter converter = new();

        SolidColorBrush first = Assert.IsType<SolidColorBrush>(Convert(converter, Red));
        SolidColorBrush second = Assert.IsType<SolidColorBrush>(Convert(converter, Red));

        Assert.Same(first, second);
        Assert.True(first.IsFrozen);
        Assert.Equal(Colors.Red, first.Color);
    }

    [Fact]
    public void TwoColours_AreTwoBrushes()
    {
        StringToBrushConverter converter = new();

        object red = Convert(converter, Red);
        object blue = Convert(converter, "#0000FF");

        Assert.NotSame(red, blue);
        Assert.Equal(Colors.Blue, ((SolidColorBrush)blue).Color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a colour")]
    public void ANonColour_StillYieldsNoValue(string? input)
    {
        Assert.Same(DependencyProperty.UnsetValue, Convert(new StringToBrushConverter(), input));
    }

    [Fact]
    public void AStreamOfDistinctColours_DoesNotGrowTheCacheWithoutLimit()
    {
        StringToBrushConverter converter = new();
        int distinct = StringToBrushConverter.MaxCachedBrushes + 40;

        for (int i = 0; i < distinct; i++)
        {
            string colour = string.Create(CultureInfo.InvariantCulture, $"#FF{i:X6}");
            SolidColorBrush brush = Assert.IsType<SolidColorBrush>(Convert(converter, colour));
            Assert.True(brush.IsFrozen);
        }

        Assert.Equal(StringToBrushConverter.MaxCachedBrushes, converter.CachedBrushCount);
    }
}
