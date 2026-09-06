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
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class RemoteTextFileCodecTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoOpAndEditedSave_PreserveEncodingAndBomByteForByte(bool useUtf16)
    {
        Encoding encoding = useUtf16
            ? new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: true,
                throwOnInvalidBytes: true)
            : new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: true,
                throwOnInvalidBytes: true);
        string originalText = "déjà\r\nsecond line";
        byte[] originalBytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(originalText)];
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "HeimdallTests",
            Guid.NewGuid().ToString("N"));
        string testFile = Path.Combine(testDirectory, "remote.txt");
        Directory.CreateDirectory(testDirectory);

        try
        {
            await File.WriteAllBytesAsync(testFile, originalBytes);

            RemoteTextDocument document = await RemoteTextFileCodec.ReadAsync(testFile);
            await RemoteTextFileCodec.WriteAsync(testFile, document.Text, document);

            byte[] savedBytes = await File.ReadAllBytesAsync(testFile);
            Assert.Equal(originalText, document.Text);
            Assert.Equal(originalBytes, savedBytes);

            string editedText = originalText + "\r\nthird line";
            byte[] editedBytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(editedText)];
            await RemoteTextFileCodec.WriteAsync(testFile, editedText, document);

            Assert.Equal(editedBytes, await File.ReadAllBytesAsync(testFile));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <remarks>
    /// A file with no byte order mark that is not valid UTF-8 could not be opened at all. It is
    /// read as Latin-1, flagged, and written back as Latin-1: the bytes survive a no-op save.
    /// </remarks>
    [Fact]
    public async Task ReadAsync_BomlessInvalidUtf8_FallsBackToLatin1AndSaysSo()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "HeimdallTests",
            Guid.NewGuid().ToString("N"));
        string testFile = Path.Combine(testDirectory, "legacy.txt");
        Directory.CreateDirectory(testDirectory);

        try
        {
            byte[] latin1 = [0x63, 0x61, 0x66, 0xE9];
            await File.WriteAllBytesAsync(testFile, latin1);

            RemoteTextDocument document = await RemoteTextFileCodec.ReadAsync(testFile);

            Assert.True(document.DecodedWithFallback);
            Assert.Equal("caf\u00e9", document.Text);
            Assert.Same(Encoding.Latin1, document.Encoding);

            await RemoteTextFileCodec.WriteAsync(testFile, document.Text, document);

            Assert.Equal(latin1, await File.ReadAllBytesAsync(testFile));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <remarks>
    /// A file WITH a byte order mark that fails its own encoding is corruption, not a legacy
    /// encoding, and stays refused.
    /// </remarks>
    [Fact]
    public async Task ReadAsync_InvalidUtf16WithMark_StillThrows()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "HeimdallTests",
            Guid.NewGuid().ToString("N"));
        string testFile = Path.Combine(testDirectory, "corrupt.txt");
        Directory.CreateDirectory(testDirectory);

        try
        {
            // UTF-16 LE mark followed by a lone high surrogate.
            await File.WriteAllBytesAsync(testFile, [0xFF, 0xFE, 0x00, 0xD8]);

            await Assert.ThrowsAsync<DecoderFallbackException>(
                () => RemoteTextFileCodec.ReadAsync(testFile));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_LeavesNoStagingFileBehind()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "HeimdallTests",
            Guid.NewGuid().ToString("N"));
        string testFile = Path.Combine(testDirectory, "notes.txt");
        Directory.CreateDirectory(testDirectory);

        try
        {
            RemoteTextDocument document = new("v1", new UTF8Encoding(false), ReadOnlyMemory<byte>.Empty);
            await RemoteTextFileCodec.WriteAsync(testFile, "v2", document);

            Assert.Equal("v2", await File.ReadAllTextAsync(testFile));
            Assert.Equal([testFile], Directory.GetFiles(testDirectory));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }
}
