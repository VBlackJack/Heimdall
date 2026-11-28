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
