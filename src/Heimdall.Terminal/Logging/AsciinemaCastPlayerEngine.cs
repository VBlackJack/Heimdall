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
using System.Security;
using System.Text;
using System.Text.Json;

namespace Heimdall.Terminal.Logging;

/// <summary>Structured error code returned when loading or parsing an asciinema v2 recording fails.</summary>
public enum AsciinemaCastLoadErrorCode
{
    None = 0,
    EmptyPath = 1,
    FileNotFound = 2,
    FileTooLargeOrEmpty = 3,
    EmptyContent = 4,
    MissingHeader = 5,
    InvalidHeader = 6,
    UnsupportedVersion = 7,
    ReadFailed = 8
}

/// <summary>Parsed asciinema v2 (<c>.cast</c>) file header.</summary>
public sealed record AsciinemaCastHeader(
    int Version,
    int Width,
    int Height,
    long? TimestampUnix,
    string Title);

/// <summary>One timestamped terminal event inside an asciinema v2 recording.</summary>
public sealed record AsciinemaCastEvent(
    double ElapsedSeconds,
    double CompressedSeconds,
    string Type,
    string Data);

/// <summary>Complete parsed asciinema v2 recording ready for timeline playback and seeking.</summary>
public sealed record AsciinemaCastRecording(
    AsciinemaCastHeader Header,
    IReadOnlyList<AsciinemaCastEvent> Events,
    double TotalDurationSeconds,
    double CompressedDurationSeconds);

/// <summary>
/// Pure parser and replay timeline engine for asciinema v2 (<c>.cast</c> NDJSON) recordings.
/// Supports bounded file/event loading, idle-time compression, and deterministic seek state reconstruction.
/// </summary>
public static class AsciinemaCastPlayerEngine
{
    public const long MaxCastFileBytes = 50 * 1024 * 1024;
    public const int MaxEvents = 500_000;
    public const double DefaultMaxIdleSeconds = 2.0;

    /// <summary>Loads and parses an asciinema v2 recording from a local file with size limits.</summary>
    public static bool TryLoadFile(
        string path,
        out AsciinemaCastRecording? recording,
        out string? errorMessage,
        double maxIdleSeconds = DefaultMaxIdleSeconds)
        => TryLoadFile(path, out recording, out _, out errorMessage, maxIdleSeconds);

    /// <summary>Loads and parses an asciinema v2 recording from a local file with structured error code.</summary>
    public static bool TryLoadFile(
        string path,
        out AsciinemaCastRecording? recording,
        out AsciinemaCastLoadErrorCode errorCode,
        out string? errorMessage,
        double maxIdleSeconds = DefaultMaxIdleSeconds)
    {
        recording = null;
        errorCode = AsciinemaCastLoadErrorCode.None;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            errorCode = AsciinemaCastLoadErrorCode.EmptyPath;
            errorMessage = "File path is empty.";
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                errorCode = AsciinemaCastLoadErrorCode.FileNotFound;
                errorMessage = "File does not exist.";
                return false;
            }

            if (info.Length <= 0 || info.Length > MaxCastFileBytes)
            {
                errorCode = AsciinemaCastLoadErrorCode.FileTooLargeOrEmpty;
                errorMessage = "File is empty or exceeds the maximum supported size (50 MiB).";
                return false;
            }

