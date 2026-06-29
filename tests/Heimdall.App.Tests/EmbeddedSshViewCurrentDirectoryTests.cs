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

public sealed class EmbeddedSshViewCurrentDirectoryTests
{
    [Fact]
    public void TryDecodeCurrentDirectoryPayload_DecodesOsc7PathForwardedByTerminal()
    {
        const string path = "/var/log/project space";
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

        bool decoded = EmbeddedSshView.TryDecodeCurrentDirectoryPayload(payload, out string? result);

        Assert.True(decoded);
        Assert.Equal(path, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64")]
    public void TryDecodeCurrentDirectoryPayload_RejectsMalformedPayload(string payload)
    {
        bool decoded = EmbeddedSshView.TryDecodeCurrentDirectoryPayload(payload, out string? result);

        Assert.False(decoded);
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeCurrentDirectoryPayload_RejectsEmptyDecodedPath()
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(" "));

        bool decoded = EmbeddedSshView.TryDecodeCurrentDirectoryPayload(payload, out string? result);

        Assert.False(decoded);
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeCurrentDirectoryPayload_RejectsOverLimitPayloadBeforeDecoding()
    {
        string payload = new('A', EmbeddedSshView.MaxInboundWebMessageBase64Length + 1);

        bool decoded = EmbeddedSshView.TryDecodeCurrentDirectoryPayload(payload, out string? result);

        Assert.False(decoded);
        Assert.Null(result);
    }

    [Fact]
    public void IsInboundWebMessageBase64PayloadWithinLimit_AcceptsNormalPayload()
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("pwd"));

        Assert.True(EmbeddedSshView.IsInboundWebMessageBase64PayloadWithinLimit(payload));
    }

    [Fact]
    public void IsInboundWebMessageBase64PayloadWithinLimit_RejectsOverLimitPayload()
    {
        string payload = new('A', EmbeddedSshView.MaxInboundWebMessageBase64Length + 1);

        Assert.False(EmbeddedSshView.IsInboundWebMessageBase64PayloadWithinLimit(payload));
    }
}

public sealed class PendingTerminalMessageBufferTests
{
    [Fact]
    public void Enqueue_DropsOldestMessagesWhenBufferedBytesExceedLimit()
    {
        const int maxBufferedBytes = 20;
        var dropNotifications = 0;
        var buffer = new PendingTerminalMessageBuffer(
            maxBufferedBytes,
            () => dropNotifications++);
        string first = new('a', maxBufferedBytes / sizeof(char));
        string second = "bb";

        buffer.Enqueue(first);
        buffer.Enqueue(second);

        Assert.Equal(1, buffer.Count);
        Assert.True(buffer.BufferedBytes <= maxBufferedBytes);
        Assert.Equal(1, dropNotifications);
        Assert.True(buffer.TryDequeue(out string? remaining));
        Assert.Equal(second, remaining);
    }

    [Fact]
    public void Enqueue_LogsOnlyOnceWhenMultipleDropsOccur()
    {
        const int maxBufferedBytes = 20;
        var dropNotifications = 0;
        var buffer = new PendingTerminalMessageBuffer(
            maxBufferedBytes,
            () => dropNotifications++);
        string fill = new('a', maxBufferedBytes / sizeof(char));

        buffer.Enqueue(fill);
        buffer.Enqueue(fill);
        buffer.Enqueue(fill);

        Assert.Equal(1, dropNotifications);
        Assert.True(buffer.BufferedBytes <= maxBufferedBytes);
    }

    [Fact]
    public void Enqueue_DoesNotGrowBeyondLimitWhenManyMessagesArriveBeforeReady()
    {
        const int maxBufferedBytes = 40;
        const int messageCount = 100;
        var buffer = new PendingTerminalMessageBuffer(maxBufferedBytes, static () => { });

        for (var i = 0; i < messageCount; i++)
        {
            buffer.Enqueue("data:" + Convert.ToBase64String([1, 2, 3, 4]));
        }

        Assert.True(buffer.BufferedBytes <= maxBufferedBytes);
    }
}
