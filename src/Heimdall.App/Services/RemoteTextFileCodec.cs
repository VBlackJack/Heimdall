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

namespace Heimdall.App.Services;

internal sealed record RemoteTextDocument(
    string Text,
    Encoding Encoding,
    ReadOnlyMemory<byte> Preamble);

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
        string text = detected.Encoding.GetString(
            bytes,
            detected.PreambleLength,
            bytes.Length - detected.PreambleLength);
        byte[] preamble = bytes[..detected.PreambleLength];
        return new RemoteTextDocument(text, detected.Encoding, preamble);
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
        await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);
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
