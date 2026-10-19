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
using Heimdall.App.Services;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// The request/reply state machine behind <c>ITerminalTextSource.ReadTextAsync</c>.
/// </summary>
/// <remarks>
/// <para>Producers covered. Replies: <c>EmbeddedSshView.OnWebMessageReceived</c>
/// (EmbeddedSshView.xaml.cs) hands every <c>buffer:</c> message from the trusted page to
/// <c>TerminalBufferReadCoordinator.HandleReplyAsync</c>. Requests: <c>EmbeddedSshView.ReadTextAsync</c>
/// calls <c>TerminalBufferReadCoordinator.ReadAsync</c> with <c>CanReadText</c> and
/// <c>PostTerminalMessage</c>. Dispose and <c>DisableWebViewPosts</c> call
/// <c>FailPending</c>. The view itself needs WPF and WebView2, so the state machine is the unit
/// under test and the view only forwards to it.</para>
/// </remarks>
public sealed class TerminalBufferReadCoordinatorTests
{
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(50);

    private static string Reply(int id, bool truncated, string text) =>
        $"buffer:{id.ToString(CultureInfo.InvariantCulture)}:{(truncated ? "1" : "0")}:"
        + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static int IdOf(string request)
    {
        Assert.StartsWith(TerminalBufferReadCoordinator.RequestPrefix, request, StringComparison.Ordinal);
        return int.Parse(
            request[TerminalBufferReadCoordinator.RequestPrefix.Length..],
            NumberStyles.None,
            CultureInfo.InvariantCulture);
    }

    // ---- reply parser ----

    [Fact]
    public void TryParseReply_ReadsIdFlagAndPayload()
    {
        bool parsed = TerminalBufferReadCoordinator.TryParseReply("buffer:12:0:QUJD", out int id, out bool truncated, out string base64);

        Assert.True(parsed);
        Assert.Equal(12, id);
        Assert.False(truncated);
        Assert.Equal("QUJD", base64);
    }

    [Fact]
    public void TryParseReply_ReadsTheTruncatedFlag()
    {
        bool parsed = TerminalBufferReadCoordinator.TryParseReply("buffer:3:1:QUJD", out int id, out bool truncated, out _);

        Assert.True(parsed);
        Assert.Equal(3, id);
        Assert.True(truncated);
    }

    [Fact]
    public void TryParseReply_AcceptsAnEmptyPayload()
    {
        bool parsed = TerminalBufferReadCoordinator.TryParseReply("buffer:1:0:", out _, out _, out string base64);

        Assert.True(parsed);
        Assert.Equal(string.Empty, base64);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("buffer:")]
    [InlineData("buffer:1")]
    [InlineData("buffer:1:0")]
    [InlineData("buffer::0:QUJD")]
    [InlineData("buffer:x:0:QUJD")]
    [InlineData("buffer:-1:0:QUJD")]
    [InlineData("buffer:1:2:QUJD")]
    [InlineData("buffer:1::QUJD")]
    [InlineData("buffer:99999999999:0:QUJD")]
    [InlineData("clipboard-write:1:0:QUJD")]
    public void TryParseReply_RejectsMalformedMessages(string? message)
    {
        Assert.False(TerminalBufferReadCoordinator.TryParseReply(message, out _, out _, out _));
    }

    [Fact]
    public void TryDecodeText_RejectsInvalidBase64()
    {
        Assert.False(TerminalBufferReadCoordinator.TryDecodeText("not base64!", out string? text));
        Assert.Null(text);
    }

    [Fact]
    public void TryDecodeText_RejectsAPayloadOverTheReplyCap()
    {
        string overCap = new('A', TerminalBufferReadCoordinator.MaxReplyBase64Length + 4);

        Assert.False(TerminalBufferReadCoordinator.TryDecodeText(overCap, out _));
    }

    [Fact]
    public void TryDecodeText_AcceptsAPayloadAtThePageCap()
    {
        string atPageCap = Convert.ToBase64String(new byte[TerminalBufferReadCoordinator.PageMaxTextBytes]);

        Assert.True(TerminalBufferReadCoordinator.TryDecodeText(atPageCap, out string? text));
        Assert.Equal(TerminalBufferReadCoordinator.PageMaxTextBytes, text!.Length);
    }

    [Fact]
    public void ReplyCap_IsSeparateFromAndLargerThanTheInboundMessageLimit()
    {
        Assert.True(TerminalBufferReadCoordinator.MaxReplyDecodedBytes > EmbeddedSshView.MaxInboundWebMessageDecodedBytes);
        Assert.True(TerminalBufferReadCoordinator.MaxReplyDecodedBytes > TerminalBufferReadCoordinator.PageMaxTextBytes);
    }

    // ---- request / reply ----

