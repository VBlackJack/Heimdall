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

using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>
/// The bounds of a <c>number</c> are compared exactly, as decimals, because the program receives the text as typed:
/// two texts that are the same double are not the same value.
/// </summary>
public sealed class RecipeExactDecimalTests
{
    private static RecipeInput Number(string? min, string? max)
        => new("r", RecipeInputType.Number, "R", false, null, null, null, null, null) { Min = min, Max = max };

    private static bool Accepts(RecipeInput input, string value)
        => RecipeValueRule.Check(input, value, placeholderStartsToken: false, FakeCharacterConverter.Plain) == RecipeValueRefusal.None;

    [Fact]
    public void AValueJustAboveALargeMaximumIsRefused()
    {
        RecipeInput input = Number(null, "9007199254740992");

        Assert.True(Accepts(input, "9007199254740992"));
        Assert.False(Accepts(input, "9007199254740993"));
    }

    [Fact]
    public void AValueJustAboveAMaximumByAFarDecimalIsRefused()
    {
        RecipeInput input = Number(null, "1");

        Assert.True(Accepts(input, "1.0"));
        Assert.True(Accepts(input, "1.00000000000000000000"));
        Assert.False(Accepts(input, "1.0000000000000001"));
    }

    [Fact]
    public void AValueJustBelowAMinimumByAFarDecimalIsRefused()
    {
        RecipeInput input = Number("0.1", null);

        Assert.True(Accepts(input, "0.10000000000000001"));
        Assert.False(Accepts(input, "0.0999999999999999999"));
    }

    [Theory]
    [InlineData("1", "1.0", 0)]
    [InlineData("1.50", "1.5", 0)]
    [InlineData("-0", "0", 0)]
    [InlineData("-0.0", "0", 0)]
    [InlineData("0", "-0.000", 0)]
    [InlineData("2", "10", -1)]
    [InlineData("10", "2", 1)]
    [InlineData("0.9", "0.10", 1)]
    [InlineData("0.123456789012345678901234567890", "0.123456789012345678901234567891", -1)]
    [InlineData("123456789012345678901234567890.5", "123456789012345678901234567890.50", 0)]
    [InlineData("-1", "1", -1)]
    [InlineData("-2", "-10", 1)]
    [InlineData("-10", "-2", -1)]
    [InlineData("-1.5", "-1.50001", 1)]
    [InlineData("-0.5", "0", -1)]
    [InlineData("0.0000001", "0", 1)]
    [InlineData("-0.0000001", "0", -1)]
    public void TwoDecimalsAreComparedByTheirDigits(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(RecipeNumbers.CompareDecimal(left, right)));
        Assert.Equal(-expected, Math.Sign(RecipeNumbers.CompareDecimal(right, left)));
    }

    [Fact]
    public void NegativeBoundsAreCompared()
    {
        RecipeInput input = Number("-2.5", "-0.5");

        Assert.True(Accepts(input, "-2.5"));
        Assert.True(Accepts(input, "-1"));
        Assert.True(Accepts(input, "-0.50"));
        Assert.False(Accepts(input, "-2.50001"));
        Assert.False(Accepts(input, "-0.4"));
        Assert.False(Accepts(input, "0"));
        Assert.False(Accepts(input, "-0"));
    }

    [Fact]
    public void AZeroWithAMinusSignIsNotAValueAndNotABound()
    {
        Assert.False(Accepts(Number(null, null), "-0"));
        Assert.False(Accepts(Number(null, null), "-0.000"));
        Assert.True(Accepts(Number("0", "0"), "0.00"));
        Assert.False(Accepts(Number("0", null), "-0.0000000000000000001"));

        // A bound that is not valid is reported by the validator, and ignored by the value rule.
        Assert.Contains(RecipeErrorCode.InvalidBound, RecipeTestData.Codes(RecipeTestData.Using(Number("-0", null))));
        Assert.Contains(RecipeErrorCode.InvalidBound, RecipeTestData.Codes(RecipeTestData.Using(Number(null, "-0.0"))));
    }

    [Fact]
    public void AVeryLongFractionKeepsItsLastDigit()
    {
        string max = "0." + new string('0', 40) + "1";
        RecipeInput input = Number(null, max);

        Assert.True(Accepts(input, "0." + new string('0', 40) + "10"));
        Assert.False(Accepts(input, "0." + new string('0', 40) + "11"));
    }

    [Fact]
    public void AMinimumAboveAMaximumIsRefusedExactly()
    {
        RecipeInput input = new("r", RecipeInputType.Number, "R", false, null, null, null, null, null) { Min = "1.0000000000000001", Max = "1" };
        Recipe recipe = RecipeTestData.Using(input);

        Assert.Contains(RecipeErrorCode.MinAboveMax, RecipeTestData.Codes(recipe));
        Assert.DoesNotContain(RecipeErrorCode.MinAboveMax, RecipeTestData.Codes(RecipeTestData.Using(input with { Min = "1.0", Max = "1" })));
    }

    [Fact]
    public void ADefaultJustAboveTheMaximumIsRefusedAtImport()
    {
        RecipeInput input = new("r", RecipeInputType.Number, "R", false, "1.0000000000000001", null, null, null, null) { Max = "1" };

        Assert.Contains(RecipeErrorCode.DefaultOutOfBounds, RecipeTestData.Codes(RecipeTestData.Using(input)));
    }

    [Fact]
    public void AnIntKeepsItsInt32Comparison()
    {
        RecipeInput input = new("n", RecipeInputType.Int, "N", false, null, null, null, null, null) { Min = "-2147483648", Max = "2147483647" };

        Assert.True(Accepts(input, "-2147483648"));
        Assert.True(Accepts(input, "2147483647"));
        Assert.False(Accepts(input, "2147483648"));
    }
}
