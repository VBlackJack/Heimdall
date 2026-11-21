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

using System.Text;
using System.Text.RegularExpressions;
using Heimdall.Core.LogFiles;
using Heimdall.Core.Matching;

namespace Heimdall.Core.Tests;

public sealed class LogFileScannerTests : IDisposable
{
    private const int CodePageWindows1252 = 1252;
    private const int TotalLineCount = 12000;
    private const int HugeLineChars = 10 * 1024 * 1024;
    private const int ReadBlockBytes = 64 * 1024;
    private const int BoundaryLineCount = 6000;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly LogFilter NoFilter = new(null);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "heimdall-logscan-" + Guid.NewGuid().ToString("N"));

    public LogFileScannerTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private string NewPath(string name = "app.log") => Path.Combine(_dir, name);

    private static string[] Texts(LogScanResult result) => result.Lines.Select(l => l.Text).ToArray();

    private static LogFilter Filter(string pattern, bool caseSensitive = false, TimeSpan? timeout = null)
    {
        var options = RegexOptions.CultureInvariant;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new LogFilter(new Regex(pattern, options, timeout ?? RegexEngine.DefaultTimeout));
    }

    [Fact]
    public void ReadInitial_KeepsLastMaxLinesAndCountsAllLines()
    {
        var path = NewPath();
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, TotalLineCount).Select(i => $"line {i}\n")));

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.Equal(LogFileScanner.MaxLines, result.Lines.Count);
        Assert.Equal("line " + (TotalLineCount - LogFileScanner.MaxLines), result.Lines[0].Text);
        Assert.Equal("line " + (TotalLineCount - 1), result.Lines[^1].Text);
        Assert.Equal(TotalLineCount, result.TotalLines);
        Assert.True(result.Overflowed);
        Assert.Null(result.PendingLine);
    }

    [Fact]
    public void ReadInitial_TrailingNewlineProducesNoEmptyLine()
    {
        var path = NewPath();
        File.WriteAllText(path, "a\nb\n");

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.Equal(new[] { "a", "b" }, Texts(result));
        Assert.Equal(2, result.TotalLines);
        Assert.Null(result.PendingLine);
    }

    [Fact]
    public void ReadInitial_StripsCarriageReturns()
    {
        var path = NewPath();
        File.WriteAllText(path, "one\r\ntwo\r\n");

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.Equal(new[] { "one", "two" }, Texts(result));
    }

    [Fact]
    public void ReadAppended_CompletesPartialLineWithoutDuplicating()
    {
        var path = NewPath();
        File.WriteAllText(path, "first\nsec");
        var initial = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.Equal(new[] { "first" }, Texts(initial));
        Assert.Equal("sec", initial.PendingLine!.Text);

        File.AppendAllText(path, "ond\nthird\n");
        var appended = LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter);

        Assert.Equal(LogScanStatus.Ok, appended.Status);
        Assert.Equal(new[] { "second", "third" }, Texts(appended));
        Assert.Null(appended.PendingLine);
        Assert.Equal(3, appended.TotalLines);
    }

    [Fact]
    public void ReadAppended_CrLfSplitAcrossReadsKeepsOneLine()
    {
        var path = NewPath();
        File.WriteAllBytes(path, "alpha\r"u8.ToArray());
        var initial = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.Equal("alpha", initial.PendingLine!.Text);

        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.Write("\nbeta\n"u8);
        }

        var appended = LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter);

        Assert.Equal(new[] { "alpha", "beta" }, Texts(appended));
    }

    [Fact]
    public void ReadAppended_Utf8CharacterSplitBetweenCallsIsDecodedOnce()
    {
        var path = NewPath();
        var bytes = Encoding.UTF8.GetBytes("café €\n");
        // Cut inside the three-byte euro sign.
        var cut = Array.IndexOf(bytes, (byte)0xE2) + 1;
        File.WriteAllBytes(path, bytes[..cut]);
        var initial = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.Write(bytes, cut, bytes.Length - cut);
        }

        var appended = LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter);

        Assert.Equal(new[] { "café €" }, Texts(appended));
    }

    [Fact]
    public void ReadAppended_Utf16CodeUnitSplitBetweenCallsIsDecodedOnce()
    {
        var path = NewPath();
        var bytes = Encoding.Unicode.GetBytes("ab\ncd\n");
        // Three whole code units, then one byte of the fourth.
        var cut = (2 * sizeof(char)) + 1 + sizeof(char);
        File.WriteAllBytes(path, bytes[..cut]);
        var initial = LogFileScanner.ReadInitial(path, Encoding.Unicode, NoFilter);

        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.Write(bytes, cut, bytes.Length - cut);
        }

        var appended = LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter);

        Assert.Equal(new[] { "ab", "cd" }, Texts(initial).Concat(Texts(appended)).ToArray());
    }

    [Fact]
    public void ReadInitial_MultibyteCharacterAcrossReadBlockBoundaryIsIntact()
    {
        var path = NewPath();
        // Lines of eleven bytes put the first byte of an accented character on the block boundary.
        const string line = "xxxxxxxxé";
        File.WriteAllText(path, string.Concat(Enumerable.Repeat(line + "\n", BoundaryLineCount)), new UTF8Encoding(false));

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.Equal(BoundaryLineCount, result.Lines.Count);
        Assert.All(result.Lines, l => Assert.Equal(line, l.Text));
    }

    [Fact]
    public void ReadInitial_Utf8BomIsNotEmitted()
    {
        var path = NewPath();
        File.WriteAllText(path, "hello\n", new UTF8Encoding(true));

        var result = LogFileScanner.ReadInitial(path, Encoding.ASCII, NoFilter);

        Assert.Equal(new[] { "hello" }, Texts(result));
    }

    [Fact]
    public void ReadInitial_Utf16BomSelectsEncodingAndIsNotEmitted()
    {
        var path = NewPath();
        File.WriteAllText(path, "wide\n", Encoding.Unicode);

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.Equal(new[] { "wide" }, Texts(result));
    }

    [Fact]
    public void Reset_AfterTruncation_RestartsWithoutBom()
    {
        var path = NewPath();
        File.WriteAllText(path, "a long first line of the original file\nsecond\n", new UTF8Encoding(true));
        var initial = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        File.WriteAllText(path, "new\n", new UTF8Encoding(true));
        var reset = LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter);

        Assert.Equal(LogScanStatus.Reset, reset.Status);
        Assert.Equal(new[] { "new" }, Texts(reset));
        Assert.Equal(1, reset.TotalLines);
    }

    [Fact]
    public void Reset_AfterRenameAndRecreateWithLongerFile_IsDetectedByHeadFingerprint()
    {
        var path = NewPath();
        File.WriteAllText(path, "old-1\nold-2\n");
        var initial = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        File.Move(path, NewPath("app.log.1"));
        File.WriteAllText(path, "fresh-1\nfresh-2\nfresh-3\nfresh-4\n");
        var result = LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter);

        Assert.Equal(LogScanStatus.Reset, result.Status);
        Assert.Equal(new[] { "fresh-1", "fresh-2", "fresh-3", "fresh-4" }, Texts(result));
    }

    [Fact]
    public void ReadAppended_UnchangedFile_ReturnsNothingAndKeepsPosition()
    {
        var path = NewPath();
        File.WriteAllText(path, "a\nb\n");
        var initial = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        var again = LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter);

        Assert.Equal(LogScanStatus.Ok, again.Status);
        Assert.Empty(again.Lines);
        Assert.Equal(initial.Cursor!.Position, again.Cursor!.Position);
    }

    [Fact]
    public void ReadInitial_HugeSingleLineIsCappedAndFlagged()
    {
        var path = NewPath();
        File.WriteAllText(path, new string('z', HugeLineChars) + "\nshort\n");

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(LogFileScanner.MaxLineChars, result.Lines[0].Text.Length);
        Assert.True(result.Lines[0].Truncated);
        Assert.Equal("short", result.Lines[1].Text);
        Assert.False(result.Lines[1].Truncated);
    }

    [Fact]
    public void ReadInitial_CatastrophicPatternReportsFilterTimeout()
    {
        var path = NewPath();
        File.WriteAllText(path, new string('a', 40) + "!\n");
        var filter = Filter("^(a+)+$", timeout: ShortTimeout);

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, filter);

        Assert.Equal(LogScanStatus.FilterTimeout, result.Status);
        Assert.Null(result.Cursor);
    }

    [Fact]
    public void ReadInitial_CancelledToken_Throws()
    {
        var path = NewPath();
        File.WriteAllText(path, "a\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter, cts.Token));
    }

    [Fact]
    public void ReadInitial_CancellationIsObservedMidScan()
    {
        var path = NewPath();
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, TotalLineCount).Select(i => $"row {i}\n")));
        using var cts = new CancellationTokenSource();
        // The stream cancels the token after the first full block is read, before the second block.
        var filter = new LogFilter(new Regex("row", RegexOptions.CultureInvariant, RegexEngine.DefaultTimeout));
        using var stream = new CancelAfterFirstReadStream(path, cts);

        Assert.ThrowsAny<OperationCanceledException>(
            () => LogFileScanner.Scan(stream, null, Encoding.UTF8, filter, LogFileScanner.MaxLines, cts.Token));
    }

    [Fact]
    public void ReadInitial_HighlightsAreCaseInsensitiveAndOrdered()
    {
        var path = NewPath();
        File.WriteAllText(path, "Error one, error two\nnothing here\n");

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, Filter("error"));

        var line = Assert.Single(result.Lines);
        Assert.Equal(new[] { new LogHighlight(0, 5), new LogHighlight(11, 5) }, line.Highlights);
        Assert.Equal(2, result.TotalLines);
    }

    [Fact]
    public void ReadInitial_CaseSensitiveFilterDoesNotMatchOtherCase()
    {
        var path = NewPath();
        File.WriteAllText(path, "Error\nerror\n");

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, Filter("error", caseSensitive: true));

        Assert.Equal(new[] { "error" }, Texts(result));
    }

    [Fact]
    public void ReadInitial_ZeroLengthMatchesAreKeptAsLinesButNotHighlighted()
    {
        var path = NewPath();
        File.WriteAllText(path, "abc\n");

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, Filter("^"));

        var line = Assert.Single(result.Lines);
        Assert.Empty(line.Highlights);
    }

    [Fact]
    public void ReadAppended_PendingLineIsFilteredToo()
    {
        var path = NewPath();
        File.WriteAllText(path, "keep one\nskip");
        var filter = Filter("keep");
        var initial = LogFileScanner.ReadInitial(path, Encoding.UTF8, filter);

        Assert.Equal(new[] { "keep one" }, Texts(initial));
        Assert.Null(initial.PendingLine);

        File.AppendAllText(path, "ped keep\n");
        var appended = LogFileScanner.ReadAppended(path, initial.Cursor!, filter);

        Assert.Equal(new[] { "skipped keep" }, Texts(appended));
        Assert.Equal(new[] { new LogHighlight(8, 4) }, appended.Lines[0].Highlights);
    }

    [Fact]
    public void ReadAppended_CancelledScan_InvalidatesCursorAndNextCallResets()
    {
        var path = NewPath();
        File.WriteAllText(path, "a\nb\n");
        var initial = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter, cts.Token));
        var next = LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter);

        Assert.Equal(LogScanStatus.Reset, next.Status);
        Assert.Equal(new[] { "a", "b" }, Texts(next));
    }

    [Fact]
    public void ReadAppended_FilterTimeout_ReturnsNullCursorAndInvalidatesOldOne()
    {
        var path = NewPath();
        File.WriteAllText(path, "ok\n");
        var initial = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);
        File.AppendAllText(path, new string('a', 40) + "!\n");

        var timedOut = LogFileScanner.ReadAppended(path, initial.Cursor!, Filter("^(a+)+$", timeout: ShortTimeout));

        Assert.Equal(LogScanStatus.FilterTimeout, timedOut.Status);
        Assert.Null(timedOut.Cursor);
        var next = LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter);
        Assert.Equal(LogScanStatus.Reset, next.Status);
        Assert.Equal(2, next.Lines.Count);
    }

    [Fact]
    public void ReadAppended_TruncatedPendingLineKeepsCapAcrossAppends()
    {
        var path = NewPath();
        File.WriteAllText(path, new string('a', 3000));
        var first = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);
        File.AppendAllText(path, new string('b', 3000));
        var second = LogFileScanner.ReadAppended(path, first.Cursor!, NoFilter);

        Assert.True(second.PendingLine!.Truncated);
        Assert.Equal(LogFileScanner.MaxLineChars, second.PendingLine.Text.Length);

        File.AppendAllText(path, "tail\n");
        var third = LogFileScanner.ReadAppended(path, second.Cursor!, NoFilter);

        var line = Assert.Single(third.Lines);
        Assert.True(line.Truncated);
        Assert.Equal(LogFileScanner.MaxLineChars, line.Text.Length);
    }

    [Fact]
    public void ReadInitial_CapSizedLineWithCrLfIsNotTruncated()
    {
        var path = NewPath();
        File.WriteAllText(path, new string('c', LogFileScanner.MaxLineChars) + "\r\nnext\r\n");

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.False(result.Lines[0].Truncated);
        Assert.Equal(LogFileScanner.MaxLineChars, result.Lines[0].Text.Length);
    }

    [Fact]
    public void ReadAppended_CapSizedLineWithCrLfSplitAcrossReadsIsNotTruncated()
    {
        var path = NewPath();
        File.WriteAllText(path, new string('c', LogFileScanner.MaxLineChars) + "\r");
        var first = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);
        File.AppendAllText(path, "\n");

        var second = LogFileScanner.ReadAppended(path, first.Cursor!, NoFilter);

        var line = Assert.Single(second.Lines);
        Assert.False(line.Truncated);
        Assert.Equal(LogFileScanner.MaxLineChars, line.Text.Length);
    }

    [Fact]
    public void ReadAppended_CapSizedLineContinuedAfterCarriageReturnIsTruncated()
    {
        var path = NewPath();
        File.WriteAllText(path, new string('c', LogFileScanner.MaxLineChars) + "\r");
        var first = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);
        File.AppendAllText(path, "more\n");

        var second = LogFileScanner.ReadAppended(path, first.Cursor!, NoFilter);

        var line = Assert.Single(second.Lines);
        Assert.True(line.Truncated);
    }

    [Fact]
    public void ReadInitial_UnderTheCap_IsNotOverflowed()
    {
        var path = NewPath();
        File.WriteAllText(path, "a\nb\nc\n");

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.False(result.Overflowed);
    }

    [Fact]
    public void ReadAppended_OverflowInsideOneChunk_KeepsLastMaxLines()
    {
        var path = NewPath();
        File.WriteAllText(path, "first\n");
        var initial = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);
        File.AppendAllText(path, string.Concat(Enumerable.Range(0, TotalLineCount).Select(i => $"n{i}\n")));

        var appended = LogFileScanner.ReadAppended(path, initial.Cursor!, NoFilter);

        Assert.Equal(LogScanStatus.Ok, appended.Status);
        Assert.True(appended.Overflowed);
        Assert.Equal(LogFileScanner.MaxLines, appended.Lines.Count);
        Assert.Equal("n" + (TotalLineCount - 1), appended.Lines[^1].Text);
        Assert.Equal(TotalLineCount + 1, appended.TotalLines);
    }

    [Fact]
    public void ReadInitial_WorksWhileWriterHoldsFileOpen()
    {
        var path = NewPath();
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("live line\n"u8);
        writer.Flush();

        var result = LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        Assert.Equal(new[] { "live line" }, Texts(result));
    }

    [Fact]
    public void ReadInitial_DoesNotKeepFileHandle()
    {
        var path = NewPath();
        File.WriteAllText(path, "x\n");

        LogFileScanner.ReadInitial(path, Encoding.UTF8, NoFilter);

        // FileShare.None fails if any handle is still open on the file.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public void ReadInitial_Windows1252DecodesAccents()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(CodePageWindows1252);
        var path = NewPath();
        File.WriteAllBytes(path, encoding.GetBytes("café naïve\n"));

        var result = LogFileScanner.ReadInitial(path, encoding, NoFilter);

        Assert.Equal(new[] { "café naïve" }, Texts(result));
    }

    /// <summary>Stream over a file that cancels the token as soon as a full block was read.</summary>
    private sealed class CancelAfterFirstReadStream : FileStream
    {
        private readonly CancellationTokenSource _cts;

        public CancelAfterFirstReadStream(string path, CancellationTokenSource cts)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        {
            _cts = cts;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            if (count == ReadBlockBytes)
            {
                _cts.Cancel();
            }

            return read;
        }
    }
}
