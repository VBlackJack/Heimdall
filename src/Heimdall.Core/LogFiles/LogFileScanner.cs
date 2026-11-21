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

namespace Heimdall.Core.LogFiles;

/// <summary>
/// Incremental, bounded log file reader: opens the file per call without keeping a handle, decodes
/// it in blocks, keeps only the last <see cref="MaxLines"/> matching lines and caps each line.
/// </summary>
public static class LogFileScanner
{
    public const int MaxLines = 10000;
    public const int MaxLineChars = 4096;

    private const int ReadBlockBytes = 64 * 1024;
    private const int HeadFingerprintBytes = 1024;
    private const int LineCancelCheckInterval = 1024;
    private const int Utf8BomLength = 3;
    private const int Utf16BomLength = 2;
    private const byte Utf8Bom0 = 0xEF;
    private const byte Utf8Bom1 = 0xBB;
    private const byte Utf8Bom2 = 0xBF;
    private const byte Utf16LeBom0 = 0xFF;
    private const byte Utf16LeBom1 = 0xFE;
    private const byte Utf16BeBom0 = 0xFE;
    private const byte Utf16BeBom1 = 0xFF;

    private static readonly IReadOnlyList<LogHighlight> NoHighlights = [];

    /// <summary>Reads the last matching lines of the whole file.</summary>
    public static LogScanResult ReadInitial(string path, Encoding encoding, LogFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(filter);

        using var stream = Open(path);
        return Finish(stream, Scan(stream, null, encoding, filter, MaxLines, ct));
    }

    /// <summary>Reads what was appended since <paramref name="cursor"/>, or restarts if the file was replaced.</summary>
    public static LogScanResult ReadAppended(string path, LogCursor cursor, LogFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(filter);

        using var stream = Open(path);
        try
        {
            var result = Scan(stream, cursor, cursor.FallbackEncoding, filter, MaxLines, ct);
            if (result.Status == LogScanStatus.FilterTimeout)
            {
                cursor.Invalid = true;
            }

            return Finish(stream, result);
        }
        catch
        {
            // The decoder was advanced by a scan that did not complete: never reuse this cursor.
            cursor.Invalid = true;
            throw;
        }
    }

    internal static LogScanResult Scan(
        Stream stream,
        LogCursor? cursor,
        Encoding fallbackEncoding,
        LogFilter filter,
        int maxLines,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var end = stream.Length;
        var status = LogScanStatus.Ok;
        var fallback = cursor?.FallbackEncoding ?? fallbackEncoding;

        if (cursor is not null
            && (cursor.Invalid || end < cursor.Position || !HeadMatches(stream, cursor.Head)))
        {
            status = LogScanStatus.Reset;
            cursor = null;
        }
        else if (cursor is { Position: 0 })
        {
            // Nothing consumed yet: the byte order mark has not been seen.
            cursor = null;
        }

        Encoding encoding;
        Decoder decoder;
        long position;
        var pending = string.Empty;
        var pendingTruncated = false;
        long totalLines = 0;

        if (cursor is null)
        {
            (encoding, position) = DetectEncoding(stream, end, fallback);
            decoder = encoding.GetDecoder();
        }
        else
        {
            encoding = cursor.Encoding;
            decoder = cursor.Decoder;
            position = cursor.Position;
            pending = cursor.PendingText;
            pendingTruncated = cursor.PendingTruncated;
            totalLines = cursor.TotalLines;
        }

        var assembler = new LineAssembler(filter, maxLines, pending, pendingTruncated, totalLines, ct);

        try
        {
            position = ReadBlocks(stream, position, end, encoding, decoder, assembler, ct);
            var lines = assembler.BuildLines();
            var pendingLine = assembler.BuildPendingLine();
            var newCursor = new LogCursor(
                position,
                fallback,
                encoding,
                decoder,
                ReadHead(stream, Math.Min(HeadFingerprintBytes, position)),
                assembler.PendingText,
                assembler.PendingTruncated,
                assembler.TotalLines);

            return new LogScanResult(
                status, lines, pendingLine, assembler.Overflowed, assembler.TotalLines, end, default, newCursor);
        }
        catch (RegexMatchTimeoutException)
        {
            return new LogScanResult(
                LogScanStatus.FilterTimeout, [], null, false, assembler.TotalLines, end, default, null);
        }
    }

    private static long ReadBlocks(
        Stream stream,
        long position,
        long end,
        Encoding encoding,
        Decoder decoder,
        LineAssembler assembler,
        CancellationToken ct)
    {
        var buffer = new byte[ReadBlockBytes];
        var chars = new char[encoding.GetMaxCharCount(ReadBlockBytes)];
        stream.Position = position;

        while (position < end)
        {
            ct.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(buffer.Length, end - position);
            var read = stream.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            position += read;
            var count = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
            assembler.Feed(chars.AsSpan(0, count));
        }

        return position;
    }

