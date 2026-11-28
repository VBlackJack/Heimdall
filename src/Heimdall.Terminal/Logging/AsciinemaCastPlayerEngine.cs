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

namespace Heimdall.Terminal.Logging;

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
    {
        recording = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            errorMessage = "File path is empty.";
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                errorMessage = "File does not exist.";
                return false;
            }

            if (info.Length <= 0 || info.Length > MaxCastFileBytes)
            {
                errorMessage = "File is empty or exceeds the maximum supported size (50 MiB).";
                return false;
            }

            string content = File.ReadAllText(path, Encoding.UTF8);
            return TryParse(content, out recording, out errorMessage, maxIdleSeconds);
        }
        catch (IOException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
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
    {
        recording = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(content))
        {
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
            errorMessage = "Missing asciinema header line.";
            return false;
        }

        if (!TryParseHeader(headerLine, out AsciinemaCastHeader? header, out errorMessage) || header is null)
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
        ArgumentNullException.ThrowIfNull(recording);

        var sb = new StringBuilder();
        int lastIdx = -1;
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
        }

        return (sb.ToString(), lastIdx);
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

    private static bool TryParseHeader(string line, out AsciinemaCastHeader? header, out string? error)
    {
        header = null;
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Header must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("version", out var versionProp) || versionProp.ValueKind != JsonValueKind.Number
                || !versionProp.TryGetInt32(out int version) || version != 2)
            {
                error = "Only asciinema v2 (.cast) recordings are supported.";
                return false;
            }

            int width = root.TryGetProperty("width", out var wProp) && wProp.TryGetInt32(out int w)
                ? Math.Clamp(w, 1, 500)
                : 80;
            int height = root.TryGetProperty("height", out var hProp) && hProp.TryGetInt32(out int h)
                ? Math.Clamp(h, 1, 200)
                : 24;
            long? timestamp = root.TryGetProperty("timestamp", out var tsProp) && tsProp.TryGetInt64(out long ts)
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
