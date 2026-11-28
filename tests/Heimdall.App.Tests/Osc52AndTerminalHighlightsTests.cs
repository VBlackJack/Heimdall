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
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

public sealed class Osc52AndTerminalHighlightsTests
{
    [Fact]
    public void TryDecodeOsc52Payload_WhenDisabled_RejectsValidPayload()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello from remote"));
        Assert.False(EmbeddedSshView.TryDecodeOsc52Payload(allowOsc52: false, b64, out _));
    }

    [Fact]
    public void TryDecodeOsc52Payload_WhenEnabled_RejectsReadQueryAndOversizedPayload()
    {
        Assert.False(EmbeddedSshView.TryDecodeOsc52Payload(allowOsc52: true, "?", out _));
        Assert.False(EmbeddedSshView.TryDecodeOsc52Payload(allowOsc52: true, "!!!not-base64!!!", out _));

        var oversized = Convert.ToBase64String(new byte[EmbeddedSshView.MaxOsc52DecodedBytes + 1]);
        Assert.False(EmbeddedSshView.TryDecodeOsc52Payload(allowOsc52: true, oversized, out _));
    }

    [Fact]
    public void TryDecodeOsc52Payload_WhenEnabled_DecodesValidUtf8Payload()
    {
        const string expected = "copied via tmux OSC 52";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(expected));

        var ok = EmbeddedSshView.TryDecodeOsc52Payload(allowOsc52: true, b64, out var decoded);

        Assert.True(ok);
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void TryParseCmdDoneMessage_ParsesValidExitCodeAndDuration()
    {
        Assert.True(EmbeddedSshView.TryParseCmdDoneMessage("0:12500", out int exitCode, out long durationMs));
        Assert.Equal(0, exitCode);
        Assert.Equal(12500L, durationMs);

        Assert.True(EmbeddedSshView.TryParseCmdDoneMessage("130:45000", out int sigExit, out long sigDuration));
        Assert.Equal(130, sigExit);
        Assert.Equal(45000L, sigDuration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData(":1000")]
    [InlineData("0:")]
    [InlineData("abc:1000")]
    [InlineData("0:-500")]
    [InlineData("0:1000:extra")]
    public void TryParseCmdDoneMessage_RejectsMalformedPayloads(string? payload)
    {
        Assert.False(EmbeddedSshView.TryParseCmdDoneMessage(payload, out _, out _));
    }

    [Theory]
    [InlineData(-100, "0s")]
    [InlineData(0, "0s")]
    [InlineData(14_999, "14s")]
    [InlineData(65_000, "1m 5s")]
    [InlineData(3_660_000, "1h 1m")]
    public void FormatCommandDuration_FormatsReadableIntervals(long durationMs, string expected)
    {
        Assert.Equal(expected, EmbeddedSshView.FormatCommandDuration(durationMs));
    }
}
