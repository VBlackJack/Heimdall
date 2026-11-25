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
/// The Windows calls the recipe layer needs and no managed API offers: the final path of an
/// open file (every junction and link followed, parents included), the OEM code page, and the
/// conversion of a character to the ANSI code page with the best-fit mapping that a program
/// reading its command line would be served.
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

    /// <summary>The system ANSI code page (the one a program writes in when it is not told otherwise), or 0 off Windows.</summary>
    internal static int SystemAnsiCodePage() => OperatingSystem.IsWindows() ? (int)GetACP() : 0;

    /// <summary><c>CP_ACP</c>: the ANSI code page of the machine, the one a program reads its command line in.</summary>
    internal const uint AnsiCodePage = 0;

    /// <summary>Longest conversion of one character: a surrogate pair in a multi-byte code page.</summary>
    private const int ConvertedBufferBytes = 8;

    private const byte AsciiLimit = 0x80;

    private const byte QuestionMark = (byte)'?';

    /// <summary>
    /// True when one character converts, in the given code page and with Windows' default best-fit
    /// mapping, to a single ASCII byte other than <c>?</c>. No default-character pointer is passed:
    /// the call refuses one for some code pages, and the converted byte is all that is read.
    /// </summary>
    internal static bool BecomesAscii(string character, uint codePage)
    {
        if (!OperatingSystem.IsWindows() || character.Length == 0)
            return false;

        byte[] buffer = new byte[ConvertedBufferBytes];
        int written = WideCharToMultiByte(codePage, 0, character, character.Length, buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero);
        return written == 1 && buffer[0] < AsciiLimit && buffer[0] != QuestionMark;
    }

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

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int WideCharToMultiByte(
        uint codePage,
        uint flags,
        string text,
        int textLength,
        [Out] byte[] bytes,
        int byteLength,
        IntPtr defaultChar,
        IntPtr usedDefaultChar);
}
