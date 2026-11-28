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
using System.Text;
using System.Text.Json;
using Heimdall.Core.Redaction;

namespace Heimdall.Terminal.Logging;

/// <summary>
/// Thread-safe, bounded asciinema v2 (<c>.cast</c> NDJSON) terminal session recorder.
/// Uses a stateful UTF-8 <see cref="Decoder"/> so multibyte characters split across raw
/// transport reads never corrupt, records output (<c>"o"</c>) and resize (<c>"r"</c>) events
/// with monotonic timestamps, and supports fail-closed secret redaction across chunk boundaries
/// via <see cref="TextRedactor"/> on export.
/// </summary>
public sealed class AsciinemaCastRecorder
{
    /// <summary>Maximum decoded character payload retained in memory (32 MiB).</summary>
    public const int DefaultMaxRecordedChars = 16 * 1024 * 1024;

    private const char ChunkBoundarySentinel = '\uE000';

    private readonly record struct CastEvent(double ElapsedSeconds, string Type, string Data);

    private readonly object _sync = new();
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private readonly List<CastEvent> _events = [];
    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly int _maxRecordedChars;
    private readonly int _initialColumns;
    private readonly int _initialRows;
    private readonly string _title;
    private int _recordedChars;
    private bool _stopped;
    private bool _truncated;

