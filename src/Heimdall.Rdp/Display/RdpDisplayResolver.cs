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
using Heimdall.Rdp;

namespace Heimdall.Rdp.Display;

public static class RdpDisplayResolver
{
    private const int MinDesktopWidthPx = 640;
    private const int WidthSnapMultiplePx = 4;

    private static readonly Size DefaultSize = new(1024, 768);

    public static EffectiveDisplayContext Resolve(
        RdpResolutionMode configuredMode,
        HostDisplayContext hostContext,
        IReadOnlyList<(int Width, int Height)> presets,
        int? configuredWidthPx = null,
        int? configuredHeightPx = null)
    {
        ArgumentNullException.ThrowIfNull(hostContext);
        ArgumentNullException.ThrowIfNull(presets);

        // Desktop/device scale factors map onto the canonical RDP API tables.
        var desktopScaleFactor = RdpDisplayHelper.MapDpiToDesktopScaleFactor(hostContext.DesktopDpiScale);
        var deviceScaleFactor = RdpDisplayHelper.MapDpiToDeviceScaleFactor(hostContext.DesktopDpiScale);

        return configuredMode switch
        {
            RdpResolutionMode.Auto => ResolveAuto(
                hostContext,
                presets,
                desktopScaleFactor,
                deviceScaleFactor),
            RdpResolutionMode.Fixed => Create(
                configuredMode,
                RdpResolutionMode.Fixed,
                // A profile can reach here unbounded: the schema validator downgrades an
                // out-of-range dimension to a warning and loads the server anyway, and the
                // clamp on the external mstsc path (RdpProfileResolver) is never called for
                // the embedded control. Bound it here, against the shared Core limits.
                new Size(
                    RdpDisplayLimits.ClampFixedWidth(
                        configuredWidthPx.GetValueOrDefault(DefaultSize.Width)),
                    RdpDisplayLimits.ClampFixedHeight(
                        configuredHeightPx.GetValueOrDefault(DefaultSize.Height))),
                desktopScaleFactor,
                deviceScaleFactor,
                smartSizing: false,
                multiMonitor: false,
                reason: "explicit-fixed",
                floorWidthToDesktopMinimum: true),
            RdpResolutionMode.SmartSizing => Create(
                configuredMode,
                RdpResolutionMode.SmartSizing,
                ResolveViewportOrFallback(hostContext, out _),
                desktopScaleFactor,
                deviceScaleFactor,
                smartSizing: true,
                multiMonitor: false,
                reason: "explicit-smart-sizing",
                floorWidthToDesktopMinimum: false),
            RdpResolutionMode.Multimon => Create(
                configuredMode,
                RdpResolutionMode.Multimon,
                CoalesceSize(hostContext.MonitorBoundsPhysicalPx, DefaultSize),
                desktopScaleFactor,
                deviceScaleFactor,
                smartSizing: false,
                multiMonitor: true,
                reason: "explicit-multimon",
                floorWidthToDesktopMinimum: true),
            _ => Create(
                configuredMode,
                RdpResolutionMode.FitWindow,
                ResolveViewportOrFallback(hostContext, out _),
                desktopScaleFactor,
                deviceScaleFactor,
                smartSizing: true,
                multiMonitor: false,
                reason: "explicit-fit-window-scaled",
                floorWidthToDesktopMinimum: false)
        };
    }

    public static RdpMultimonValidation ValidateMultimon(
        RdpDisplayCapabilities host,
        RdpDisplaySettings requested)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(requested);

        if (!requested.UseMultimon)
        {
            return new RdpMultimonValidation(false, MultimonFallbackReason.None, requested);
        }

        // Fewer than two, not exactly one. A host whose screens could not be enumerated reports
        // zero, and zero is not one, so multimon used to survive this check whenever the selection
        // was empty. Coercing is the conservative answer for a host that cannot be described.
        if (host.MonitorCount <= 1)
        {
            return CreateMultimonFallback(requested, MultimonFallbackReason.SingleMonitorHost);
        }

        if (requested.SelectedMonitorIndices.Any(index => index < 0 || index >= host.MonitorCount))
        {
            return CreateMultimonFallback(requested, MultimonFallbackReason.InvalidMonitorIndex);
        }

        if (IsSelectionDisconnected(host, requested.SelectedMonitorIndices))
        {
            return CreateMultimonFallback(requested, MultimonFallbackReason.NonContiguousSelection);
        }

