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
using Heimdall.App.Converters;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the height a bottom panel of the Command Library is allowed to take.
/// </summary>
public sealed class RemainingHeightConverterTests
{
    private const double Reserved = 128;
    private const double Cap = 450;

    [Theory]
    [InlineData(1000, Cap)]          // room to spare: the panel keeps its own cap
    [InlineData(390, 390 - Reserved)] // short body: the panel gives way to the list
    [InlineData(100, 0)]             // body smaller than the reserve: never negative
    public void ThePanelGetsWhatTheBodyLeavesUpToItsCap(double available, double expected)
    {
        Assert.Equal(expected, Convert(available, Reserved, Cap));
    }

    /// <summary>
    /// Before the first layout pass the body measures zero. Answering zero would collapse the
    /// panel for one frame and open it on the next.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void AnUnmeasuredBodyLeavesThePanelAtItsCap(double available)
    {
        Assert.Equal(Cap, Convert(available, Reserved, Cap));
    }

    /// <summary>
    /// Once the operator has dragged the panel, the cap stops standing in for that choice: the
    /// panel may grow past it, still never into the list's reserve.
    /// </summary>
    [Theory]
    [InlineData(1000, 700, 1000 - Reserved)]
    [InlineData(390, 200, 390 - Reserved)]
    public void AHandChosenHeightLiftsTheCapButNotTheReserve(double available, double chosen, double expected)
    {
        object result = new RemainingHeightConverter().Convert(
            [available, Reserved, Cap, chosen], typeof(double), null!, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    /// <summary>Positive control: an automatic height (NaN) keeps the cap.</summary>
    [Fact]
    public void AnAutomaticHeightKeepsTheCap()
    {
        object result = new RemainingHeightConverter().Convert(
            [1000d, Reserved, Cap, double.NaN], typeof(double), null!, CultureInfo.InvariantCulture);

        Assert.Equal(Cap, result);
    }

    [Fact]
    public void AnUnresolvedBindingImposesNoLimit()
    {
        var converter = new RemainingHeightConverter();

        object result = converter.Convert(
            [System.Windows.DependencyProperty.UnsetValue, Reserved, System.Windows.DependencyProperty.UnsetValue],
            typeof(double), null!, CultureInfo.InvariantCulture);

        Assert.Equal(double.PositiveInfinity, result);
    }

    private static double Convert(double available, double reserved, double cap)
        => (double)new RemainingHeightConverter().Convert(
            [available, reserved, cap], typeof(double), null!, CultureInfo.InvariantCulture);
}
