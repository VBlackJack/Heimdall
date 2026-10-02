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

using System.IO;

namespace Heimdall.App.Services;

/// <summary>
/// Tells a binary file from a text file by its first bytes, so a file that is not text is not
/// opened in a text editor.
/// </summary>
/// <remarks>
/// A NUL byte is the signal: text encodings used on a server do not produce one, while archives,
/// images and programs do within their first kilobytes. UTF-16 and UTF-32 text is full of NUL
/// bytes by construction, so a file that opens with one of their byte-order marks is text. The
/// check is a heuristic by design and only ever steers a double-click towards "download"; the
/// explicit "Edit" command never consults it.
/// </remarks>
public static class BinaryFileSniffer
{
    /// <summary>How many leading bytes are inspected.</summary>
    public const int SniffLength = 8192;

    private static readonly byte[][] TextByteOrderMarks =
    [
        [0xFF, 0xFE, 0x00, 0x00],
        [0x00, 0x00, 0xFE, 0xFF],
        [0xFF, 0xFE],
        [0xFE, 0xFF],
    ];

    /// <summary>Whether the leading bytes of a file look like binary content.</summary>
    /// <param name="leadingBytes">The first bytes of the file (at most <see cref="SniffLength"/> are read).</param>
    public static bool LooksBinary(ReadOnlySpan<byte> leadingBytes)
    {
        if (leadingBytes.IsEmpty)
        {
            return false;
        }

        foreach (byte[] mark in TextByteOrderMarks)
        {
            if (leadingBytes.StartsWith(mark))
            {
                return false;
            }
        }

        ReadOnlySpan<byte> inspected = leadingBytes.Length > SniffLength
            ? leadingBytes[..SniffLength]
            : leadingBytes;
        return inspected.Contains((byte)0);
    }

    /// <summary>Whether the file at <paramref name="path"/> looks binary.</summary>
    public static bool LooksBinary(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] buffer = new byte[SniffLength];
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = stream.Read(buffer, 0, buffer.Length);
        return LooksBinary(buffer.AsSpan(0, read));
    }
}