    private static (Encoding Encoding, long PreambleLength) DetectEncoding(Stream stream, long length, Encoding fallback)
    {
        var size = (int)Math.Min(Utf8BomLength, length);
        if (size < Utf16BomLength)
        {
            return (fallback, 0);
        }

        var bom = new byte[size];
        stream.Position = 0;
        var read = stream.Read(bom, 0, size);

        if (read >= Utf8BomLength && bom[0] == Utf8Bom0 && bom[1] == Utf8Bom1 && bom[2] == Utf8Bom2)
        {
            return (new UTF8Encoding(false), Utf8BomLength);
        }

        if (read >= Utf16BomLength && bom[0] == Utf16LeBom0 && bom[1] == Utf16LeBom1)
        {
            return (Encoding.Unicode, Utf16BomLength);
        }

        if (read >= Utf16BomLength && bom[0] == Utf16BeBom0 && bom[1] == Utf16BeBom1)
        {
            return (Encoding.BigEndianUnicode, Utf16BomLength);
        }

        return (fallback, 0);
    }

    private static bool HeadMatches(Stream stream, byte[] head)
    {
        if (head.Length == 0)
        {
            return true;
        }

        if (stream.Length < head.Length)
        {
            return false;
        }

        return ReadHead(stream, head.Length).AsSpan().SequenceEqual(head);
    }

    private static byte[] ReadHead(Stream stream, long length)
    {
        var head = new byte[length];
        stream.Position = 0;
        var total = 0;
        while (total < head.Length)
        {
            var read = stream.Read(head, total, head.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == head.Length ? head : head[..total];
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ReadBlockBytes, FileOptions.SequentialScan);

    private static LogScanResult Finish(FileStream stream, LogScanResult result) =>
        result with { LastWriteUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle) };

    private static LogLine BuildLine(string text, bool truncated, Regex? pattern)
    {
        if (pattern is null)
        {
            return new LogLine(text, truncated, NoHighlights);
        }

        List<LogHighlight>? highlights = null;
        for (var match = pattern.Match(text); match.Success; match = match.NextMatch())
        {
            if (match.Length > 0)
            {
                (highlights ??= []).Add(new LogHighlight(match.Index, match.Length));
            }
        }

        return new LogLine(text, truncated, highlights ?? NoHighlights);
    }

    private sealed class LineAssembler
    {
        private readonly LogFilter _filter;
        private readonly int _maxLines;
        private readonly CancellationToken _ct;
        private readonly Queue<(string Text, bool Truncated)> _matches = new();
        private readonly StringBuilder _line = new();
        private bool _truncated;
        // A carriage return that overflowed the cap: dropped only if more content follows on the line.
        private bool _heldCr;
        private int _sinceCancelCheck;

        public LineAssembler(
            LogFilter filter, int maxLines, string pending, bool pendingTruncated, long totalLines, CancellationToken ct)
        {
            _filter = filter;
            _maxLines = maxLines;
            _ct = ct;
            if (pending.Length > MaxLineChars)
            {
                _heldCr = true;
                pending = pending[..MaxLineChars];
            }

            _line.Append(pending);
            _truncated = pendingTruncated;
            TotalLines = totalLines;
        }

        public long TotalLines { get; private set; }

        public bool Overflowed { get; private set; }

        public string PendingText => _heldCr ? _line + "\r" : _line.ToString();

        public bool PendingTruncated => _truncated;

        public void Feed(ReadOnlySpan<char> span)
        {
            while (span.Length > 0)
            {
                var newline = span.IndexOf('\n');
                Append(newline < 0 ? span : span[..newline]);
                if (newline < 0)
                {
                    return;
                }

                CompleteLine();
                span = span[(newline + 1)..];
            }
        }

        public IReadOnlyList<LogLine> BuildLines()
        {
            var lines = new List<LogLine>(_matches.Count);
            foreach (var (text, truncated) in _matches)
            {
                _ct.ThrowIfCancellationRequested();
                lines.Add(BuildLine(text, truncated, _filter.Pattern));
            }

            return lines;
        }

        public LogLine? BuildPendingLine()
        {
            var text = Normalize(_line.ToString(), _truncated);
            if (text.Length == 0 || (_filter.Pattern is not null && !_filter.Pattern.IsMatch(text)))
            {
                return null;
            }

            return BuildLine(text, _truncated, _filter.Pattern);
        }

        private void Append(ReadOnlySpan<char> segment)
        {
            if (segment.Length == 0)
            {
                return;
            }

            if (_heldCr)
            {
                _heldCr = false;
                _truncated = true;
            }

            var room = MaxLineChars - _line.Length;
            if (segment.Length <= room)
            {
                _line.Append(segment);
                return;
            }

            _line.Append(segment[..room]);
            if (segment.Length - room == 1 && segment[room] == '\r')
            {
                _heldCr = true;
            }
            else
            {
                _truncated = true;
            }
        }

        private void CompleteLine()
        {
            var text = Normalize(_line.ToString(), _truncated);
            var truncated = _truncated;
            _line.Clear();
            _truncated = false;
            _heldCr = false;
            TotalLines++;

            if (++_sinceCancelCheck >= LineCancelCheckInterval)
            {
                _sinceCancelCheck = 0;
                _ct.ThrowIfCancellationRequested();
            }

            if (_filter.Pattern is not null && !_filter.Pattern.IsMatch(text))
            {
                return;
            }

            if (_matches.Count >= _maxLines)
            {
                _matches.Dequeue();
                Overflowed = true;
            }

            _matches.Enqueue((text, truncated));
        }

        private static string Normalize(string text, bool truncated)
        {
            if (text.Length > 0 && text[^1] == '\r')
            {
                text = text[..^1];
            }

            if (truncated && text.Length > 0 && char.IsHighSurrogate(text[^1]))
            {
                text = text[..^1];
            }

            return text;
        }
    }
}
