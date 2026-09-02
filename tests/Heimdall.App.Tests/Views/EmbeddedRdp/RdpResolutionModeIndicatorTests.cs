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

using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

public sealed class RdpResolutionModeIndicatorTests
{
    [Theory]
    [InlineData(RdpResolutionMode.Auto)]
    [InlineData(RdpResolutionMode.FitWindow)]
    [InlineData(RdpResolutionMode.SmartSizing)]
    [InlineData(RdpResolutionMode.Fixed)]
    [InlineData(RdpResolutionMode.Multimon)]
    public void Resolve_WithManualOverride_AlwaysReportsFixedWithOverrideDims(
        RdpResolutionMode profileMode)
    {
        var state = RdpResolutionModeIndicator.Resolve(
            profileMode,
            manualWidth: 1280,
            manualHeight: 720,
            profileFixedWidth: 1920,
            profileFixedHeight: 1080);

        Assert.Equal(RdpResolutionMode.Fixed, state.Mode);
        Assert.Equal(1280, state.Width);
        Assert.Equal(720, state.Height);
    }

    [Theory]
    [InlineData(RdpResolutionMode.Auto)]
    [InlineData(RdpResolutionMode.FitWindow)]
    [InlineData(RdpResolutionMode.SmartSizing)]
    [InlineData(RdpResolutionMode.Multimon)]
    public void Resolve_WithoutOverride_ReturnsProfileModeWithoutDims(RdpResolutionMode profileMode)
    {
        var state = RdpResolutionModeIndicator.Resolve(
            profileMode,
            manualWidth: 0,
            manualHeight: 0,
            profileFixedWidth: 0,
            profileFixedHeight: 0);

        Assert.Equal(profileMode, state.Mode);
        Assert.Null(state.Width);
        Assert.Null(state.Height);
    }

    [Fact]
    public void Resolve_FixedProfileWithoutOverride_SurfacesProfileDims()
    {
        var state = RdpResolutionModeIndicator.Resolve(
            RdpResolutionMode.Fixed,
            manualWidth: 0,
            manualHeight: 0,
            profileFixedWidth: 2560,
            profileFixedHeight: 1440);

        Assert.Equal(RdpResolutionMode.Fixed, state.Mode);
        Assert.Equal(2560, state.Width);
        Assert.Equal(1440, state.Height);
    }

    [Fact]
    public void Resolve_FixedProfileWithZeroDims_ReturnsFixedWithoutDims()
    {
        var state = RdpResolutionModeIndicator.Resolve(
            RdpResolutionMode.Fixed,
            manualWidth: 0,
            manualHeight: 0,
            profileFixedWidth: 0,
            profileFixedHeight: 0);

        Assert.Equal(RdpResolutionMode.Fixed, state.Mode);
        Assert.Null(state.Width);
        Assert.Null(state.Height);
    }

    [Fact]
    public void Resolve_PartialManualOverride_FallsBackToProfile()
    {
        var state = RdpResolutionModeIndicator.Resolve(
            RdpResolutionMode.FitWindow,
            manualWidth: 1280,
            manualHeight: 0,
            profileFixedWidth: 0,
            profileFixedHeight: 0);

        Assert.Equal(RdpResolutionMode.FitWindow, state.Mode);
        Assert.Null(state.Width);
        Assert.Null(state.Height);
    }

    [Fact]
    public void GetGlyph_AllModes_ReturnFiveDistinctSegoeMdl2Codepoints()
    {
        var glyphs = new[]
        {
            RdpResolutionModeIndicator.GetGlyph(RdpResolutionMode.Auto),
            RdpResolutionModeIndicator.GetGlyph(RdpResolutionMode.FitWindow),
            RdpResolutionModeIndicator.GetGlyph(RdpResolutionMode.SmartSizing),
            RdpResolutionModeIndicator.GetGlyph(RdpResolutionMode.Fixed),
            RdpResolutionModeIndicator.GetGlyph(RdpResolutionMode.Multimon),
        };

        Assert.Equal(5, glyphs.Distinct().Count());

        foreach (var glyph in glyphs)
        {
            Assert.Equal(1, glyph.Length);
            var codepoint = (int)glyph[0];
            Assert.InRange(codepoint, 0xE000, 0xF8FF);
        }
    }