        return new RdpMultimonValidation(false, MultimonFallbackReason.None, requested);
    }

    /// <summary>
    /// Whether an explicit selection of two or more monitors leaves the desktop in pieces.
    /// </summary>
    /// <remarks>
    /// <para>Answered only when the topology is actually known. A host whose screens could not be
    /// enumerated carries a count without bounds, and guessing there would be worse than the
    /// question going unanswered.</para>
    /// <para>An empty selection is the "every monitor" sentinel and is deliberately left alone.
    /// Windows permits arrangements where two monitors meet only at a corner; such a host works
    /// today across all its screens, and coercing the sentinel would silently drop one of them.
    /// </para>
    /// <para>A single selected monitor cannot be disconnected from anything.</para>
    /// <para>The count check here is deliberately redundant with the predicate, which also treats
    /// fewer than two monitors as connected. Removing either one alone changes nothing observable;
    /// they are kept apart so that a future change to what the predicate says about an empty set
    /// cannot silently start coercing the sentinel.</para>
    /// </remarks>
    private static bool IsSelectionDisconnected(
        RdpDisplayCapabilities host,
        IReadOnlyList<int> selectedMonitorIndices)
    {
        if (selectedMonitorIndices.Count < 2 || host.MonitorBounds.Count != host.MonitorCount)
        {
            return false;
        }

        Rectangle[] selectedBounds =
            [.. selectedMonitorIndices.Select(index => host.MonitorBounds[index])];

        return !RdpMonitorContiguity.AreContiguous(selectedBounds);
    }

    public static Size ResolveExternalAutoWindowedSize(Size primaryWorkingArea, Size fallback)
    {
        var source = IsValidSize(primaryWorkingArea)
            ? primaryWorkingArea
            : fallback;

        return new Size(
            SnapDimension(source.Width),
            SnapDimension(source.Height));
    }

    private static EffectiveDisplayContext ResolveAuto(
        HostDisplayContext hostContext,
        IReadOnlyList<(int Width, int Height)> presets,
        uint desktopScaleFactor,
        uint deviceScaleFactor)
    {
        if (hostContext.IsFullscreen)
        {
            var monitorSize = CoalesceSize(hostContext.MonitorBoundsPhysicalPx, DefaultSize);
            return Create(
                RdpResolutionMode.Auto,
                RdpResolutionMode.Fixed,
                SelectClosestPreset(monitorSize, presets),
                desktopScaleFactor,
                deviceScaleFactor,
                smartSizing: false,
                multiMonitor: false,
                reason: "auto-fullscreen-single-monitor",
                floorWidthToDesktopMinimum: true);
        }

        return Create(
            RdpResolutionMode.Auto,
            RdpResolutionMode.SmartSizing,
            ResolveViewportOrFallback(hostContext, out var usedFallback),
            desktopScaleFactor,
            deviceScaleFactor,
            smartSizing: true,
            multiMonitor: false,
            reason: usedFallback ? "auto-windowed-fallback" : "auto-windowed",
            floorWidthToDesktopMinimum: false);
    }

    private static RdpMultimonValidation CreateMultimonFallback(
        RdpDisplaySettings requested,
        MultimonFallbackReason reason)
    {
        var coerced = requested with
        {
            ResolutionMode = RdpResolutionMode.FitWindow,
            UseMultimon = false,
            SelectedMonitorIndices = []
        };

        return new RdpMultimonValidation(true, reason, coerced);
    }

    private static EffectiveDisplayContext Create(
        RdpResolutionMode configuredMode,
        RdpResolutionMode effectiveMode,
        Size size,
        uint desktopScaleFactor,
        uint deviceScaleFactor,
        bool smartSizing,
        bool multiMonitor,
        string reason,
        bool floorWidthToDesktopMinimum)
    {
        return new EffectiveDisplayContext
        {
            ConfiguredMode = configuredMode,
            EffectiveMode = effectiveMode,
            Width = SnapWidth(size.Width, floorWidthToDesktopMinimum),
            Height = size.Height > 0 ? size.Height : DefaultSize.Height,
            DesktopScaleFactor = desktopScaleFactor,
            DeviceScaleFactor = deviceScaleFactor,
            SmartSizingEnabled = smartSizing,
            MultiMonitorEnabled = multiMonitor,
            Reason = reason
        };
    }

    private static Size ResolveViewportOrFallback(HostDisplayContext hostContext, out bool usedFallback)
    {
        if (IsValidSize(hostContext.ViewportPhysicalPx))
        {
            usedFallback = false;
            return hostContext.ViewportPhysicalPx;
        }

        usedFallback = true;
        return CoalesceSize(hostContext.WorkingAreaPhysicalPx, hostContext.MonitorBoundsPhysicalPx, DefaultSize);
    }

    private static Size SelectClosestPreset(Size target, IReadOnlyList<(int Width, int Height)> presets)
    {
        var hasCandidate = false;
        var selected = target;
        var selectedDistance = long.MaxValue;
        var selectedAreaDelta = long.MaxValue;
        var selectedArea = long.MinValue;

        foreach (var preset in presets)
        {
            if (preset.Width <= 0 || preset.Height <= 0)
            {
                continue;
            }

            var widthDelta = (long)preset.Width - target.Width;
            var heightDelta = (long)preset.Height - target.Height;
            var distance = widthDelta * widthDelta + heightDelta * heightDelta;
            var area = (long)preset.Width * preset.Height;
            var areaDelta = Math.Abs(area - ((long)target.Width * target.Height));

            if (!hasCandidate
                || distance < selectedDistance
                || (distance == selectedDistance && areaDelta < selectedAreaDelta)
                || (distance == selectedDistance && areaDelta == selectedAreaDelta && area > selectedArea))
            {
                hasCandidate = true;
                selected = new Size(preset.Width, preset.Height);
                selectedDistance = distance;
                selectedAreaDelta = areaDelta;
                selectedArea = area;
            }
        }

        return hasCandidate ? selected : target;
    }

    private static Size CoalesceSize(params Size[] sizes)
    {
        foreach (var size in sizes)
        {
            if (IsValidSize(size))
            {
                return size;
            }
        }

        return DefaultSize;
    }

    private static bool IsValidSize(Size size) => size.Width > 0 && size.Height > 0;

    private static int SnapDimension(int value)
    {
        var snapped = RdpDisplayHelper.SnapToMultipleOf(value, WidthSnapMultiplePx);
        return snapped > 0 ? snapped : WidthSnapMultiplePx;
    }

    private static int SnapWidth(int width, bool floorToDesktopMinimum)
    {
        var snapped = RdpDisplayHelper.SnapToMultipleOf(width, WidthSnapMultiplePx);
        if (snapped <= 0)
        {
            snapped = WidthSnapMultiplePx;
        }

        return floorToDesktopMinimum && snapped < MinDesktopWidthPx
            ? MinDesktopWidthPx
            : snapped;
    }
}
