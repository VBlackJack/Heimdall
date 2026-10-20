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
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>Outcome of a <see cref="ITerminalTextSource.ReadTextAsync"/> call.</summary>
public enum TerminalTextReadStatus
{
    /// <summary>The text was read.</summary>
    Ok,

    /// <summary>The terminal page is not ready, the view is disposed or WebView2 is unusable; nothing was requested.</summary>
    NotReady,

    /// <summary>The page did not answer within the read timeout.</summary>
    Timeout,

    /// <summary>The read failed: another read is in flight, the reply was unusable, or the view went away.</summary>
    Failed
}

/// <summary>Text read from a terminal session, with the reason when there is none.</summary>
/// <param name="Text">The terminal text, empty unless <paramref name="Status"/> is <see cref="TerminalTextReadStatus.Ok"/>.</param>
/// <param name="Truncated">True when the oldest output was cut, by the size cap or by the scrollback; the partial first line is then already dropped from the text.</param>
/// <param name="Status">How the read ended.</param>
public readonly record struct TerminalTextReadResult(string Text, bool Truncated, TerminalTextReadStatus Status)
{
    /// <summary>A result carrying no text and the given non-Ok status.</summary>
    public static TerminalTextReadResult Empty(TerminalTextReadStatus status) => new(string.Empty, false, status);

    /// <summary>
    /// Describes the result without its text, which can hold secrets: the generated member would
    /// print it into any log line or assertion message that formats the result.
    /// </summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"TerminalTextReadResult {{ Status = {Status}, Truncated = {Truncated}, TextLength = {Text.Length} }}");
}

/// <summary>
/// Abstraction for a host control that can hand back the text of its terminal session, so a
/// caller can offer a "copy output" command. Implemented by embedded terminal views
/// (e.g. <c>EmbeddedSshView</c>); panes without a text terminal (RDP, SFTP) do not implement it.
/// </summary>
/// <remarks>
/// The text is whatever the terminal shows, unmasked: it can hold secrets. Reading it touches
/// neither the clipboard, the disk nor the network; what happens to it is the caller's
/// responsibility. All members must be used from the UI thread.
/// </remarks>
public interface ITerminalTextSource
{
    /// <summary>
    /// True only when the terminal page is ready, WebView2 is usable and the view is not
    /// disposed. There is no change notification: callers poll it.
    /// </summary>
    bool CanReadText { get; }

    /// <summary>
    /// Reads the text of the terminal's normal screen buffer (never the alternate screen of a
    /// full-screen program): scrollback included, wrapped rows rejoined, newest output kept
    /// when the size cap cuts. Fails fast with <see cref="TerminalTextReadStatus.NotReady"/>
    /// when <see cref="CanReadText"/> is false. Only one read is in flight at a time: a call made
    /// while another is pending returns <see cref="TerminalTextReadStatus.Failed"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<TerminalTextReadResult> ReadTextAsync(CancellationToken cancellationToken);

    /// <summary>Gives the keyboard focus back to the terminal.</summary>
    void FocusTerminal();
}

/// <summary>
/// The request/reply state machine behind <see cref="ITerminalTextSource.ReadTextAsync"/>: it
/// numbers one request, parses and decodes the page's reply and completes the pending read.
/// Owned by the view; the view supplies the message transport.
/// </summary>
internal sealed class TerminalBufferReadCoordinator
{
    /// <summary>Host to page: asks the page for its text, followed by the request id.</summary>
    internal const string RequestPrefix = "buffer-request:";

    /// <summary>
    /// Page to host: <c>buffer:&lt;id&gt;:&lt;truncated 0|1&gt;:&lt;base64&gt;</c>. The flag is 1 when the
    /// text starts mid-line, because the size cap cut it or because the scrollback evicted the head
    /// of its first line.
    /// </summary>
    internal const string ReplyPrefix = "buffer:";

    /// <summary>Largest UTF-8 text the page sends; mirrors <c>bufferMaxBytes</c> in terminal.html.</summary>
    internal const int PageMaxTextBytes = 4 * 1024 * 1024;

    /// <summary>Slack above the page cap, so a page that cut exactly at its cap is never refused.</summary>
    internal const int ReplyMarginBytes = 64 * 1024;

    /// <summary>Decoded-byte cap of a <c>buffer:</c> reply, separate from the 1 MiB limit of the other messages.</summary>
    internal const int MaxReplyDecodedBytes = PageMaxTextBytes + ReplyMarginBytes;

    /// <summary>Base64 length of <see cref="MaxReplyDecodedBytes"/>.</summary>
    internal const int MaxReplyBase64Length = ((MaxReplyDecodedBytes + 2) / 3) * 4;

    /// <summary>How long a read waits for the page before ending as a timeout.</summary>
    internal const int ReadTimeoutMilliseconds = 10_000;

    /// <summary>Base64 length from which the decode runs off the UI thread.</summary>
    internal const int OffThreadDecodeMinBase64Length = 64 * 1024;

    private const char FieldSeparator = ':';
    private const string LineFeed = "\n";
    private const string TruncatedFlag = "1";
    private const string NotTruncatedFlag = "0";

    private sealed record PendingRead(int Id, TaskCompletionSource<TerminalTextReadResult> Completion);

