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

namespace Heimdall.Core.Recipes;

/// <summary>
/// The two Windows calls the recipe layer needs and no managed API offers: the final path of an
/// open file (every junction and link followed, parents included) and the OEM code page.
/// </summary>
internal static class RecipeProgramNative
{
    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>: the drive-letter form with links resolved.</summary>
    private const uint FinalPathNormalizedDos = 0x0;

    /// <summary>First buffer size, in characters; one retry is made with the size the call reports.</summary>
    private const int InitialPathBufferChars = 512;

    private const uint ShareAll = 0x7;

    private const uint OpenExisting = 3;

    /// <summary>Needed to open a folder; harmless for a file.</summary>
    private const uint BackupSemantics = 0x02000000;

    private const string ExtendedPathPrefix = @"\\?\";

    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// The final path of an existing file or folder, with every reparse point resolved (parents
    /// included) and the extended-length prefix removed when the result is a drive path; null when it
    /// cannot be resolved. The item is opened for attribute queries only, with full sharing, and is
    /// never executed.
    /// </summary>
    internal static string? ResolveFinalPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        using SafeFileHandle handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        char[] buffer = new char[InitialPathBufferChars];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FinalPathNormalizedDos);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FinalPathNormalizedDos);
        }

        return length == 0 || length > buffer.Length
            ? null
            : StripExtendedPrefix(new string(buffer, 0, (int)length));
    }

    /// <summary>
    /// Removes <c>\\?\</c> from a drive path. A <c>\\?\UNC\</c> path is turned into the plain UNC
    /// form so that it can never be mistaken for a drive path by a later prefix test.
    /// </summary>
    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[ExtendedUncPrefix.Length..];

        return path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal)
            ? path[ExtendedPathPrefix.Length..]
            : path;
    }

    /// <summary>The OEM code page of the machine (the code page of a console child), or 0 off Windows.</summary>
    internal static int OemCodePage() => OperatingSystem.IsWindows() ? (int)GetOEMCP() : 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle handle,
        [Out] char[] path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
