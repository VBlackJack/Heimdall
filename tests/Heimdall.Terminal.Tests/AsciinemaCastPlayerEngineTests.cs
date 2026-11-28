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
using Heimdall.Terminal.Logging;

namespace Heimdall.Terminal.Tests;

public sealed class AsciinemaCastPlayerEngineTests
{
    [Fact]
    public void TryParse_RoundTripsRecordedCastAndCompressesLongIdleGaps()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero));
        var recorder = new AsciinemaCastRecorder(120, 40, "ops-session", timeProvider: clock);

        recorder.WriteOutput(Encoding.UTF8.GetBytes("hello "));
        clock.Advance(TimeSpan.FromSeconds(15));
        recorder.WriteOutput(Encoding.UTF8.GetBytes("world\r\n"));
        recorder.WriteResize(140, 45);
        clock.Advance(TimeSpan.FromSeconds(1));
        recorder.WriteOutput(Encoding.UTF8.GetBytes("done"));

        string castContent = recorder.SerializeToCastV2(maskSecrets: false, out bool refused, out _);
        Assert.False(refused);
        Assert.True(AsciinemaCastPlayerEngine.TryParse(castContent, out var recording, out string? error, maxIdleSeconds: 2.0), error);
        Assert.NotNull(recording);
        Assert.Equal(2, recording.Header.Version);
        Assert.Equal(120, recording.Header.Width);
        Assert.Equal(40, recording.Header.Height);
        Assert.Equal("ops-session", recording.Header.Title);
        Assert.Equal(4, recording.Events.Count);
        Assert.InRange(recording.TotalDurationSeconds, 15.9, 16.1);
        Assert.InRange(recording.CompressedDurationSeconds, 2.9, 3.1);

        var (midOutput, midIndex) = AsciinemaCastPlayerEngine.BuildStateAtTime(recording, 2.5, useIdleCap: true);
        Assert.Equal("hello world\r\n", midOutput);
        Assert.Equal(2, midIndex);

        var (fullOutput, fullIndex) = AsciinemaCastPlayerEngine.BuildStateAtTime(recording, 5.0, useIdleCap: true);
        Assert.Equal("hello world\r\ndone", fullOutput);
        Assert.Equal(3, fullIndex);
    }

    [Fact]
    public void TryParse_RejectsUnsupportedVersionAndInvalidJson()
    {
        Assert.False(AsciinemaCastPlayerEngine.TryParse("{\"version\":1,\"width\":80,\"height\":24}", out _, out var verCode, out _));
        Assert.Equal(AsciinemaCastLoadErrorCode.UnsupportedVersion, verCode);
        Assert.False(AsciinemaCastPlayerEngine.TryParse("not-json", out _, out var jsonCode, out _));
        Assert.Equal(AsciinemaCastLoadErrorCode.InvalidHeader, jsonCode);
        Assert.False(AsciinemaCastPlayerEngine.TryParse("", out _, out var emptyCode, out _));
        Assert.Equal(AsciinemaCastLoadErrorCode.EmptyContent, emptyCode);
    }

    [Fact]
    public void TryParse_HandlesNullOrStringHeaderDimensionsWithoutThrowing()
    {
        string cast = "{\"version\":2,\"width\":\"80\",\"height\":null,\"timestamp\":null,\"title\":\"safe\"}\n[0.1,\"o\",\"ok\"]\n";
        Assert.True(AsciinemaCastPlayerEngine.TryParse(cast, out var recording, out var code, out string? error), error);
        Assert.Equal(AsciinemaCastLoadErrorCode.None, code);
        Assert.NotNull(recording);
        Assert.Equal(80, recording.Header.Width);
        Assert.Equal(24, recording.Header.Height);
        Assert.Null(recording.Header.TimestampUnix);
        Assert.Equal("safe", recording.Header.Title);
    }

    [Theory]
    [InlineData(-5, "00:00")]
    [InlineData(0, "00:00")]
    [InlineData(65.4, "01:05")]
    [InlineData(3661.9, "1:01:01")]
    public void FormatTimestamp_FormatsReadableTime(double seconds, string expected)
    {
        Assert.Equal(expected, AsciinemaCastPlayerEngine.FormatTimestamp(seconds));
    }

    [Theory]
    [InlineData("120x40", true, 120, 40)]
    [InlineData("80x24", true, 80, 24)]
    [InlineData("0x24", false, 0, 0)]
    [InlineData("900x24", false, 0, 0)]
    [InlineData("invalid", false, 0, 0)]
    public void TryParseResizePayload_ValidatesBoundsAndFormat(
        string payload,
        bool expectedOk,
        int expectedCols,
        int expectedRows)
    {
        bool ok = AsciinemaCastPlayerEngine.TryParseResizePayload(payload, out int cols, out int rows);
        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedCols, cols);
        Assert.Equal(expectedRows, rows);
    }

    [Fact]
    public void BuildSeekStateAtTime_ReplaysOutputsAndTracksResizeEvents()
    {
        string cast = string.Join('\n',
            "{\"version\":2,\"width\":80,\"height\":24}",
            "[0.5,\"o\",\"hello \"]",
            "[1.0,\"r\",\"132x43\"]",
            "[1.5,\"o\",\"world\"]",
            "[3.0,\"o\",\" later\"]");

        Assert.True(AsciinemaCastPlayerEngine.TryParse(cast, out var recording, out _));
        Assert.NotNull(recording);

        var (text, lastIndex, cols, rows) = AsciinemaCastPlayerEngine.BuildSeekStateAtTime(
            recording,
            targetSeconds: 2.0,
            useIdleCap: true);

        Assert.Equal("hello world", text);
        Assert.Equal(2, lastIndex);
        Assert.Equal(132, cols);
        Assert.Equal(43, rows);
    }

    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private DateTimeOffset _now = startUtc;
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta)
        {
            _now += delta;
            _ticks += delta.Ticks;
        }
    }
}
