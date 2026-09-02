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
using Heimdall.Core.Rdp;
using Heimdall.Rdp.Display;

namespace Heimdall.Rdp.Tests.Display;

public sealed class RdpDisplayResolverTests
{
    [Fact]
    public void Resolve_AutoFullscreen_UsesExactMonitorPreset()
    {
        var result = Resolve(
            RdpResolutionMode.Auto,
            Host(monitor: new Size(2560, 1440), isFullscreen: true),
            [(1920, 1080), (2560, 1440), (3840, 2160)]);

        Assert.Equal(RdpResolutionMode.Auto, result.ConfiguredMode);
        Assert.Equal(RdpResolutionMode.Fixed, result.EffectiveMode);
        Assert.Equal(2560, result.Width);
        Assert.Equal(1440, result.Height);
        Assert.False(result.SmartSizingEnabled);
        Assert.False(result.MultiMonitorEnabled);
        Assert.Equal("auto-fullscreen-single-monitor", result.Reason);
    }

    /// <summary>
    /// This case used to assert 1920x1080, which is one pixel wider and one pixel taller than the
    /// monitor: it froze the overshoot as intended behaviour. The rule is that the desktop never
    /// exceeds the monitor, so the nearest preset that fits wins even when a nearer one does not.
    /// </summary>
    [Fact]
    public void Resolve_AutoFullscreen_UsesClosestPresetThatFits()
    {
        var result = Resolve(
            RdpResolutionMode.Auto,
            Host(monitor: new Size(1919, 1079), isFullscreen: true),
            [(1280, 720), (1920, 1080), (2560, 1440)]);

        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
    }

    /// <summary>
    /// The fullscreen branch resolves with smart sizing off and the host strips the control's
    /// scrollbars, so every row and column the desktop has beyond the monitor is unreachable for
    /// the life of the session, the remote taskbar included. Unfiltered, the nearest preset to a
    /// 3440x1440 ultrawide is 3840x2160 (distance 678400) and not 2560x1440 (774400).
    /// </summary>
    [Fact]
    public void Resolve_AutoFullscreen_NeverExceedsTheMonitor()
    {
        var result = Resolve(
            RdpResolutionMode.Auto,
            Host(monitor: new Size(3440, 1440), isFullscreen: true),
            [(1920, 1080), (2560, 1440), (3840, 2160)]);

        Assert.Equal(2560, result.Width);
        Assert.Equal(1440, result.Height);
    }

    /// <summary>
    /// Overshooting on one axis is still an overshoot: 1920x1080 on a 1920x1024 monitor is the
    /// nearest preset by distance and loses 56 rows that nothing can scroll to.
    /// </summary>
    [Fact]
    public void Resolve_AutoFullscreen_PresetTallerThanTheMonitor_IsRejected()
    {
        var result = Resolve(
            RdpResolutionMode.Auto,
            Host(monitor: new Size(1920, 1024), isFullscreen: true),
            [(1920, 1080), (1600, 900)]);

        Assert.Equal(1600, result.Width);
        Assert.Equal(900, result.Height);
    }

    /// <summary>
    /// A monitor smaller than every configured preset still has one candidate that is certain to
    /// fit: itself.
    /// </summary>
    [Fact]
    public void Resolve_AutoFullscreen_NoPresetFits_UsesTheMonitorSize()
    {
        var result = Resolve(
            RdpResolutionMode.Auto,
            Host(monitor: new Size(1600, 1200), isFullscreen: true),
            [(1920, 1080), (3840, 2160)]);

        Assert.Equal(1600, result.Width);
        Assert.Equal(1200, result.Height);
    }

    [Fact]
    public void Resolve_AutoFullscreen_OddPresetWidth_SnapsDown()
    {
        var result = Resolve(
            RdpResolutionMode.Auto,
            Host(monitor: new Size(1366, 768), isFullscreen: true),
            [(1280, 720), (1366, 768), (1440, 900)]);

        Assert.Equal(1364, result.Width);
        Assert.Equal(768, result.Height);
    }

