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

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Heimdall.App.Theming;

internal static class WindowThemeHelper
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attr,
        ref int attrValue,
        int attrSize);

    public static void ApplyCurrentTheme(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        void OnSourceInitialized(object? sender, EventArgs args)
        {
            window.SourceInitialized -= OnSourceInitialized;
            Apply(window);
        }

        if (PresentationSource.FromVisual(window) is null)
        {
            window.SourceInitialized += OnSourceInitialized;
            return;
        }

        Apply(window);
    }

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var useDarkMode = ShouldUseDarkTitleBar(window) ? 1 : 0;
        var size = Marshal.SizeOf<int>();

        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref useDarkMode, size);
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeLegacy, ref useDarkMode, size);

        if (TryResolveBrushColor(window, "SurfaceBrush", out System.Windows.Media.Color captionColor)
            || TryResolveBrushColor(window, "BackgroundBrush", out captionColor))
        {
            int captionColorRef = ToColorRef(captionColor);
            _ = DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref captionColorRef, size);
        }

        if (TryResolveBrushColor(window, "TextPrimaryBrush", out System.Windows.Media.Color textColor))
        {
            int textColorRef = ToColorRef(textColor);
            _ = DwmSetWindowAttribute(hwnd, DwmTextColor, ref textColorRef, size);
        }
    }

    private static bool TryResolveBrushColor(FrameworkElement element, string resourceKey, out System.Windows.Media.Color color)
    {
        var resource = element.TryFindResource(resourceKey)
            ?? Application.Current?.TryFindResource(resourceKey);

        if (resource is SolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }

        color = default;
        return false;
    }

    private static int ToColorRef(System.Windows.Media.Color color)
        => color.R | (color.G << 8) | (color.B << 16);

    private static bool ShouldUseDarkTitleBar(FrameworkElement element)
    {
        if (TryResolveBrushColor(element, "SurfaceBrush", out System.Windows.Media.Color color)
            || TryResolveBrushColor(element, "BackgroundBrush", out color))
        {
            var luminance = ((0.299 * color.R)
                + (0.587 * color.G)
                + (0.114 * color.B)) / 255.0;

            return luminance < 0.5;
        }

        return true;
    }
}
