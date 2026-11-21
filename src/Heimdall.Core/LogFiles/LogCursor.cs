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

/// <summary>Filter applied while scanning a log file. A null pattern keeps every line.</summary>
public sealed record LogFilter(Regex? Pattern);

/// <summary>A highlighted range inside a <see cref="LogLine"/> text.</summary>
public readonly record struct LogHighlight(int Start, int Length);

/// <summary>One decoded log line, possibly capped, with the ranges matched by the filter.</summary>
public sealed record LogLine(string Text, bool Truncated, IReadOnlyList<LogHighlight> Highlights);

/// <summary>Outcome of a scan.</summary>
public enum LogScanStatus
{
    /// <summary>The scan continued from the cursor (or started fresh).</summary>
    Ok,

    /// <summary>The file was truncated or replaced; the scan restarted from the beginning.</summary>
    Reset,

    /// <summary>The filter exceeded its match timeout; the scan was stopped.</summary>
    FilterTimeout,
}

/// <summary>
/// Result of a scan. <see cref="Lines"/> holds at most <see cref="LogFileScanner.MaxLines"/> matching
/// lines; <see cref="Overflowed"/> tells that older matches were dropped from it.
/// </summary>
public sealed record LogScanResult(
    LogScanStatus Status,
    IReadOnlyList<LogLine> Lines,
    LogLine? PendingLine,
    bool Overflowed,
    long TotalLines,
    long FileLength,
    DateTime LastWriteUtc,
    LogCursor? Cursor);

/// <summary>
/// Opaque resume point of a scan. It owns the decoder state, so it is single use: pass the cursor
/// returned by a scan to the next call and discard the previous one.
/// </summary>
public sealed class LogCursor
{
    internal LogCursor(
        long position,
        Encoding fallbackEncoding,
        Encoding encoding,
        Decoder decoder,
        byte[] head,
        string pendingText,
        bool pendingTruncated,
        long totalLines)
    {
        Position = position;
        FallbackEncoding = fallbackEncoding;
        Encoding = encoding;
        Decoder = decoder;
        Head = head;
        PendingText = pendingText;
        PendingTruncated = pendingTruncated;
        TotalLines = totalLines;
    }

    /// <summary>Number of bytes of the file consumed so far.</summary>
    public long Position { get; }

    internal Encoding FallbackEncoding { get; }

    internal Encoding Encoding { get; }

    internal Decoder Decoder { get; }

    internal byte[] Head { get; }

    internal string PendingText { get; }

    internal bool PendingTruncated { get; }

    internal long TotalLines { get; }

    /// <summary>Set when a scan failed midway: the decoder state is unreliable and the next scan restarts.</summary>
    internal bool Invalid { get; set; }
}
