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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The page answers a <c>buffer-request:</c> with the text of the normal buffer.
/// </summary>
/// <remarks>
/// <para>These tests pin the text of the shipped page only. The script runs in WebView2, not in
/// a test host, so the behaviour (wrapped rows rejoined, tail kept under the cap, the reply
/// arriving) is proven by the later UI test, not here.</para>
/// <para>Producer: <c>terminal.html</c>, <c>handleHostMessage</c>, branch <c>buffer-request:</c>,
/// triggered by <c>EmbeddedSshView.ReadTextAsync</c> posting that message. The assertions are
/// line-based so that a commented-out statement does not satisfy them.</para>
/// </remarks>
public sealed class TerminalHtmlBufferReadTests
{
    private const string RequestAnchor = "if (message.indexOf('buffer-request:') === 0)";
    private const string ReadAnchor = "function readNormalBufferText()";
    private const string EncodeAnchor = "function encodeBufferText(text)";
    private const string ChunkedAnchor = "function bytesToBase64Chunked(bytes)";

    [Fact]
    public void TheRequestHandlerRepliesWithAnIdCountedBufferMessage()
    {
        string[] lines = ExecutableLines(ExtractBlock(TerminalAssetsLoader.TerminalHtml, RequestAnchor));

        Assert.Contains(lines, line => line.Contains("encodeBufferText(readNormalBufferText())", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("postMessage('buffer:' + requestId + ':'", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("/^[0-9]+$/.test(requestId)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReplyPrefixesMatchTheHostConstants()
    {
        string html = TerminalAssetsLoader.TerminalHtml;

        Assert.Contains("'" + TerminalBufferReadCoordinator.RequestPrefix + "'", html, StringComparison.Ordinal);
        Assert.Contains("'" + TerminalBufferReadCoordinator.ReplyPrefix + "' + requestId", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadWalksTheNormalBufferNeverTheActiveOne()
    {
        string[] lines = ExecutableLines(ExtractBlock(TerminalAssetsLoader.TerminalHtml, ReadAnchor));

        Assert.Contains(lines, line => line.Contains("term.buffer.normal", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("buffer.active", StringComparison.Ordinal));
    }

    [Fact]
    public void AWrappedRowIsJoinedToThePreviousOneWithoutANewline()
    {
        string[] lines = ExecutableLines(ExtractBlock(TerminalAssetsLoader.TerminalHtml, ReadAnchor));

        Assert.Contains(lines, line => line.Contains("line.isWrapped", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("logicalLines[logicalLines.length - 1] += rowText;", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(".join('\\n')", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("translateToString(", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTextIsCappedByBytesAndTheTailIsKept()
    {
        string html = TerminalAssetsLoader.TerminalHtml;
        Assert.Contains(
            ExecutableLines(html),
            line => line == "var bufferMaxBytes = 4 * 1024 * 1024;");
        Assert.Equal(4 * 1024 * 1024, TerminalBufferReadCoordinator.PageMaxTextBytes);

        string[] lines = ExecutableLines(ExtractBlock(html, EncodeAnchor));
        Assert.Contains(lines, line => line.Contains("bytes.length > bufferMaxBytes", StringComparison.Ordinal));
        Assert.Contains(lines, line => line == "var start = bytes.length - bufferMaxBytes;");
        Assert.Contains(lines, line => line == "bytes = bytes.subarray(start);");
        Assert.Contains(lines, line => line == "truncated = true;");
    }

    [Fact]
    public void TheReplyIsEncodedWithTheChunkedEncoderNotTheCharAtATimeOne()
    {
        string html = TerminalAssetsLoader.TerminalHtml;

        string[] encode = ExecutableLines(ExtractBlock(html, EncodeAnchor));
        Assert.Contains(encode, line => line.Contains("bytesToBase64Chunked(bytes)", StringComparison.Ordinal));
        Assert.DoesNotContain(encode, line => line.Contains("bytesToBase64(", StringComparison.Ordinal));

        string[] chunked = ExecutableLines(ExtractBlock(html, ChunkedAnchor));
        Assert.Contains(chunked, line => line.Contains("String.fromCharCode.apply(null,", StringComparison.Ordinal));
        Assert.Contains(chunked, line => line.Contains("base64ChunkBytes", StringComparison.Ordinal));
        Assert.Contains(chunked, line => line.Contains("btoa(parts.join(''))", StringComparison.Ordinal));
        Assert.DoesNotContain(chunked, line => line.Contains("+= String.fromCharCode", StringComparison.Ordinal));
    }

    /// <summary>
    /// Guards the guard: the extraction reads the functions it claims to read.
    /// </summary>
    [Fact]
    public void TheSourceBeingReadCarriesTheNewFunctions()
    {
        string html = TerminalAssetsLoader.TerminalHtml;

        Assert.Contains(RequestAnchor, html, StringComparison.Ordinal);
        Assert.Contains(ReadAnchor, html, StringComparison.Ordinal);
        Assert.Contains(EncodeAnchor, html, StringComparison.Ordinal);
        Assert.Contains(ChunkedAnchor, html, StringComparison.Ordinal);
    }

    private static string[] ExecutableLines(string block) => block
        .Split('\n')
        .Select(static line => line.Trim())
        .Where(static line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
        .ToArray();

    private static string ExtractBlock(string source, string anchor)
    {
        int start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Anchor not found: {anchor}");

        int open = source.IndexOf('{', start + anchor.Length);
        Assert.True(open >= 0, $"No block for: {anchor}");

        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced block for: {anchor}");
    }
}
