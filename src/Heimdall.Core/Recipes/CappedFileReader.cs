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

namespace Heimdall.Core.Recipes;

/// <summary>What one capped read of a file found.</summary>
/// <param name="Bytes">The whole content, or null when the file is larger than the cap.</param>
/// <param name="Length">
/// The size of the file as the opened handle reports it when it can, else one more than the cap. Only
/// meaningful when <paramref name="Bytes"/> is null.
/// </param>
public readonly record struct CappedRead(byte[]? Bytes, long Length)
{
    /// <summary>True when the file holds more than the cap.</summary>
    public bool TooLarge => Bytes is null;
}

/// <summary>
/// Reads a file that must not be larger than a cap, in one pass over one open handle: the size is not asked
/// first and the file read afterwards, so a file that grows in between cannot be read whole, and a file
/// over the cap is never read beyond one byte more than the cap.
/// </summary>
public static class CappedFileReader
{
    private const int ChunkBytes = 8192;

    /// <summary>Reads a file, or says it is larger than the cap.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cap">The most bytes the file may hold.</param>
    /// <exception cref="IOException">The file could not be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    /// <exception cref="NotSupportedException">The path is not a file that can be read.</exception>
    public static CappedRead Read(string path, int cap)
    {
        ArgumentNullException.ThrowIfNull(path);

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.SequentialScan);
        return Read(stream, cap);
    }

    /// <summary>Reads at most one byte more than the cap from a stream.</summary>
    /// <param name="stream">The opened stream.</param>
    /// <param name="cap">The most bytes the content may hold.</param>
    public static CappedRead Read(Stream stream, int cap)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(cap);

        long limit = (long)cap + 1;
        byte[] buffer = new byte[Math.Min(limit, ChunkBytes)];
        using MemoryStream content = new();
        while (content.Length < limit)
        {
            int wanted = (int)Math.Min(buffer.Length, limit - content.Length);
            int read = stream.Read(buffer, 0, wanted);
            if (read == 0)
                break;

            content.Write(buffer, 0, read);
        }

        if (content.Length <= cap)
            return new CappedRead(content.ToArray(), content.Length);

        long length = limit;
        try
        {
            if (stream.CanSeek)
                length = Math.Max(stream.Length, limit);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            // A handle that cannot say its length: one more than the cap is all that is known.
        }

        return new CappedRead(null, length);
    }
}
