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
using Microsoft.Win32.SafeHandles;

namespace Heimdall.Ssh.Pageant;

/// <summary>
/// Native Win32 interop methods for Pageant shared memory IPC.
/// Uses DllImport (not LibraryImport) to avoid requiring AllowUnsafeBlocks.
/// </summary>
internal static class NativeMethods
{
    internal static readonly IntPtr InvalidHandleValue = new(-1);
    internal const uint PAGE_READWRITE = 0x04;
    internal const uint FILE_MAP_ALL_ACCESS = 0xF001F;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        ref COPYDATASTRUCT lParam,
        SendMessageTimeoutFlags flags,
        uint timeoutMilliseconds,
        out UIntPtr result);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileMapping(
        IntPtr hFile,
        IntPtr lpAttributes,
        uint flProtect,
        uint dwMaxSizeHigh,
        uint dwMaxSizeLow,
        string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr MapViewOfFile(
        IntPtr hFileMappingObject,
        uint dwDesiredAccess,
        uint dwFileOffsetHigh,
        uint dwFileOffsetLow,
        UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [Flags]
    internal enum SendMessageTimeoutFlags : uint
    {
        Block = 0x0001,
        AbortIfHung = 0x0002,
        ErrorOnExit = 0x0020
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }
}
