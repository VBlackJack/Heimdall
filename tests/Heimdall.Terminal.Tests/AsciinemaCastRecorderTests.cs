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
using System.Text.Json;
using Heimdall.Terminal.Logging;

namespace Heimdall.Terminal.Tests;

public sealed class AsciinemaCastRecorderTests
{
    [Fact]
    public void SerializeToCastV2_WritesValidNdjsonHeaderOutputAndResizeEvents()
    {
        var recorder = new AsciinemaCastRecorder(120, 40, "prod-ssh");
        recorder.WriteOutput(Encoding.UTF8.GetBytes("hello "));
        recorder.WriteResize(140, 45);
        recorder.WriteOutput(Encoding.UTF8.GetBytes("world\r\n"));
        recorder.Stop();

        string cast = recorder.SerializeToCastV2(maskSecrets: false, out bool refused, out _);
        Assert.False(refused);

        string[] lines = cast.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);

        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal(2, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(120, header.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(40, header.RootElement.GetProperty("height").GetInt32());
        Assert.Equal("prod-ssh", header.RootElement.GetProperty("title").GetString());

        using var resize = JsonDocument.Parse(lines[2]);
        Assert.Equal("r", resize.RootElement[1].GetString());
        Assert.Equal("140x45", resize.RootElement[2].GetString());
    }

    [Fact]
    public void WriteOutput_PreservesMultibyteUtf8SplitAcrossChunks()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("café");
        var recorder = new AsciinemaCastRecorder(80, 24);
        recorder.WriteOutput(utf8.AsSpan(0, 4)); // splits 2-byte 'é' (0xC3 0xA9)
        recorder.WriteOutput(utf8.AsSpan(4));
        recorder.Stop();

        string cast = recorder.SerializeToCastV2(maskSecrets: false, out bool refused, out _);
        Assert.False(refused);

        string[] lines = cast.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string combined = string.Concat(lines.Skip(1).Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            return doc.RootElement[2].GetString();
        }));
        Assert.Equal("café", combined);
    }

    [Fact]
    public void WriteOutput_PreservesMultibyteUtf8WhenLeadingChunkHasZeroCompleteChars()
    {
        byte[] eAcute = Encoding.UTF8.GetBytes("é"); // [0xC3, 0xA9]
        byte[] euro = Encoding.UTF8.GetBytes("€");   // [0xE2, 0x82, 0xAC]
        var recorder = new AsciinemaCastRecorder(80, 24);
        recorder.WriteOutput(eAcute.AsSpan(0, 1));   // 0 complete chars -> must buffer 0xC3
        recorder.WriteOutput(eAcute.AsSpan(1, 1));
        recorder.WriteOutput(euro.AsSpan(0, 2));     // 0 complete chars -> must buffer 0xE2 0x82
        recorder.WriteOutput(euro.AsSpan(2, 1));
        recorder.Stop();

        string cast = recorder.SerializeToCastV2(maskSecrets: false, out bool refused, out _);
        Assert.False(refused);

        string[] lines = cast.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string combined = string.Concat(lines.Skip(1).Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            return doc.RootElement[2].GetString();
        }));
        Assert.Equal("é€", combined);
    }

    [Fact]
    public void SerializeToCastV2_WithMaskSecrets_RedactsSecretSplitAcrossTwoChunks()
    {
        var recorder = new AsciinemaCastRecorder(80, 24);
        recorder.WriteOutput(Encoding.UTF8.GetBytes("Authorization: Bearer "));
        recorder.WriteOutput(Encoding.UTF8.GetBytes("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c\n"));
        recorder.Stop();

        string cast = recorder.SerializeToCastV2(maskSecrets: true, out bool refused, out _);
        Assert.False(refused);
        Assert.DoesNotContain("SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c", cast, StringComparison.Ordinal);
        Assert.Contains("[masked:", cast, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeToCastV2_WithCrossChunkSecret_PreservesTimestampsOfUnaffectedSurroundingEvents()
    {
        var clock = new SteppingTimeProvider();
        var recorder = new AsciinemaCastRecorder(80, 24, timeProvider: clock);
        recorder.WriteOutput(Encoding.UTF8.GetBytes("step 1\r\n"));
        clock.Advance(TimeSpan.FromSeconds(5));
        recorder.WriteOutput(Encoding.UTF8.GetBytes("key=AKIAIOSFODNN"));
        clock.Advance(TimeSpan.FromSeconds(1));
        recorder.WriteOutput(Encoding.UTF8.GetBytes("7EXAMPLE\r\n"));
        clock.Advance(TimeSpan.FromSeconds(10));
        recorder.WriteOutput(Encoding.UTF8.GetBytes("step 2 done\r\n"));
        recorder.Stop();

        string cast = recorder.SerializeToCastV2(maskSecrets: true, out bool refused, out _);
        Assert.False(refused);
        Assert.DoesNotContain("AKIAIOSFODNN", cast, StringComparison.Ordinal);
        Assert.DoesNotContain("7EXAMPLE", cast, StringComparison.Ordinal);

        string[] lines = cast.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length); // header + step1 + redacted middle + step2
        using var evt1 = JsonDocument.Parse(lines[1]);
        using var evt2 = JsonDocument.Parse(lines[2]);
        using var evt3 = JsonDocument.Parse(lines[3]);
        Assert.Equal("step 1\r\n", evt1.RootElement[2].GetString());
        Assert.Equal(5.0, evt2.RootElement[0].GetDouble(), precision: 2);
        Assert.Contains("[masked:", evt2.RootElement[2].GetString(), StringComparison.Ordinal);
        Assert.Equal(16.0, evt3.RootElement[0].GetDouble(), precision: 2);
        Assert.Equal("step 2 done\r\n", evt3.RootElement[2].GetString());
    }

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerSecond * 100;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan delta) => _ticks += delta.Ticks;
    }

    [Fact]
    public void WriteOutput_EnforcesMaxRecordedCharsCap()
    {
        var recorder = new AsciinemaCastRecorder(80, 24, maxRecordedChars: 1024);
        recorder.WriteOutput(Encoding.UTF8.GetBytes(new string('A', 2000)));
        Assert.True(recorder.WasTruncated);
    }
}
