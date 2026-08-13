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

using System.Windows;
using Heimdall.Core.Logging;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using WinForms = System.Windows.Forms;

namespace Heimdall.App.Services;

/// <summary>
/// Provides monitor working-area projections in physical pixels and WPF device-independent units.
/// </summary>
internal static class WindowWorkingAreaProvider
{
    internal static IReadOnlyList<Rect> GetWorkingAreas(DpiScale dpiScale)
    {
        if (!double.IsFinite(dpiScale.DpiScaleX) ||
            !double.IsFinite(dpiScale.DpiScaleY) ||
            dpiScale.DpiScaleX <= 0 ||
            dpiScale.DpiScaleY <= 0)
        {
            FileLogger.Warn("Window working-area enumeration skipped because the WPF DPI scale is invalid.");
            return [];
        }

        try
        {
            return WinForms.Screen.AllScreens
                .Select(screen => ConvertPixelsToDips(
                    screen.WorkingArea,
                    dpiScale.DpiScaleX,
                    dpiScale.DpiScaleY))
                .ToArray();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Window working-area enumeration failed: {ex.Message}");
            return [];
        }
    }

    internal static Rect ConvertPixelsToDips(
        DrawingRectangle pixelBounds,
        double dpiScaleX,
        double dpiScaleY)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpiScaleX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpiScaleY);

        return new Rect(
            pixelBounds.Left / dpiScaleX,
            pixelBounds.Top / dpiScaleY,
            pixelBounds.Width / dpiScaleX,
            pixelBounds.Height / dpiScaleY);
    }

    /// <summary>
    /// Projects a monitor rectangle onto its size in physical pixels, applying no
    /// device-independent conversion. The origin is deliberately discarded: callers need
    /// the dimensions of the area, never its position on the virtual desktop.
    /// </summary>
    internal static DrawingSize ToPhysicalSize(DrawingRectangle pixelBounds)
        => new(pixelBounds.Width, pixelBounds.Height);

    /// <summary>
    /// Returns the primary screen working area in physical pixels, for callers that must
    /// not scale it - external RDP sessions in particular, where mstsc reads the emitted
    /// desktop size as pixels. Returns an empty size when no primary screen can be
    /// resolved, so callers fall back to their configured default.
    /// </summary>
    internal static DrawingSize GetPrimaryWorkingAreaPhysicalPx()
    {
        try
        {
            WinForms.Screen? primaryScreen = WinForms.Screen.PrimaryScreen;
            if (primaryScreen is null)
            {
                FileLogger.Warn("Primary working-area lookup skipped because no primary screen is available.");
                return DrawingSize.Empty;
            }

            return ToPhysicalSize(primaryScreen.WorkingArea);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Primary working-area lookup failed: {ex.Message}");
            return DrawingSize.Empty;
        }
    }
}
