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
using WinForms = System.Windows.Forms;

namespace Heimdall.App.Services;

/// <summary>
/// Provides physical monitor working areas normalized to WPF device-independent units.
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
}