    private readonly object _gate = new();
    private int _lastId;
    private PendingRead? _pending;

    /// <summary>True while a request is waiting for its reply.</summary>
    internal bool HasPendingRead
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>
    /// Posts one request through <paramref name="postRequest"/> and waits for its reply, the
    /// timeout or the token. Nothing is posted when <paramref name="canRead"/> is false (the
    /// transport would queue or drop the request and the read would wait for a reply that cannot
    /// come); a second call while one is pending fails fast.
    /// </summary>
    internal async Task<TerminalTextReadResult> ReadAsync(
        bool canRead,
        Action<string> postRequest,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postRequest);
        cancellationToken.ThrowIfCancellationRequested();

        if (!canRead)
        {
            return TerminalTextReadResult.Empty(TerminalTextReadStatus.NotReady);
        }

        PendingRead pending;
        lock (_gate)
        {
            if (_pending is not null)
            {
                return TerminalTextReadResult.Empty(TerminalTextReadStatus.Failed);
            }

            pending = new PendingRead(
                ++_lastId,
                new TaskCompletionSource<TerminalTextReadResult>(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending = pending;
        }

        try
        {
            postRequest(RequestPrefix + pending.Id.ToString(CultureInfo.InvariantCulture));
            return await pending.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return TerminalTextReadResult.Empty(TerminalTextReadStatus.Timeout);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }
        }
    }

    /// <summary>Completes the pending read, if any, as failed (the view is going away).</summary>
    internal void FailPending()
    {
        PendingRead? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        pending?.Completion.TrySetResult(TerminalTextReadResult.Empty(TerminalTextReadStatus.Failed));
    }

    /// <summary>
    /// Handles a <c>buffer:</c> message. A reply whose id is not the pending one (a late answer
    /// to a request that timed out) is discarded before any decoding. Never throws.
    /// </summary>
    internal async Task HandleReplyAsync(string message)
    {
        try
        {
            if (!TryParseReply(message, out int id, out bool truncated, out string base64))
            {
                FileLogger.Debug("EmbeddedSSH ignored a malformed terminal buffer reply.");
                return;
            }

            PendingRead? pending;
            lock (_gate)
            {
                pending = _pending;
            }

            if (pending is null || pending.Id != id)
            {
                FileLogger.Debug("EmbeddedSSH discarded a terminal buffer reply that matches no pending request.");
                return;
            }

            string? text = base64.Length >= OffThreadDecodeMinBase64Length
                ? await Task.Run(() => DecodeOrNull(base64)).ConfigureAwait(false)
                : DecodeOrNull(base64);

            // A cut text starts in the middle of a line: the key naming a secret may be gone
            // while its value remains, and no masking rule could recognize it. Drop that line.
            if (text is not null && truncated)
            {
                text = DropPartialFirstLine(text);
            }

            pending.Completion.TrySetResult(text is null
                ? TerminalTextReadResult.Empty(TerminalTextReadStatus.Failed)
                : new TerminalTextReadResult(text, truncated, TerminalTextReadStatus.Ok));
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"EmbeddedSSH terminal buffer reply failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Splits <c>buffer:&lt;id&gt;:&lt;0|1&gt;:&lt;base64&gt;</c> on its first three separators:
    /// the payload may be empty, the id must be a non-negative integer, the flag 0 or 1.
    /// </summary>
    internal static bool TryParseReply(string? message, out int id, out bool truncated, out string base64)
    {
        id = 0;
        truncated = false;
        base64 = string.Empty;

        if (message is null || !message.StartsWith(ReplyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> rest = message.AsSpan(ReplyPrefix.Length);

        int idEnd = rest.IndexOf(FieldSeparator);
        if (idEnd <= 0
            || !int.TryParse(rest[..idEnd], NumberStyles.None, CultureInfo.InvariantCulture, out id))
        {
            return false;
        }

        rest = rest[(idEnd + 1)..];
        int flagEnd = rest.IndexOf(FieldSeparator);
        if (flagEnd < 0)
        {
            return false;
        }

        ReadOnlySpan<char> flag = rest[..flagEnd];
        if (flag.SequenceEqual(TruncatedFlag))
        {
            truncated = true;
        }
        else if (!flag.SequenceEqual(NotTruncatedFlag))
        {
            return false;
        }

        base64 = rest[(flagEnd + 1)..].ToString();
        return true;
    }

    /// <summary>
    /// Removes everything up to and including the first line feed: the first line of a text
    /// whose head was cut is partial. A text without a line feed is one partial line and becomes empty.
    /// </summary>
    internal static string DropPartialFirstLine(string text)
    {
        int lineEnd = text.IndexOf(LineFeed, StringComparison.Ordinal);
        return lineEnd < 0 ? string.Empty : text[(lineEnd + 1)..];
    }

    /// <summary>Decodes a reply payload; false when it is over the reply cap or is not valid base64.</summary>
    internal static bool TryDecodeText(string base64, out string? text)
    {
        text = null;
        if (base64.Length > MaxReplyBase64Length)
        {
            return false;
        }

        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? DecodeOrNull(string base64) => TryDecodeText(base64, out string? text) ? text : null;
}