    [Fact]
    public async Task ReadAsync_DeliversTheTextAndTheTruncatedFlag()
    {
        TerminalBufferReadCoordinator coordinator = new();
        string? posted = null;

        Task<TerminalTextReadResult> read = coordinator.ReadAsync(true, m => posted = m, LongTimeout, CancellationToken.None);
        await coordinator.HandleReplyAsync(Reply(IdOf(posted!), truncated: true, "line one\nline two"));
        TerminalTextReadResult result = await read;

        Assert.Equal(TerminalTextReadStatus.Ok, result.Status);
        Assert.Equal("line one\nline two", result.Text);
        Assert.True(result.Truncated);
        Assert.False(coordinator.HasPendingRead);
    }

    [Fact]
    public async Task ReadAsync_DecodesALargeReplyOffTheCallerThread()
    {
        TerminalBufferReadCoordinator coordinator = new();
        string? posted = null;
        string large = new('x', TerminalBufferReadCoordinator.OffThreadDecodeMinBase64Length * 2);

        Task<TerminalTextReadResult> read = coordinator.ReadAsync(true, m => posted = m, LongTimeout, CancellationToken.None);
        await coordinator.HandleReplyAsync(Reply(IdOf(posted!), truncated: false, large));
        TerminalTextReadResult result = await read;

        Assert.Equal(TerminalTextReadStatus.Ok, result.Status);
        Assert.Equal(large, result.Text);
    }

    /// <summary>
    /// A reply to an older request (the page answered after the timeout) must not complete the
    /// request now pending.
    /// </summary>
    [Fact]
    public async Task HandleReplyAsync_DiscardsAReplyWhoseIdIsNotThePendingOne()
    {
        TerminalBufferReadCoordinator coordinator = new();
        string? posted = null;

        Task<TerminalTextReadResult> read = coordinator.ReadAsync(true, m => posted = m, LongTimeout, CancellationToken.None);
        int pendingId = IdOf(posted!);

        await coordinator.HandleReplyAsync(Reply(pendingId + 1, truncated: false, "stale"));
        await coordinator.HandleReplyAsync(Reply(pendingId - 1, truncated: false, "stale"));
        Assert.False(read.IsCompleted);

        await coordinator.HandleReplyAsync(Reply(pendingId, truncated: false, "fresh"));
        TerminalTextReadResult result = await read;

        Assert.Equal("fresh", result.Text);
    }

    [Fact]
    public async Task HandleReplyAsync_IgnoresAReplyWhenNothingIsPending()
    {
        TerminalBufferReadCoordinator coordinator = new();

        await coordinator.HandleReplyAsync(Reply(1, truncated: false, "orphan"));

        Assert.False(coordinator.HasPendingRead);
    }

