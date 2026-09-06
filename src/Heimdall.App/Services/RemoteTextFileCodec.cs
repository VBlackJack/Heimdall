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
using System.Text;
using Heimdall.Sftp;

namespace Heimdall.App.Services;

internal sealed record RemoteTextDocument(
    string Text,
    Encoding Encoding,
    ReadOnlyMemory<byte> Preamble)
{
    /// <summary>
    /// True when the bytes were not valid UTF-8 and were read as Latin-1 instead. The editor says
    /// so, and the save writes Latin-1 back.
    /// </summary>
    public bool DecodedWithFallback { get; init; }
}

internal static class RemoteTextFileCodec
{
    private static readonly Encoding Utf32LittleEndian = new UTF32Encoding(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidCharacters: true);

    private static readonly Encoding Utf32BigEndian = new UTF32Encoding(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidCharacters: true);

    private static readonly Encoding Utf8Bom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: true,
        throwOnInvalidBytes: true);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding Utf16LittleEndian = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidBytes: true);

    private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidBytes: true);

    private static readonly Encoding[] BomEncodings =
    [
        Utf32LittleEndian,
        Utf32BigEndian,
        Utf8Bom,
        Utf16LittleEndian,
        Utf16BigEndian
    ];

    internal static async Task<RemoteTextDocument> ReadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        (Encoding Encoding, int PreambleLength) detected = DetectEncoding(bytes);
        string text;
        bool decodedWithFallback = false;
        try
        {
            text = detected.Encoding.GetString(
                bytes,
                detected.PreambleLength,
                bytes.Length - detected.PreambleLength);
        }
        catch (DecoderFallbackException) when (detected.PreambleLength == 0)
        {
            // No byte order mark and not valid UTF-8: every Latin-1 configuration file with an
            // accent in it. Latin-1 maps every byte, so the file opens; it is written back as
            // Latin-1, and the editor says so. A file WITH a mark that fails its own encoding is
            // still refused: that is corruption, not a legacy encoding.
            text = Encoding.Latin1.GetString(bytes);
            decodedWithFallback = true;
        }

        byte[] preamble = bytes[..detected.PreambleLength];
        Encoding encoding = decodedWithFallback ? Encoding.Latin1 : detected.Encoding;
        return new RemoteTextDocument(text, encoding, preamble) { DecodedWithFallback = decodedWithFallback };
    }

    internal static async Task WriteAsync(
        string filePath,
        string text,
        RemoteTextDocument document,
        CancellationToken cancellationToken = default)
    {
        byte[] content = document.Encoding.GetBytes(text);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(
            document.Preamble.Length + content.Length);
        document.Preamble.Span.CopyTo(bytes);
        content.CopyTo(bytes, document.Preamble.Length);

        // Staged beside the destination and published by a rename: a direct write that died
        // part way left the user's file truncated.
        string tempPath = AtomicLocalFile.CreateTempPath(filePath);
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            AtomicLocalFile.Commit(tempPath, filePath);
        }
        catch
        {
            AtomicLocalFile.Rollback(tempPath);
            throw;
        }
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        foreach (Encoding encoding in BomEncodings)
        {
            byte[] preamble = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(preamble))
            {
                return (encoding, preamble.Length);
            }
        }

        return (Utf8NoBom, 0);
    }
}
