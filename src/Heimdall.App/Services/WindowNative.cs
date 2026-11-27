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

namespace Heimdall.App.Services;

/// <summary>
/// The user32 window calls shared by the views and services of the application.
/// </summary>
internal static class WindowNative
{
    /// <summary>Activates and displays a window in its original size and position.</summary>
    public const int SwShowNormal = 1;

    /// <summary>Activates the window and displays it maximized.</summary>
    public const int SwMaximize = 3;

    /// <summary>Displays a window in its most recent size and position without activating it.</summary>
    public const int SwShowNoActivate = 4;

    /// <summary>Activates and displays a minimized or maximized window in its original position.</summary>
    public const int SwRestore = 9;

    /// <summary>Sets the show state of a window.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Index of the extended window style for <see cref="GetWindowLong"/> and <see cref="SetWindowLong"/>.</summary>
    public const int GwlExStyle = -20;

    /// <summary>Extended style: the window lets mouse input fall through to the windows below it.</summary>
    public const int WsExTransparent = 0x00000020;

    /// <summary>Extended style: the window never becomes the foreground window when clicked.</summary>
    public const int WsExNoActivate = 0x08000000;

    /// <summary>Z-order marker: place the window above all non-topmost windows only.</summary>
    public static readonly IntPtr HwndNoTopmost = new(-2);

    /// <summary>Keeps the current size.</summary>
    public const uint SwpNoSize = 0x0001;

    /// <summary>Keeps the current position.</summary>
    public const uint SwpNoMove = 0x0002;

    /// <summary>Does not activate the window.</summary>
    public const uint SwpNoActivate = 0x0010;

    /// <summary>Relationship for <see cref="GetWindow"/>: the window directly above in the z-order.</summary>
    public const uint GwHwndPrev = 3;

    /// <summary>Retrieves the window that has the given z-order relationship to a window.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>Changes the z-order and position of a window.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Reads a 32-bit value from the window information.</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    /// <summary>Writes a 32-bit value into the window information.</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Retrieves the window that has the keyboard focus on the calling thread.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    /// <summary>Tells whether a window is a child or descendant of another window.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    /// <summary>Gives the keyboard focus to a window of the calling thread.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetFocus(IntPtr hWnd);
}
