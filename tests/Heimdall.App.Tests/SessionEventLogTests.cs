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
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="SessionEventLog"/>: NDJSON shape, append across events and sessions,
/// required-field round-trip, null-field omission, "user@" host stripping, size rollover, lazy
/// file creation, and Dispose-flush completeness.
/// </summary>
public sealed class SessionEventLogTests : IDisposable
{
    private const long LargeCap = 4L * 1024 * 1024;
    private const int FlushIntervalMs = 1000;

    private readonly List<string> _tempDirectories = [];

    private string NewTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "HeimdallSessionEventLogTests",
            Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }

    private static string EventLogPath(string root) => Path.Combine(root, "session-events.log");

    // Reads non-empty NDJSON lines from a file, tolerant of a writer still holding a handle.
    private static List<string> ReadLines(string path)
    {
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        string content = reader.ReadToEnd();
        return content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static int CountLinesAcrossFiles(string root) =>
        Directory.GetFiles(root).Sum(file => ReadLines(file).Count);

    [Fact]
    public void LogEvent_Connected_WritesSingleNdjsonLineWithRequiredFields()
    {
        string root = NewTempDirectory();
        SessionEventLog log = new SessionEventLog(root, LargeCap, FlushIntervalMs);

        log.LogEvent(SessionEventRecord.Connected("RDP", "host.example", "host.example (admin)"));
        log.Dispose();

        List<string> lines = ReadLines(EventLogPath(root));
        lines.Should().HaveCount(1);

        using JsonDocument doc = JsonDocument.Parse(lines[0]);
        JsonElement obj = doc.RootElement;
        obj.GetProperty("protocol").GetString().Should().Be("RDP");
        obj.GetProperty("event").GetString().Should().Be("Connected");
        obj.GetProperty("host").GetString().Should().Be("host.example");
        obj.GetProperty("title").GetString().Should().Be("host.example (admin)");

        // ts must be ISO-8601 round-trip UTC.
        string ts = obj.GetProperty("ts").GetString()!;
        DateTimeOffset.TryParse(
            ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            .Should().BeTrue();
        ts.Should().EndWith("Z");

        // Connect-only: disconnect fields are omitted, not null.
        obj.TryGetProperty("reason", out _).Should().BeFalse();
        obj.TryGetProperty("reasonCode", out _).Should().BeFalse();
        obj.TryGetProperty("durationMs", out _).Should().BeFalse();
        obj.TryGetProperty("endTrigger", out _).Should().BeFalse();
    }

    [Fact]
    public void LogEvent_Disconnected_RoundTripsReasonAndDurationFields()
    {
        string root = NewTempDirectory();
        SessionEventLog log = new SessionEventLog(root, LargeCap, FlushIntervalMs);

        log.LogEvent(SessionEventRecord.Disconnected(
            "RDP",
            "host.example",
            title: "host.example (admin)",
            reasonKey: "BadCredentials",
            reasonCode: 2055,
            durationMs: 42_000,
            endTrigger: "remote"));
        log.Dispose();

        List<string> lines = ReadLines(EventLogPath(root));
        lines.Should().HaveCount(1);

        using JsonDocument doc = JsonDocument.Parse(lines[0]);
        JsonElement obj = doc.RootElement;
        obj.GetProperty("event").GetString().Should().Be("Disconnected");
        obj.GetProperty("reason").GetString().Should().Be("BadCredentials");
        obj.GetProperty("reasonCode").GetInt32().Should().Be(2055);
        obj.GetProperty("durationMs").GetInt64().Should().Be(42_000);
        obj.GetProperty("endTrigger").GetString().Should().Be("remote");
    }

    [Fact]
    public void LogEvent_DisconnectedWithoutReason_OmitsOptionalFields()
    {
        string root = NewTempDirectory();
        SessionEventLog log = new SessionEventLog(root, LargeCap, FlushIntervalMs);

        // VNC / Citrix have no reason code: only event + host (+ optional duration/trigger).
        log.LogEvent(SessionEventRecord.Disconnected(
            "VNC", "10.0.0.5", title: null, durationMs: 1234, endTrigger: "teardown"));
        log.Dispose();

        using JsonDocument doc = JsonDocument.Parse(ReadLines(EventLogPath(root)).Single());
        JsonElement obj = doc.RootElement;
        obj.GetProperty("event").GetString().Should().Be("Disconnected");
        obj.GetProperty("durationMs").GetInt64().Should().Be(1234);
        obj.GetProperty("endTrigger").GetString().Should().Be("teardown");
        obj.TryGetProperty("reason", out _).Should().BeFalse();
        obj.TryGetProperty("reasonCode", out _).Should().BeFalse();
        obj.TryGetProperty("title", out _).Should().BeFalse();
    }

    [Fact]
    public void LogEvent_StripsUserPrefixFromHost()
    {
        string root = NewTempDirectory();
        SessionEventLog log = new SessionEventLog(root, LargeCap, FlushIntervalMs);

        log.LogEvent(SessionEventRecord.Connected("CITRIX", "admin@10.0.0.5", title: null));
        log.Dispose();

        using JsonDocument doc = JsonDocument.Parse(ReadLines(EventLogPath(root)).Single());
        doc.RootElement.GetProperty("host").GetString().Should().Be("10.0.0.5");
    }

    [Fact]
    public void LogEvent_AppendsAcrossMultipleEventsAndSessions()
    {
        string root = NewTempDirectory();
        SessionEventLog log = new SessionEventLog(root, LargeCap, FlushIntervalMs);

        log.LogEvent(SessionEventRecord.Connected("RDP", "host-a", "A"));
        log.LogEvent(SessionEventRecord.Disconnected("RDP", "host-a", "A", endTrigger: "user"));
        log.LogEvent(SessionEventRecord.Connected("VNC", "host-b", "B"));
        log.LogEvent(SessionEventRecord.Disconnected("VNC", "host-b", "B", endTrigger: "remote"));
        log.Dispose();

        List<string> lines = ReadLines(EventLogPath(root));
        lines.Should().HaveCount(4);
        lines.Should().OnlyContain(line => line.StartsWith("{") && line.EndsWith("}"));
    }

    [Fact]
    public void LogEvent_AppendsAcrossSeparateInstancesToSameFile()
    {
        string root = NewTempDirectory();

        SessionEventLog first = new SessionEventLog(root, LargeCap, FlushIntervalMs);
        first.LogEvent(SessionEventRecord.Connected("RDP", "host-a", "A"));
        first.Dispose();

        SessionEventLog second = new SessionEventLog(root, LargeCap, FlushIntervalMs);
        second.LogEvent(SessionEventRecord.Connected("VNC", "host-b", "B"));
        second.Dispose();

        ReadLines(EventLogPath(root)).Should().HaveCount(2);
    }

    [Fact]
    public void LogEvent_PastSizeCap_RollsOverToContinuationFile()
    {
        string root = NewTempDirectory();
        // Tiny cap forces rollover after a couple of event lines.
        SessionEventLog log = new SessionEventLog(root, maxBytes: 64, flushIntervalMs: 50);

        const int eventCount = 20;
        for (int i = 0; i < eventCount; i++)
        {
            log.LogEvent(SessionEventRecord.Connected("RDP", $"host-{i}", "session"));
        }

        log.Dispose();

        string continuation = EventLogPath(root)[..^4] + ".1.log";
        File.Exists(continuation).Should().BeTrue();

        // Every line is written atomically (rollover only happens between lines), so the total over
        // all files must equal the number of events, with none lost or duplicated at the seam.
        CountLinesAcrossFiles(root).Should().Be(eventCount);
    }

    [Fact]
    public void LogEvent_BeforeAnyWrite_CreatesNoFile()
    {
        string root = NewTempDirectory();
        // The sink is a dumb writer; when no caller logs, nothing is materialized on disk.
        using SessionEventLog log = new SessionEventLog(root, LargeCap, FlushIntervalMs);

        (Directory.Exists(root) && Directory.EnumerateFiles(root).Any()).Should().BeFalse();
    }

    [Fact]
    public void Dispose_FlushesPendingEvents_NoLoss()
    {
        string root = NewTempDirectory();
        // A long flush interval guarantees the timer does not fire before Dispose: the only way
        // these events reach disk is the synchronous drain inside Dispose.
        SessionEventLog log = new SessionEventLog(root, LargeCap, flushIntervalMs: 600_000);

        const int eventCount = 50;
        for (int i = 0; i < eventCount; i++)
        {
            log.LogEvent(SessionEventRecord.Connected("RDP", $"host-{i}", "session"));
        }

        log.Dispose();

        ReadLines(EventLogPath(root)).Should().HaveCount(eventCount);

        // No open handle must remain after Dispose: an exclusive open must succeed.
        Action exclusiveOpen = () =>
        {
            using FileStream stream = new FileStream(
                EventLogPath(root), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        };
        exclusiveOpen.Should().NotThrow();
    }

    public void Dispose()
    {
        foreach (string directory in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of temp artifacts.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup of temp artifacts.
            }
        }
    }
}