    public AsciinemaCastRecorder(
        int initialColumns,
        int initialRows,
        string? title = null,
        int maxRecordedChars = DefaultMaxRecordedChars,
        TimeProvider? timeProvider = null)
    {
        _initialColumns = Math.Max(1, initialColumns);
        _initialRows = Math.Max(1, initialRows);
        _title = title?.Trim() ?? string.Empty;
        _maxRecordedChars = Math.Max(1024, maxRecordedChars);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAtUtc = _timeProvider.GetUtcNow();
        _startTimestamp = _timeProvider.GetTimestamp();
    }

    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return !_stopped;
            }
        }
    }

    public bool WasTruncated
    {
        get
        {
            lock (_sync)
            {
                return _truncated;
            }
        }
    }

    public int EventCount
    {
        get
        {
            lock (_sync)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>Appends a raw UTF-8 terminal output chunk from any thread.</summary>
    public void WriteOutput(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            if (_stopped || _truncated)
            {
                return;
            }

            int charCount = _utf8Decoder.GetCharCount(data, flush: false);
            if (charCount == 0)
            {
                // Still call GetChars so incomplete multi-byte UTF-8 leading bytes are buffered.
                _ = _utf8Decoder.GetChars(data, Span<char>.Empty, flush: false);
                return;
            }

            char[] chars = new char[charCount];
            int decoded = _utf8Decoder.GetChars(data, chars, flush: false);
            if (decoded <= 0)
            {
                return;
            }

            string text = new string(chars, 0, decoded);
            if (_recordedChars + text.Length > _maxRecordedChars)
            {
                int remaining = _maxRecordedChars - _recordedChars;
                if (remaining > 0)
                {
                    text = text[..remaining];
                    _events.Add(new CastEvent(GetElapsedSecondsLocked(), "o", text));
                    _recordedChars += text.Length;
                }

                _truncated = true;
                return;
            }

            _events.Add(new CastEvent(GetElapsedSecondsLocked(), "o", text));
            _recordedChars += text.Length;
        }
    }

    /// <summary>Records an asciinema v2 resize (<c>"r"</c>) event.</summary>
    public void WriteResize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_stopped || _truncated)
            {
                return;
            }

            string dim = string.Create(CultureInfo.InvariantCulture, $"{columns}x{rows}");
            _events.Add(new CastEvent(GetElapsedSecondsLocked(), "r", dim));
        }
    }

    /// <summary>Stops recording; subsequent writes are ignored.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            _stopped = true;
        }
    }

    /// <summary>
    /// Serializes the captured session as valid asciinema v2 NDJSON.
    /// When <paramref name="maskSecrets"/> is true, runs <see cref="TextRedactor"/> across
    /// chunk boundaries and fails closed (returns empty string with <paramref name="redactionRefused"/> = true)
    /// if any redaction rule times out.
    /// </summary>
    public string SerializeToCastV2(
        bool maskSecrets,
        out bool redactionRefused,
        out string? refusedRuleId)
    {
        redactionRefused = false;
        refusedRuleId = null;

        List<CastEvent> snapshot;
        lock (_sync)
        {
            snapshot = [.. _events];
        }

        if (maskSecrets && snapshot.Count > 0)
        {
            if (!TryRedactOutputEvents(snapshot, out var redactedEvents, out refusedRuleId))
            {
                redactionRefused = true;
                return string.Empty;
            }

            snapshot = redactedEvents;
        }

        var sb = new StringBuilder();
        var header = new Dictionary<string, object>
        {
            ["version"] = 2,
            ["width"] = _initialColumns,
            ["height"] = _initialRows,
            ["timestamp"] = _startedAtUtc.ToUnixTimeSeconds(),
            ["env"] = new Dictionary<string, string> { ["TERM"] = "xterm-256color" }
        };
        if (!string.IsNullOrWhiteSpace(_title))
        {
            header["title"] = _title;
        }

        sb.Append(JsonSerializer.Serialize(header));
        sb.Append('\n');

        foreach (var evt in snapshot)
        {
            sb.Append('[');
            sb.Append(Math.Round(evt.ElapsedSeconds, 6).ToString("0.######", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(JsonSerializer.Serialize(evt.Type));
            sb.Append(',');
            sb.Append(JsonSerializer.Serialize(evt.Data));
            sb.Append("]\n");
        }

        return sb.ToString();
    }

    private static bool TryRedactOutputEvents(
        List<CastEvent> events,
        out List<CastEvent> result,
        out string? refusedRuleId)
    {
        result = [];
        refusedRuleId = null;

        // Reassemble consecutive output events into one stream (stripping any pre-existing sentinel
        // collision) so secrets split across two SSH read chunks are caught by TextRedactor.
        var outputIndices = new List<int>();
        var joined = new StringBuilder();
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].Type == "o")
            {
                if (outputIndices.Count > 0)
                {
                    joined.Append(ChunkBoundarySentinel);
                }

                outputIndices.Add(i);
                joined.Append(events[i].Data.Replace(ChunkBoundarySentinel, ' '));
            }
        }

        if (outputIndices.Count == 0)
        {
            result = events;
            return true;
        }

        // First pass: redact the contiguous stream without sentinels to detect cross-chunk secrets.
        string contiguous = joined.ToString().Replace(ChunkBoundarySentinel.ToString(), string.Empty, StringComparison.Ordinal);
        RedactionResult contiguousResult = TextRedactor.Redact(contiguous, new RedactionOptions());
        if (contiguousResult.Refused)
        {
            refusedRuleId = contiguousResult.RefusedRuleId;
            return false;
        }

        // Also redact the sentinel-delimited form so intra-chunk secrets preserve exact event timing.
        RedactionResult sentinelResult = TextRedactor.Redact(joined.ToString(), new RedactionOptions());
        if (sentinelResult.Refused)
        {
            refusedRuleId = sentinelResult.RefusedRuleId;
            return false;
        }

        string[] parts = sentinelResult.Text.Split(ChunkBoundarySentinel);
        bool crossChunkSecretDetected =
            !string.Equals(
                sentinelResult.Text.Replace(ChunkBoundarySentinel.ToString(), string.Empty, StringComparison.Ordinal),
                contiguousResult.Text,
                StringComparison.Ordinal);

        if (crossChunkSecretDetected || parts.Length != outputIndices.Count)
        {
            // A secret spanned a chunk boundary: emit the safely redacted contiguous output at the
            // first output timestamp while keeping all resize events intact (never leak split secret).
            bool emitted = false;
            foreach (var evt in events)
            {
                if (evt.Type != "o")
                {
                    result.Add(evt);
                }
                else if (!emitted)
                {
                    result.Add(new CastEvent(evt.ElapsedSeconds, "o", contiguousResult.Text));
                    emitted = true;
                }
            }

            return true;
        }

        int outPos = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].Type == "o")
            {
                result.Add(events[i] with { Data = parts[outPos++] });
            }
            else
            {
                result.Add(events[i]);
            }
        }

        return true;
    }

    private double GetElapsedSecondsLocked()
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(_startTimestamp);
        return Math.Max(0d, elapsed.TotalSeconds);
    }
}