            string content = File.ReadAllText(path, Encoding.UTF8);
            return TryParse(content, out recording, out errorCode, out errorMessage, maxIdleSeconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            errorCode = AsciinemaCastLoadErrorCode.ReadFailed;
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>Parses asciinema v2 NDJSON content into a playback recording.</summary>
    public static bool TryParse(
        string? content,
        out AsciinemaCastRecording? recording,
        out string? errorMessage,
        double maxIdleSeconds = DefaultMaxIdleSeconds)
        => TryParse(content, out recording, out _, out errorMessage, maxIdleSeconds);

    /// <summary>Parses asciinema v2 NDJSON content into a playback recording with structured error code.</summary>
    public static bool TryParse(
        string? content,
        out AsciinemaCastRecording? recording,
        out AsciinemaCastLoadErrorCode errorCode,
        out string? errorMessage,
        double maxIdleSeconds = DefaultMaxIdleSeconds)
    {
        recording = null;
        errorCode = AsciinemaCastLoadErrorCode.None;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(content))
        {
            errorCode = AsciinemaCastLoadErrorCode.EmptyContent;
            errorMessage = "Recording content is empty.";
            return false;
        }

        double idleCap = maxIdleSeconds > 0 && !double.IsNaN(maxIdleSeconds) && !double.IsInfinity(maxIdleSeconds)
            ? maxIdleSeconds
            : double.PositiveInfinity;

        using var reader = new StringReader(content);
        string? headerLine = null;
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                headerLine = line;
                break;
            }
        }

        if (headerLine is null)
        {
            errorCode = AsciinemaCastLoadErrorCode.MissingHeader;
            errorMessage = "Missing asciinema header line.";
            return false;
        }

        if (!TryParseHeader(headerLine, out AsciinemaCastHeader? header, out errorCode, out errorMessage) || header is null)
        {
            return false;
        }

        var events = new List<AsciinemaCastEvent>();
        double prevRaw = 0;
        double compressed = 0;

        while (reader.ReadLine() is { } rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            if (events.Count >= MaxEvents)
            {
                break;
            }

            if (!TryParseEventLine(rawLine, out double time, out string? type, out string? data))
            {
                continue;
            }

            if (type is not ("o" or "r"))
            {
                continue;
            }

            double clampedTime = double.IsNaN(time) || double.IsInfinity(time) || time < prevRaw
                ? prevRaw
                : time;

            double delta = clampedTime - prevRaw;
            compressed += Math.Min(delta, idleCap);
            prevRaw = clampedTime;

            events.Add(new AsciinemaCastEvent(clampedTime, compressed, type, data ?? string.Empty));
        }

        recording = new AsciinemaCastRecording(header, events, prevRaw, compressed);
        return true;
    }

    /// <summary>
    /// Reconstructs cumulative terminal output up to <paramref name="targetSeconds"/> and returns
    /// the index of the last included event (or <c>-1</c> if none).
    /// </summary>
    public static (string CumulativeOutput, int LastEventIndex) BuildStateAtTime(
        AsciinemaCastRecording recording,
        double targetSeconds,
        bool useIdleCap)
    {
        var (output, lastIdx, _, _) = BuildSeekStateAtTime(recording, targetSeconds, useIdleCap);
        return (output, lastIdx);
    }

    /// <summary>
    /// Reconstructs cumulative terminal output and effective terminal dimensions (<c>COLSxROWS</c>
    /// from the header and any <c>"r"</c> resize events) up to <paramref name="targetSeconds"/>.
    /// </summary>
    public static (string CumulativeOutput, int LastEventIndex, int Width, int Height) BuildSeekStateAtTime(
        AsciinemaCastRecording recording,
        double targetSeconds,
        bool useIdleCap)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var sb = new StringBuilder();
        int lastIdx = -1;
        int width = Math.Clamp(recording.Header.Width, 1, 500);
        int height = Math.Clamp(recording.Header.Height, 1, 200);
        double bound = Math.Max(0, targetSeconds);

        for (int i = 0; i < recording.Events.Count; i++)
        {
            var evt = recording.Events[i];
            double evtTime = useIdleCap ? evt.CompressedSeconds : evt.ElapsedSeconds;
            if (evtTime > bound)
            {
                break;
            }

            lastIdx = i;
            if (evt.Type == "o" && evt.Data.Length > 0)
            {
                sb.Append(evt.Data);
            }
            else if (evt.Type == "r" && TryParseResizePayload(evt.Data, out int cols, out int rows))
            {
                width = cols;
                height = rows;
            }
        }

        return (sb.ToString(), lastIdx, width, height);
    }

    /// <summary>Parses an asciinema v2 <c>"r"</c> resize event payload (<c>COLSxROWS</c>).</summary>
    public static bool TryParseResizePayload(string? payload, out int columns, out int rows)
    {
        columns = 0;
        rows = 0;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        int sep = payload.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (sep <= 0 || sep >= payload.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(payload[..sep].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedCols)
            || !int.TryParse(payload[(sep + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedRows))
        {
            return false;
        }

        if (parsedCols is < 1 or > 500 || parsedRows is < 1 or > 200)
        {
            return false;
        }

        columns = parsedCols;
        rows = parsedRows;
        return true;
    }

    /// <summary>Formats a non-negative playback position in seconds as <c>mm:ss</c> or <c>h:mm:ss</c>.</summary>
    public static string FormatTimestamp(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        long total = (long)Math.Floor(seconds);
        long hours = total / 3600;
        long minutes = (total % 3600) / 60;
        long secs = total % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{secs:00}");
    }

    private static bool TryParseHeader(
        string line,
        out AsciinemaCastHeader? header,
        out AsciinemaCastLoadErrorCode errorCode,
        out string? error)
    {
        header = null;
        errorCode = AsciinemaCastLoadErrorCode.None;
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errorCode = AsciinemaCastLoadErrorCode.InvalidHeader;
                error = "Header must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("version", out var versionProp) || versionProp.ValueKind != JsonValueKind.Number
                || !versionProp.TryGetInt32(out int version) || version != 2)
            {
                errorCode = AsciinemaCastLoadErrorCode.UnsupportedVersion;
                error = "Only asciinema v2 (.cast) recordings are supported.";
                return false;
            }

            int width = root.TryGetProperty("width", out var wProp) && wProp.ValueKind == JsonValueKind.Number && wProp.TryGetInt32(out int w)
                ? Math.Clamp(w, 1, 500)
                : 80;
            int height = root.TryGetProperty("height", out var hProp) && hProp.ValueKind == JsonValueKind.Number && hProp.TryGetInt32(out int h)
                ? Math.Clamp(h, 1, 200)
                : 24;
            long? timestamp = root.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.Number && tsProp.TryGetInt64(out long ts)
                ? ts
                : null;
            string title = root.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String
                ? titleProp.GetString() ?? string.Empty
                : string.Empty;

            header = new AsciinemaCastHeader(version, width, height, timestamp, title);
            return true;
        }
        catch (JsonException ex)
        {
            errorCode = AsciinemaCastLoadErrorCode.InvalidHeader;
            error = $"Invalid asciinema header JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseEventLine(string line, out double time, out string? type, out string? data)
    {
        time = 0;
        type = null;
        data = null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3)
            {
                return false;
            }

            if (root[0].ValueKind != JsonValueKind.Number || !root[0].TryGetDouble(out time))
            {
                return false;
            }

            if (root[1].ValueKind != JsonValueKind.String || root[2].ValueKind != JsonValueKind.String)
            {
                return false;
            }

            type = root[1].GetString();
            data = root[2].GetString();
            return !string.IsNullOrEmpty(type);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
