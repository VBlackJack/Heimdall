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

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Heimdall.App.Behaviors;

/// <summary>
/// Removes the Minimize and Maximize caption buttons from a dialog while keeping its icon, its
/// title, its close button and its resize border.
/// </summary>
/// <remarks>
/// <para>A modal dialog disables its owner. With a Minimize button it can be sent to the taskbar
/// while the owner stays disabled, which leaves the user in front of a main window that ignores
/// every click. <c>WindowStyle="ToolWindow"</c> removes both buttons but also the caption icon and
/// shrinks the title bar, and <c>ResizeMode="NoResize"</c> removes them only by also taking away
/// the resize border. WPF has no setting that drops the two boxes alone, so this clears
/// <c>WS_MINIMIZEBOX</c> and <c>WS_MAXIMIZEBOX</c> on the native window once it exists.</para>
/// <para>Clearing <c>WS_MAXIMIZEBOX</c> also stops a double-click on the title bar from maximizing
/// the dialog, and greys out the matching entries of the system menu.</para>
/// <para>The style is written once, on <see cref="Window.SourceInitialized"/>. WPF rewrites the
/// window style from its own cache when, for instance, <see cref="Window.ResizeMode"/> or
/// <see cref="Window.WindowStyle"/> change at run time, which would bring the boxes back; no dialog
/// carrying this property changes either after it is created.</para>
/// </remarks>
public static class DialogChromeBehavior
{
    private const int GwlStyle = -16;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsMinimizeBox = 0x00020000;

    public static readonly DependencyProperty HideCaptionButtonsProperty =
        DependencyProperty.RegisterAttached(
            "HideCaptionButtons",
            typeof(bool),
            typeof(DialogChromeBehavior),
            new PropertyMetadata(false, OnHideCaptionButtonsChanged));

    public static void SetHideCaptionButtons(DependencyObject element, bool value)
        => element.SetValue(HideCaptionButtonsProperty, value);

    public static bool GetHideCaptionButtons(DependencyObject element)
        => (bool)element.GetValue(HideCaptionButtonsProperty);

    /// <summary>The window style with the Minimize and Maximize boxes removed.</summary>
    /// <param name="style">A <c>GWL_STYLE</c> value.</param>
    internal static int WithoutCaptionButtons(int style)
        => style & ~(WsMinimizeBox | WsMaximizeBox);

    /// <summary>Whether a <c>GWL_STYLE</c> value still carries either caption box.</summary>
    /// <param name="style">A <c>GWL_STYLE</c> value.</param>
    internal static bool HasCaptionButtons(int style)
        => (style & (WsMinimizeBox | WsMaximizeBox)) != 0;

    private static void OnHideCaptionButtonsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Window window)
        {
            return;
        }

        window.SourceInitialized -= OnSourceInitialized;
        if (!(bool)e.NewValue)
        {
            return;
        }

        window.SourceInitialized += OnSourceInitialized;

        // A window whose handle already exists has raised SourceInitialized for good.
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ClearCaptionButtons(hwnd);
        }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window && GetHideCaptionButtons(window))
        {
            ClearCaptionButtons(new WindowInteropHelper(window).Handle);
        }
    }

    private static void ClearCaptionButtons(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int style = GetWindowLong(hwnd, GwlStyle);
        if (HasCaptionButtons(style))
        {
            _ = SetWindowLong(hwnd, GwlStyle, WithoutCaptionButtons(style));
        }
    }

    // GWL_STYLE is a 32-bit value on every architecture, so the 32-bit entry points are exact here.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
