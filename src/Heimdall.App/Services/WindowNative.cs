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
}