    [Theory]
    [InlineData(RdpResolutionMode.Auto, "RdpResolutionModeAutoShort")]
    [InlineData(RdpResolutionMode.FitWindow, "ServerDialogResolutionModeFitWindow")]
    [InlineData(RdpResolutionMode.SmartSizing, "ServerDialogResolutionModeSmartSizing")]
    [InlineData(RdpResolutionMode.Fixed, "ServerDialogResolutionModeFixed")]
    [InlineData(RdpResolutionMode.Multimon, "ServerDialogResolutionModeMultimon")]
    public void GetModeLocalizationKey_ReturnsExpectedKey(
        RdpResolutionMode mode,
        string expectedKey)
    {
        Assert.Equal(expectedKey, RdpResolutionModeIndicator.GetModeLocalizationKey(mode));
    }

    [Fact]
    public void FormatHeader_WithoutDims_OmitsDimensionsSegment()
    {
        var result = RdpResolutionModeIndicator.FormatHeader(
            "Active mode",
            "Fit window",
            null,
            null);

        Assert.Equal("Active mode: Fit window", result);
    }

    [Fact]
    public void FormatHeader_WithDims_AppendsParenthesizedDimensions()
    {
        var result = RdpResolutionModeIndicator.FormatHeader(
            "Active mode",
            "Fixed",
            1920,
            1080);

        Assert.Equal("Active mode: Fixed (1920x1080)", result);
    }

    /// <summary>
    /// The header layout comes from the caller, so a translator can reach it.
    /// </summary>
    /// <remarks>
    /// It used to be a literal compiled into the method, carrying a multiplication sign that the
    /// method's own documentation described as an ASCII x. Neither the separator, the word order
    /// nor the character was adaptable, on a string shown in the resolution menu.
    /// </remarks>
    [Fact]
    public void FormatHeader_UsesTheSuppliedTemplate()
    {
        var result = RdpResolutionModeIndicator.FormatHeader(
            "Mode actif",
            "Fixe",
            1920,
            1080,
            "{0} : {1}",
            "{0} : {1} [{2} sur {3}]");

        Assert.Equal("Mode actif : Fixe [1920 sur 1080]", result);
    }

    [Fact]
    public void FormatHeader_WithoutDimensions_UsesTheSuppliedModeOnlyTemplate()
    {
        var result = RdpResolutionModeIndicator.FormatHeader(
            "Mode actif",
            "Ajuster",
            null,
            null,
            "{0} : {1}",
            "{0} : {1} [{2} sur {3}]");

        Assert.Equal("Mode actif : Ajuster", result);
    }

    /// <summary>
    /// And the fallback a caller with no locale access gets is ASCII, which the previous literal
    /// was not.
    /// </summary>
    [Fact]
    public void FormatHeader_WithNoTemplate_ProducesOnlyAsciiPunctuation()
    {
        var result = RdpResolutionModeIndicator.FormatHeader(
            "Active mode",
            "Fixed",
            1920,
            1080);

        Assert.All(result, c => Assert.True(c <= '\u007e', $"non-ASCII character U+{(int)c:X4}"));
    }

    [Fact]
    public void FormatHeader_WithZeroDims_OmitsDimensionsSegment()
    {
        var result = RdpResolutionModeIndicator.FormatHeader(
            "Active mode",
            "Fixed",
            0,
            0);

        Assert.Equal("Active mode: Fixed", result);
    }

    [Fact]
    public void FormatTooltip_WithoutDims_UsesModeOnlyTemplate()
    {
        var result = RdpResolutionModeIndicator.FormatTooltip(
            "Change resolution - {0}",
            "Change resolution - {0} ({1}x{2})",
            "Multi-monitor",
            null,
            null);

        Assert.Equal("Change resolution - Multi-monitor", result);
    }

    [Fact]
    public void FormatTooltip_WithDims_UsesModeAndSizeTemplate()
    {
        var result = RdpResolutionModeIndicator.FormatTooltip(
            "Change resolution - {0}",
            "Change resolution - {0} ({1}x{2})",
            "Fixed",
            2560,
            1440);

        Assert.Equal("Change resolution - Fixed (2560x1440)", result);
    }
}
