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

using System.Globalization;
using System.IO;
using Heimdall.App.Converters;
using Heimdall.App.Services;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

/// <summary>The small pieces behind the SFTP browser: the binary sniffer, the state store, the date format and the progress mailbox.</summary>
public sealed class SftpBrowserSupportTests
{
    // ------------------------------------------------------------------
    // Binary files
    // ------------------------------------------------------------------

    [Fact]
    public void LooksBinary_ANulByteMeansBinary()
    {
        Assert.True(BinaryFileSniffer.LooksBinary(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x01 }));
    }

    [Fact]
    public void LooksBinary_PlainTextAndTheEmptyFileAreText()
    {
        Assert.False(BinaryFileSniffer.LooksBinary("hello\nworld\n"u8));
        Assert.False(BinaryFileSniffer.LooksBinary(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x41 })]
    public void LooksBinary_Utf16AndUtf32TextWithAByteOrderMarkIsText(byte[] content)
    {
        Assert.False(BinaryFileSniffer.LooksBinary(content));
    }

    [Fact]
    public void LooksBinary_OnlyTheLeadingBytesAreInspected()
    {
        byte[] content = new byte[BinaryFileSniffer.SniffLength + 10];
        Array.Fill(content, (byte)'a');
        content[BinaryFileSniffer.SniffLength + 5] = 0;

        Assert.False(BinaryFileSniffer.LooksBinary(content));

        content[BinaryFileSniffer.SniffLength - 1] = 0;

        Assert.True(BinaryFileSniffer.LooksBinary(content));
    }

    [Fact]
    public void LooksBinary_ReadsAFileFromDisk()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string text = Path.Combine(scratch.Path, "note.txt");
        string binary = Path.Combine(scratch.Path, "data.bin");
        File.WriteAllText(text, "just text");
        File.WriteAllBytes(binary, [1, 2, 0, 3]);

        Assert.False(BinaryFileSniffer.LooksBinary(text));
        Assert.True(BinaryFileSniffer.LooksBinary(binary));
    }

    // ------------------------------------------------------------------
    // State store
    // ------------------------------------------------------------------

    [Fact]
    public void StateStore_RoundTripsBookmarksPerServerAndTheLastDownloadFolder()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        SftpBrowserStateStore store = new(scratch.Path);

        store.SaveBookmarks("a:22:u", ["/x", "/y"]);
        store.SaveBookmarks("b:22:u", ["/z"]);
        store.SaveLastDownloadFolder(@"C:\Downloads");

        SftpBrowserStateStore reopened = new(scratch.Path);
        Assert.Equal(["/x", "/y"], reopened.LoadBookmarks("a:22:u"));
        Assert.Equal(["/z"], reopened.LoadBookmarks("b:22:u"));
        Assert.Empty(reopened.LoadBookmarks("c:22:u"));
        Assert.Equal(@"C:\Downloads", reopened.LoadLastDownloadFolder());
    }

    [Fact]
    public void StateStore_SavingNoBookmarksForgetsTheServer()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        SftpBrowserStateStore store = new(scratch.Path);
        store.SaveBookmarks("a:22:u", ["/x"]);

        store.SaveBookmarks("a:22:u", []);

        Assert.Empty(new SftpBrowserStateStore(scratch.Path).LoadBookmarks("a:22:u"));
    }

    [Fact]
    public void StateStore_ACorruptFileReadsAsEmptyAndIsReplacedByTheNextSave()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        File.WriteAllText(Path.Combine(scratch.Path, "sftp-browser-state.json"), "{ not json");
        SftpBrowserStateStore store = new(scratch.Path);

        Assert.Empty(store.LoadBookmarks("a:22:u"));
        Assert.Null(store.LoadLastDownloadFolder());

        store.SaveBookmarks("a:22:u", ["/x"]);

        Assert.Equal(["/x"], new SftpBrowserStateStore(scratch.Path).LoadBookmarks("a:22:u"));
    }

    [Fact]
    public void StateStore_CreatesItsDirectoryAndLeavesNoTemporaryFileBehind()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string nested = Path.Combine(scratch.Path, "deeper", "still");
        SftpBrowserStateStore store = new(nested);

        store.SaveLastDownloadFolder(@"D:\Out");

        Assert.Equal(["sftp-browser-state.json"], Directory.GetFiles(nested).Select(Path.GetFileName));
    }

    [Fact]
    public void StateStore_HasNoParameterlessConstructor_SoATestCannotReachTheOperatorsOwnFile()
    {
        Assert.DoesNotContain(
            typeof(SftpBrowserStateStore).GetConstructors(),
            constructor => constructor.GetParameters().Length == 0);
    }

    // ------------------------------------------------------------------
    // Date format
    // ------------------------------------------------------------------

    [Fact]
    public void FileDateTimeConverter_FormatsInTheCurrentRegionalFormat()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            DateTime date = new(2026, 3, 9, 14, 5, 7, DateTimeKind.Unspecified);
            FileDateTimeConverter converter = new();

            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            string french = (string)converter.Convert(date, typeof(string), string.Empty, CultureInfo.InvariantCulture);
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            string american = (string)converter.Convert(date, typeof(string), string.Empty, CultureInfo.InvariantCulture);
            string withSeconds = (string)converter.Convert(
                date,
                typeof(string),
                FileDateTimeConverter.LongPatternParameter,
                CultureInfo.InvariantCulture);

            Assert.Equal(date.ToString("g", new CultureInfo("fr-FR")), french);
            Assert.Equal(date.ToString("g", new CultureInfo("en-US")), american);
            Assert.NotEqual(french, american);
            Assert.Equal(date.ToString("G", new CultureInfo("en-US")), withSeconds);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void FileDateTimeConverter_ANonDateIsAnEmptyCell()
    {
        Assert.Equal(string.Empty, new FileDateTimeConverter().Convert("x", typeof(string), string.Empty, CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------
    // Progress mailbox
    // ------------------------------------------------------------------

    [Fact]
    public void ProgressCoalescer_AFloodOfEventsLeavesOnlyTheNewest()
    {
        SftpProgressCoalescer coalescer = new();
        for (int index = 1; index <= 1000; index++)
        {
            coalescer.Post(new SftpTransferProgress("f", index, 1000, true));
        }

        Assert.True(coalescer.HasPending);
        SftpTransferProgress? taken = coalescer.Take();

        Assert.Equal(1000, taken!.BytesTransferred);
        Assert.Null(coalescer.Take());
        Assert.False(coalescer.HasPending);
    }

    [Fact]
    public async Task ProgressCoalescer_EventsPostedFromManyThreadsNeverCorruptTheMailbox()
    {
        SftpProgressCoalescer coalescer = new();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int index = 0; index < 5000; index++)
            {
                coalescer.Post(new SftpTransferProgress("f", (worker * 5000) + index, 100_000, false));
                _ = coalescer.Take();
            }
        })));

        Assert.False(coalescer.HasPending);
    }
}
