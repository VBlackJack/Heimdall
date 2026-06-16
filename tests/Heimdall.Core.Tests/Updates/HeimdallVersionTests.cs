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

using Heimdall.Core.Updates;

namespace Heimdall.Core.Tests;

public sealed class HeimdallVersionTests
{
    [Theory]
    [InlineData("2026.061501", "2026.061501")]
    [InlineData("2026.061501+abc123", "2026.061501")]
    [InlineData("v2026.061501", "2026.061501")]
    [InlineData("v2026.061501+abc123", "2026.061501")]
    public void TryParse_AcceptsValidForms(string input, string expectedCleaned)
    {
        Assert.True(HeimdallVersion.TryParse(input, out var version));
        Assert.Equal(expectedCleaned, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("+abc")]
    [InlineData("2026.")]
    [InlineData("2026.x")]
    [InlineData("2026..1")]
    [InlineData("-1.0")]
    [InlineData("not-a-version")]
    public void TryParse_RejectsMalformedInput(string? input)
    {
        Assert.False(HeimdallVersion.TryParse(input, out _));
    }

    [Fact]
    public void Parse_MalformedInput_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => HeimdallVersion.Parse("nope"));
    }

    [Fact]
    public void ToString_RoundTripsZeroPaddedComponent()
    {
        var version = HeimdallVersion.Parse("2026.061501");
        Assert.Equal("2026.061501", version.ToString());
    }

    [Fact]
    public void Comparison_EqualVersions()
    {
        var a = HeimdallVersion.Parse("2026.061501");
        var b = HeimdallVersion.Parse("2026.061501");
        Assert.Equal(0, a.CompareTo(b));
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a <= b);
        Assert.True(a >= b);
    }

    [Fact]
    public void Comparison_NewerIsGreater()
    {
        var older = HeimdallVersion.Parse("2026.061501");
        var newer = HeimdallVersion.Parse("2026.061502");
        Assert.True(newer > older);
        Assert.True(older < newer);
        Assert.True(newer.CompareTo(older) > 0);
    }

    [Fact]
    public void Comparison_AcrossFirstComponent()
    {
        var older = HeimdallVersion.Parse("2025.123199");
        var newer = HeimdallVersion.Parse("2026.010100");
        Assert.True(newer > older);
    }

    [Fact]
    public void Equality_IsNumericNotStringBased()
    {
        // Same numeric components, different textual zero-padding on the second segment.
        var padded = HeimdallVersion.Parse("2026.061501");
        var unpadded = HeimdallVersion.Parse("2026.61501");

        Assert.Equal(padded, unpadded);
        Assert.True(padded == unpadded);
        Assert.Equal(padded.GetHashCode(), unpadded.GetHashCode());

        // ToString still preserves each instance's own cleaned form.
        Assert.Equal("2026.061501", padded.ToString());
        Assert.Equal("2026.61501", unpadded.ToString());
    }
}