    [Fact]
    public void Resolve_AutoFullscreen_WithoutPresets_UsesMonitorBounds()
    {
        var result = Resolve(
            RdpResolutionMode.Auto,
            Host(monitor: new Size(1600, 900), isFullscreen: true),
            []);

        Assert.Equal(1600, result.Width);
        Assert.Equal(900, result.Height);
    }

    [Fact]
    public void Resolve_AutoWindowed_UsesViewportAndSmartSizing()
    {
        var result = Resolve(
            RdpResolutionMode.Auto,
            Host(viewport: new Size(1235, 700)),
            [(1920, 1080)]);

        Assert.Equal(RdpResolutionMode.SmartSizing, result.EffectiveMode);
        Assert.Equal(1232, result.Width);
        Assert.Equal(700, result.Height);
        Assert.True(result.SmartSizingEnabled);
        Assert.False(result.MultiMonitorEnabled);
        Assert.Equal("auto-windowed", result.Reason);
    }

    [Fact]
    public void Resolve_AutoWindowed_InvalidViewport_FallsBackToWorkingArea()
    {
        var result = Resolve(
            RdpResolutionMode.Auto,
            Host(viewport: Size.Empty, workingArea: new Size(1800, 1000)),
            [(1920, 1080)]);

        Assert.Equal(1800, result.Width);
        Assert.Equal(1000, result.Height);
        Assert.Equal("auto-windowed-fallback", result.Reason);
    }

    [Fact]
    public void Resolve_Auto_DoesNotEnableMultimonWhenHostRequestedMultimon()
    {
        var result = Resolve(
            RdpResolutionMode.Auto,
            Host(isFullscreen: true, isMultiMonitorRequested: true, screenCount: 2),
            [(1920, 1080)]);

        Assert.False(result.MultiMonitorEnabled);
        Assert.Equal(RdpResolutionMode.Fixed, result.EffectiveMode);
    }

    /// <summary>
    /// This case used to assert that 639 became 640, which read as a snap and was in fact the
    /// private 640 floor: the snap alone takes 639 down to 636.
    /// </summary>
    [Fact]
    public void Resolve_Fixed_UsesConfiguredDimensionsAndSnapsWidthDown()
    {
        var result = Resolve(
            RdpResolutionMode.Fixed,
            Host(),
            [],
            configuredWidthPx: 639,
            configuredHeightPx: 480);

        Assert.Equal(RdpResolutionMode.Fixed, result.EffectiveMode);
        Assert.Equal(636, result.Width);
        Assert.Equal(480, result.Height);
        Assert.False(result.SmartSizingEnabled);
        Assert.Equal("explicit-fixed", result.Reason);
    }

    /// <summary>
    /// The dialog accepts a fixed width down to <see cref="RdpDisplayLimits.MinimumFixedDimension" />,
    /// the schema validator accepts it, and the external mstsc path honours it. The embedded path
    /// used to raise anything under 640 to 640, width only, so one 400x400 profile ran as 400x400
    /// externally and 640x400 embedded while the view kept aspect-fitting its frame to the 1:1 it
    /// had asked for.
    /// </summary>
    [Fact]
    public void Resolve_Fixed_HonoursTheSharedMinimumWidth()
    {
        var result = Resolve(
            RdpResolutionMode.Fixed,
            Host(),
            [],
            configuredWidthPx: 400,
            configuredHeightPx: 400);

        Assert.Equal(400, result.Width);
        Assert.Equal(400, result.Height);
    }

    /// <summary>
    /// The floor that does apply to width is the shared one. A site that reintroduced a minimum of
    /// its own would have to disagree with this.
    /// </summary>
    [Fact]
    public void Resolve_Fixed_UndersizedWidth_ClampsToTheSharedMinimum()
    {
        var result = Resolve(
            RdpResolutionMode.Fixed,
            Host(),
            [],
            configuredWidthPx: 10,
            configuredHeightPx: 480);

        Assert.Equal(RdpDisplayLimits.MinimumFixedDimension, result.Width);
        Assert.Equal(480, result.Height);
    }