    [Fact]
    public async Task HandleReplyAsync_FailsThePendingReadWhenThePayloadIsNotBase64()
    {
        TerminalBufferReadCoordinator coordinator = new();
        string? posted = null;

        Task<TerminalTextReadResult> read = coordinator.ReadAsync(true, m => posted = m, LongTimeout, CancellationToken.None);
        await coordinator.HandleReplyAsync($"buffer:{IdOf(posted!)}:0:not base64!");
        TerminalTextReadResult result = await read;

        Assert.Equal(TerminalTextReadStatus.Failed, result.Status);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task HandleReplyAsync_FailsThePendingReadWhenThePayloadIsOverTheReplyCap()
    {
        TerminalBufferReadCoordinator coordinator = new();
        string? posted = null;

        Task<TerminalTextReadResult> read = coordinator.ReadAsync(true, m => posted = m, LongTimeout, CancellationToken.None);
        string overCap = new('A', TerminalBufferReadCoordinator.MaxReplyBase64Length + 4);
        await coordinator.HandleReplyAsync($"buffer:{IdOf(posted!)}:0:{overCap}");
        TerminalTextReadResult result = await read;

        Assert.Equal(TerminalTextReadStatus.Failed, result.Status);
    }

    [Fact]
    public async Task HandleReplyAsync_LeavesTheReadPendingWhenTheReplyIsMalformed()
    {
        TerminalBufferReadCoordinator coordinator = new();

        Task<TerminalTextReadResult> read = coordinator.ReadAsync(true, _ => { }, ShortTimeout, CancellationToken.None);
        await coordinator.HandleReplyAsync("buffer:oops");

        Assert.Equal(TerminalTextReadStatus.Timeout, (await read).Status);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNotReadyAndPostsNothingWhenTheTerminalCannotBeRead()
    {
        TerminalBufferReadCoordinator coordinator = new();
        int posts = 0;

        TerminalTextReadResult result = await coordinator.ReadAsync(false, _ => posts++, LongTimeout, CancellationToken.None);

        Assert.Equal(TerminalTextReadStatus.NotReady, result.Status);
        Assert.Equal(0, posts);
        Assert.False(coordinator.HasPendingRead);
    }

    [Fact]
    public async Task ReadAsync_TimesOutWhenThePageDoesNotAnswer()
    {
        TerminalBufferReadCoordinator coordinator = new();

        TerminalTextReadResult result = await coordinator.ReadAsync(true, _ => { }, ShortTimeout, CancellationToken.None);

        Assert.Equal(TerminalTextReadStatus.Timeout, result.Status);
        Assert.False(coordinator.HasPendingRead);
    }

    [Fact]
    public async Task ReadAsync_NumbersEachRequestAndDiscardsTheLateAnswerOfATimedOutOne()
    {
        TerminalBufferReadCoordinator coordinator = new();
        List<string> posted = [];

        TerminalTextReadResult first = await coordinator.ReadAsync(true, posted.Add, ShortTimeout, CancellationToken.None);
        Task<TerminalTextReadResult> second = coordinator.ReadAsync(true, posted.Add, LongTimeout, CancellationToken.None);

        Assert.Equal(TerminalTextReadStatus.Timeout, first.Status);
        Assert.Equal(2, posted.Count);
        Assert.True(IdOf(posted[1]) > IdOf(posted[0]));

        await coordinator.HandleReplyAsync(Reply(IdOf(posted[0]), truncated: false, "late"));
        Assert.False(second.IsCompleted);

        await coordinator.HandleReplyAsync(Reply(IdOf(posted[1]), truncated: false, "on time"));
        Assert.Equal("on time", (await second).Text);
    }

    [Fact]
    public async Task ReadAsync_FailsFastWhileAnotherReadIsPending()
    {
        TerminalBufferReadCoordinator coordinator = new();
        List<string> posted = [];

        Task<TerminalTextReadResult> first = coordinator.ReadAsync(true, posted.Add, LongTimeout, CancellationToken.None);
        TerminalTextReadResult second = await coordinator.ReadAsync(true, posted.Add, LongTimeout, CancellationToken.None);

        Assert.Equal(TerminalTextReadStatus.Failed, second.Status);
        Assert.Single(posted);
        Assert.False(first.IsCompleted);

        coordinator.FailPending();
        await first;
    }

    [Fact]
    public async Task FailPending_CompletesThePendingReadAsFailed()
    {
        TerminalBufferReadCoordinator coordinator = new();

        Task<TerminalTextReadResult> read = coordinator.ReadAsync(true, _ => { }, LongTimeout, CancellationToken.None);
        coordinator.FailPending();
        TerminalTextReadResult result = await read;

        Assert.Equal(TerminalTextReadStatus.Failed, result.Status);
        Assert.False(coordinator.HasPendingRead);
    }

    [Fact]
    public void FailPending_DoesNothingWhenNothingIsPending()
    {
        TerminalBufferReadCoordinator coordinator = new();

        coordinator.FailPending();

        Assert.False(coordinator.HasPendingRead);
    }

    [Fact]
    public async Task ReadAsync_HonoursCancellationAndFreesTheSlot()
    {
        TerminalBufferReadCoordinator coordinator = new();
        using CancellationTokenSource cts = new();

        Task<TerminalTextReadResult> read = coordinator.ReadAsync(true, _ => { }, LongTimeout, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.False(coordinator.HasPendingRead);
    }

    [Fact]
    public async Task ReadAsync_ThrowsWithoutPostingWhenTheTokenIsAlreadyCancelled()
    {
        TerminalBufferReadCoordinator coordinator = new();
        int posts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ReadAsync(true, _ => posts++, LongTimeout, new CancellationToken(canceled: true)));

        Assert.Equal(0, posts);
    }

    // ---- logging ----

    /// <summary>
    /// Producer: <c>EmbeddedSshView.OnWebMessageReceived</c> logs <c>DescribeWebViewText(message)</c>
    /// for a message it drops; a <c>buffer:</c> message is terminal text, unmasked, and must only
    /// ever be described by its length.
    /// </summary>
    [Fact]
    public void DescribeWebViewText_NeverIncludesTerminalTextFromABufferMessage()
    {
        const string secret = "hunter2-correct-horse-battery-staple";
        string message = Reply(7, truncated: false, "export TOKEN=" + secret);

        string described = EmbeddedSshView.DescribeWebViewText(message);

        Assert.DoesNotContain(secret, described, StringComparison.Ordinal);
        Assert.DoesNotContain(message[TerminalBufferReadCoordinator.ReplyPrefix.Length..], described, StringComparison.Ordinal);
        Assert.Contains(
            (message.Length - TerminalBufferReadCoordinator.ReplyPrefix.Length).ToString(CultureInfo.InvariantCulture),
            described,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeWebViewText_StillTruncatesOtherLongMessages()
    {
        string described = EmbeddedSshView.DescribeWebViewText("input:" + new string('A', 1000));

        Assert.True(described.Length < 300);
        Assert.StartsWith("input:", described, StringComparison.Ordinal);
    }
}
