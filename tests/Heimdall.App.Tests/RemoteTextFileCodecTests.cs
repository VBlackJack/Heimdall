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

    [Fact]
    public async Task ReadAsync_BomlessInvalidUtf8_ThrowsDecoderFallbackException()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "HeimdallTests",
            Guid.NewGuid().ToString("N"));
        string testFile = Path.Combine(testDirectory, "legacy.txt");
        Directory.CreateDirectory(testDirectory);

        try
        {
            await File.WriteAllBytesAsync(testFile, [0xE9]);

            await Assert.ThrowsAsync<DecoderFallbackException>(
                () => RemoteTextFileCodec.ReadAsync(testFile));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }
}