    [Fact]
    public void Resolve_Fixed_WithoutConfiguredDimensions_UsesDefaultDesktop()
    {
        var result = Resolve(RdpResolutionMode.Fixed, Host(), []);

        Assert.Equal(1024, result.Width);
        Assert.Equal(768, result.Height);
    }

    /// <summary>
    /// A hand-edited, imported or synchronised profile can carry an out-of-range
    /// fixed size: the schema validator downgrades the range error to a warning and
    /// loads the server anyway. The clamp on the external mstsc path is never
    /// called for the embedded control, so the bound has to hold here.
    /// </summary>
    [Fact]
    public void Resolve_Fixed_OversizedDimensions_ClampsToMaximum()
    {
        var result = Resolve(
            RdpResolutionMode.Fixed,
            Host(),
            [],
            configuredWidthPx: 20000,
            configuredHeightPx: 20000);

        Assert.Equal(RdpDisplayLimits.MaximumFixedWidth, result.Width);
        Assert.Equal(RdpDisplayLimits.MaximumFixedHeight, result.Height);
    }

    /// <summary>
    /// Height carries the lower bound of the clamp on its own: it is never snapped and never
    /// floored a second time, so what comes out is what the shared clamp decided.
    /// </summary>
    [Fact]
    public void Resolve_Fixed_UndersizedHeight_ClampsToMinimum()
    {
        var result = Resolve(
            RdpResolutionMode.Fixed,
            Host(),
            [],
            configuredWidthPx: 1024,
            configuredHeightPx: 10);

        Assert.Equal(RdpDisplayLimits.MinimumFixedDimension, result.Height);
    }

    [Fact]
    public void Resolve_SmartSizing_UsesViewport()
    {
        var result = Resolve(
            RdpResolutionMode.SmartSizing,
            Host(viewport: new Size(1101, 620)),
            []);

        Assert.Equal(RdpResolutionMode.SmartSizing, result.EffectiveMode);
        Assert.Equal(1100, result.Width);
        Assert.Equal(620, result.Height);
        Assert.True(result.SmartSizingEnabled);
        Assert.Equal("explicit-smart-sizing", result.Reason);
    }

    [Fact]
    public void Resolve_SmartSizing_InvalidViewport_FallsBackToWorkingArea()
    {
        var result = Resolve(
            RdpResolutionMode.SmartSizing,
            Host(viewport: Size.Empty, workingArea: new Size(1700, 960)),
            []);

        Assert.Equal(1700, result.Width);
        Assert.Equal(960, result.Height);
        Assert.Equal("explicit-smart-sizing", result.Reason);
    }

