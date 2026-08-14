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
/// Captures the set of visible top-level windows, so a launcher can record what existed before it
/// starts a process and a view can later tell a new window from a pre-existing one.
/// </summary>
/// <remarks>
/// This lives beside the launcher rather than in the view on purpose. Taking the baseline after
/// the process has started is a race: with single sign-on or a warm cache the session window can
/// already exist by then, and it is classified as pre-existing for the whole detection window -
/// the capture fails and the session falls back to an external window despite being embeddable.
/// </remarks>
internal static class VisibleWindowSnapshot
{
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    /// <summary>Handles of every visible top-level window at the moment of the call.</summary>
    /// <remarks>Returns an empty set rather than throwing: a failed baseline must not stop a launch.</remarks>
    internal static IReadOnlySet<nint> Capture()
    {
        HashSet<nint> visible = [];

        try
        {
            EnumWindows(
                (hwnd, _) =>
                {
                    if (IsWindowVisible(hwnd))
                    {
                        visible.Add(hwnd);
                    }

                    return true;
                },
                nint.Zero);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Visible-window baseline capture failed: {ex.Message}");
        }

        return visible;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);
}
