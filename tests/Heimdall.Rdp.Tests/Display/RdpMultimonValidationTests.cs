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

using System.Drawing;
using Heimdall.Core.Configuration;
using Heimdall.Rdp.Display;

namespace Heimdall.Rdp.Tests.Display;

public sealed class RdpMultimonValidationTests
{
    [Fact]
    public void ValidateMultimon_SingleMonitorRequestedOnSingleMonitorHost_FallsBack()
    {
        var result = Validate(
            monitorCount: 1,
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: []));

        Assert.True(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.SingleMonitorHost, result.Reason);
        Assert.Equal(RdpResolutionMode.FitWindow, result.CoercedSettings.ResolutionMode);
        Assert.False(result.CoercedSettings.UseMultimon);
        Assert.Empty(result.CoercedSettings.SelectedMonitorIndices);
    }

    [Fact]
    public void ValidateMultimon_InvalidSelectedMonitorIndex_FallsBack()
    {
        var result = Validate(
            monitorCount: 2,
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: [0, 2]));

        Assert.True(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.InvalidMonitorIndex, result.Reason);
        Assert.Equal(RdpResolutionMode.FitWindow, result.CoercedSettings.ResolutionMode);
        Assert.False(result.CoercedSettings.UseMultimon);
        Assert.Empty(result.CoercedSettings.SelectedMonitorIndices);
    }

    [Fact]
    public void ValidateMultimon_NegativeSelectedMonitorIndex_FallsBack()
    {
        var result = Validate(
            monitorCount: 2,
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: [-1]));

        Assert.True(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.InvalidMonitorIndex, result.Reason);
        Assert.Equal(RdpResolutionMode.FitWindow, result.CoercedSettings.ResolutionMode);
        Assert.False(result.CoercedSettings.UseMultimon);
        Assert.Empty(result.CoercedSettings.SelectedMonitorIndices);
    }

    [Fact]
    public void ValidateMultimon_NegativeIndexMixedWithValid_FallsBack()
    {
        var result = Validate(
            monitorCount: 2,
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: [0, -1]));

        Assert.True(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.InvalidMonitorIndex, result.Reason);
        Assert.Equal(RdpResolutionMode.FitWindow, result.CoercedSettings.ResolutionMode);
        Assert.False(result.CoercedSettings.UseMultimon);
        Assert.Empty(result.CoercedSettings.SelectedMonitorIndices);
    }

    [Fact]
    public void ValidateMultimon_EmptySelectionWithMultimonRequested_UsesAllMonitors()
    {
        var requested = Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: []);

        var result = Validate(monitorCount: 2, requested);

        Assert.False(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.None, result.Reason);
        Assert.Same(requested, result.CoercedSettings);
    }

    [Fact]
    public void ValidateMultimon_SingleMonitorRequestedOnSingleMonitorHost_DoesNotFallback()
    {
        var requested = Settings(RdpResolutionMode.FitWindow, useMultimon: false, selectedMonitors: []);

        var result = Validate(monitorCount: 1, requested);

        Assert.False(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.None, result.Reason);
        Assert.Same(requested, result.CoercedSettings);
    }

    [Fact]
    public void ValidateMultimon_ValidSelectedMonitorIndices_DoesNotFallback()
    {
        var requested = Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: [0, 1]);

        var result = Validate(monitorCount: 2, requested);

        Assert.False(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.None, result.Reason);
        Assert.Same(requested, result.CoercedSettings);
    }

    [Fact]
    public void ValidateMultimon_CoercedSettingsResolveToStableSingleMonitorMode()
    {
        var result = Validate(
            monitorCount: 1,
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: []));

        Assert.True(result.ShouldFallback);

        var effective = RdpDisplayResolver.Resolve(
            result.CoercedSettings.ResolutionMode,
            Host(screenCount: 1),
            []);
        var secondPass = Validate(monitorCount: 1, result.CoercedSettings);

        Assert.Equal(RdpResolutionMode.FitWindow, effective.EffectiveMode);
        Assert.False(effective.MultiMonitorEnabled);
        Assert.False(secondPass.ShouldFallback);
    }

    // Three dense monitors laid out left to right, which is the arrangement the finding was
    // reported against.
    private static readonly Rectangle[] ThreeDenseMonitors =
    [
        new(0, 0, 1920, 1080),
        new(1920, 0, 1920, 1080),
        new(3840, 0, 1920, 1080),
    ];

    private static RdpMultimonValidation ValidateAgainst(
        IReadOnlyList<Rectangle> monitorBounds,
        RdpDisplaySettings requested)
        => RdpDisplayResolver.ValidateMultimon(
            RdpDisplayCapabilities.FromMonitorBounds(monitorBounds),
            requested);

    // A host whose screens could not be enumerated reports zero. With an empty selection there was
    // no index to be out of range, so multimon used to be left on for a host nobody could describe.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AHostThatCannotOfferMultimonFallsBackEvenWithAnEmptySelection(int monitorCount)
    {
        RdpMultimonValidation result = RdpDisplayResolver.ValidateMultimon(
            new RdpDisplayCapabilities(monitorCount),
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: []));

        Assert.True(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.SingleMonitorHost, result.Reason);
        Assert.False(result.CoercedSettings.UseMultimon);
    }

    [Fact]
    public void SelectingTheFirstAndThirdOfThreeMonitorsFallsBack()
    {
        RdpMultimonValidation result = ValidateAgainst(
            ThreeDenseMonitors,
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: [0, 2]));

        Assert.True(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.NonContiguousSelection, result.Reason);
        Assert.Equal(RdpResolutionMode.FitWindow, result.CoercedSettings.ResolutionMode);
        Assert.False(result.CoercedSettings.UseMultimon);
        Assert.Empty(result.CoercedSettings.SelectedMonitorIndices);
    }

    [Fact]
    public void SelectingTwoAdjacentMonitorsIsAccepted()
    {
        RdpDisplaySettings requested =
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: [1, 2]);

        RdpMultimonValidation result = ValidateAgainst(ThreeDenseMonitors, requested);

        Assert.False(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.None, result.Reason);
        Assert.Same(requested, result.CoercedSettings);
    }

    // An L-shape is a valid Windows 7 and later arrangement, so the whole selection is accepted
    // even though it does not span a rectangle.
    [Fact]
    public void AnLShapedSelectionIsAccepted()
    {
        Rectangle[] lShaped =
        [
            new(0, 0, 1920, 1080),
            new(1920, 0, 1920, 1080),
            new(0, 1080, 1920, 1080),
        ];
        RdpDisplaySettings requested =
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: [0, 1, 2]);

        RdpMultimonValidation result = ValidateAgainst(lShaped, requested);

        Assert.False(result.ShouldFallback);
        Assert.Same(requested, result.CoercedSettings);
    }

    // The "every monitor" sentinel is left alone on purpose: a host whose screens meet only at a
    // corner works today across all of them, and coercing here would silently drop one.
    [Fact]
    public void AnEmptySelectionIsNotCoercedEvenWhenTheHostIsDisjoint()
    {
        Rectangle[] disjoint =
        [
            new(0, 0, 1920, 1080),
            new(10000, 0, 1920, 1080),
        ];
        RdpDisplaySettings requested =
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: []);

        RdpMultimonValidation result = ValidateAgainst(disjoint, requested);

        Assert.False(result.ShouldFallback);
        Assert.Same(requested, result.CoercedSettings);
    }

    [Fact]
    public void ASingleSelectedMonitorIsNeverDisconnected()
    {
        RdpDisplaySettings requested =
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: [2]);

        RdpMultimonValidation result = ValidateAgainst(ThreeDenseMonitors, requested);

        Assert.False(result.ShouldFallback);
        Assert.Same(requested, result.CoercedSettings);
    }

    // A host that reports a count without a topology cannot answer the question, and guessing
    // there would be worse than leaving it unanswered.
    [Fact]
    public void AnUnknownTopologyDoesNotProduceAContiguityFallback()
    {
        RdpDisplaySettings requested =
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: [0, 2]);

        RdpMultimonValidation result =
            RdpDisplayResolver.ValidateMultimon(new RdpDisplayCapabilities(3), requested);

        Assert.False(result.ShouldFallback);
        Assert.NotEqual(MultimonFallbackReason.NonContiguousSelection, result.Reason);
    }

    // The range check still runs first, so an out-of-range index is reported as such rather than as
    // a geometry problem the user cannot act on.
    [Fact]
    public void AnOutOfRangeIndexIsStillReportedAsInvalidRatherThanDisconnected()
    {
        RdpMultimonValidation result = ValidateAgainst(
            ThreeDenseMonitors,
            Settings(RdpResolutionMode.Multimon, useMultimon: true, selectedMonitors: [0, 7]));

        Assert.True(result.ShouldFallback);
        Assert.Equal(MultimonFallbackReason.InvalidMonitorIndex, result.Reason);
    }

    private static RdpMultimonValidation Validate(int monitorCount, RdpDisplaySettings requested)
        => RdpDisplayResolver.ValidateMultimon(new RdpDisplayCapabilities(monitorCount), requested);

    private static RdpDisplaySettings Settings(
        RdpResolutionMode mode,
        bool useMultimon,
        IReadOnlyList<int> selectedMonitors)
        => new(mode, useMultimon, selectedMonitors);

    private static HostDisplayContext Host(int screenCount)
        => new()
        {
            MonitorBoundsPhysicalPx = new Size(1920, 1080),
            WorkingAreaPhysicalPx = new Size(1920, 1040),
            DesktopDpiScale = 1.0,
            ViewportPhysicalPx = new Size(1280, 720),
            IsFullscreen = false,
            ScreenCount = screenCount,
            IsMultiMonitorRequested = false
        };
}