    [Fact]
    public void Resolve_Multimon_UsesMonitorBoundsAndEnablesMultimon()
    {
        var result = Resolve(
            RdpResolutionMode.Multimon,
            Host(monitor: new Size(1919, 1080), isMultiMonitorRequested: true, screenCount: 2),
            []);

        Assert.Equal(RdpResolutionMode.Multimon, result.EffectiveMode);
        Assert.Equal(1916, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.True(result.MultiMonitorEnabled);
        Assert.False(result.SmartSizingEnabled);
        Assert.Equal("explicit-multimon", result.Reason);
    }

    [Fact]
    public void Resolve_FitWindow_UsesViewportWithSmartSizing()
    {
        var result = Resolve(
            RdpResolutionMode.FitWindow,
            Host(viewport: new Size(1281, 721)),
            []);

        Assert.Equal(RdpResolutionMode.FitWindow, result.EffectiveMode);
        Assert.Equal(1280, result.Width);
        Assert.Equal(721, result.Height);
        Assert.True(result.SmartSizingEnabled);
        Assert.False(result.MultiMonitorEnabled);
        Assert.Equal("explicit-fit-window-scaled", result.Reason);
    }

    [Fact]
    public void Resolve_FitWindow_InvalidViewport_FallsBackToWorkingArea()
    {
        var result = Resolve(
            RdpResolutionMode.FitWindow,
            Host(viewport: Size.Empty, workingArea: new Size(1500, 820)),
            []);

        Assert.Equal(1500, result.Width);
        Assert.Equal(820, result.Height);
        Assert.Equal("explicit-fit-window-scaled", result.Reason);
    }

    [Fact]
    public void Resolve_DpiScale_SnapsDesktopAndDeviceScaleUp()
    {
        var result = Resolve(
            RdpResolutionMode.FitWindow,
            Host(dpiScale: 1.49),
            []);

        Assert.Equal(150u, result.DesktopScaleFactor);
        Assert.Equal(140u, result.DeviceScaleFactor);
    }

    [Fact]
    public void Resolve_DpiScale_125_MapsDeviceScaleTo140()
    {
        var result = Resolve(
            RdpResolutionMode.FitWindow,
            Host(dpiScale: 1.37),
            []);

        Assert.Equal(125u, result.DesktopScaleFactor);
        Assert.Equal(140u, result.DeviceScaleFactor);
    }

    [Fact]
    public void Resolve_DpiScale_175_MapsDeviceScaleTo180()
    {
        var result = Resolve(
            RdpResolutionMode.FitWindow,
            Host(dpiScale: 1.75),
            []);

        Assert.Equal(175u, result.DesktopScaleFactor);
        Assert.Equal(180u, result.DeviceScaleFactor);
    }

    [Fact]
    public void Resolve_DpiScale_250_MapsDesktopScaleAboveLegacyCap()
    {
        var result = Resolve(
            RdpResolutionMode.FitWindow,
            Host(dpiScale: 2.5),
            []);

        Assert.Equal(250u, result.DesktopScaleFactor);
        Assert.Equal(180u, result.DeviceScaleFactor);
    }

    [Fact]
    public void Resolve_InvalidDpiScale_DefaultsTo100()
    {
        var result = Resolve(
            RdpResolutionMode.FitWindow,
            Host(dpiScale: double.NaN),
            []);

        Assert.Equal(100u, result.DesktopScaleFactor);
        Assert.Equal(100u, result.DeviceScaleFactor);
    }

    [Fact]
    public void Resolve_TinyViewportWidth_ClampsToMinimumSnappedWidth()
    {
        var result = Resolve(
            RdpResolutionMode.SmartSizing,
            Host(viewport: new Size(2, 100)),
            []);

        Assert.Equal(4, result.Width);
        Assert.Equal(100, result.Height);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1366, 768, 1364, 768)]
    [InlineData(1919, 1079, 1916, 1076)]
    public void ResolveExternalAutoWindowedSize_SnapsWorkingAreaToMultipleOf4(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight)
    {
        var result = RdpDisplayResolver.ResolveExternalAutoWindowedSize(
            new Size(width, height),
            new Size(1024, 768));

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
    }

    private static EffectiveDisplayContext Resolve(
        RdpResolutionMode configuredMode,
        HostDisplayContext hostContext,
        IReadOnlyList<(int Width, int Height)> presets,
        int? configuredWidthPx = null,
        int? configuredHeightPx = null)
        => RdpDisplayResolver.Resolve(
            configuredMode,
            hostContext,
            presets,
            configuredWidthPx,
            configuredHeightPx);

    private static HostDisplayContext Host(
        Size? monitor = null,
        Size? workingArea = null,
        Size? viewport = null,
        double dpiScale = 1.0,
        bool isFullscreen = false,
        int screenCount = 1,
        bool isMultiMonitorRequested = false)
        => new()
        {
            MonitorBoundsPhysicalPx = monitor ?? new Size(1920, 1080),
            WorkingAreaPhysicalPx = workingArea ?? new Size(1920, 1040),
            DesktopDpiScale = dpiScale,
            ViewportPhysicalPx = viewport ?? new Size(1280, 720),
            IsFullscreen = isFullscreen,
            ScreenCount = screenCount,
            IsMultiMonitorRequested = isMultiMonitorRequested
        };
}
