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
using System.Runtime.Versioning;

namespace Heimdall.App.Services;

/// <summary>
/// System-wide idle measurement via Win32 <c>GetLastInputInfo</c>. Unlike a WPF
/// per-window input watcher, this is focus-independent: it reports the true
/// last-input tick even while a terminal/RDP/VNC (WebView2/ActiveX) surface owns
/// focus and swallows WPF input, so the idle auto-lock never false-fires while
/// the user is typing inside a session.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SystemIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    /// <summary>
    /// The time since the last system-wide keyboard or mouse input.
    /// </summary>
    /// <returns>The idle duration, or <see cref="TimeSpan.Zero"/> if it cannot be measured.</returns>
    public static TimeSpan GetIdleDuration()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both Environment.TickCount and dwTime are unsigned 32-bit millisecond
        // counters that wrap roughly every 49.7 days; unchecked subtraction yields
        // the correct elapsed value across a wrap.
        var idleMilliseconds = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }
}
