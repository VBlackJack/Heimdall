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
/// A confirmed paste reaches the shell the way xterm.js pastes, and every paste gesture goes
/// through the host clipboard read that <c>SmartPasteGuard</c> sits behind.
/// </summary>
/// <remarks>
/// <para>The host confirms the clipboard text and posts it back to the page. The page used to
/// re-emit it as raw keystrokes (<c>input:</c>), which skips bracketed paste (mode 2004, honored
/// by bash 5.1+, zsh and vim) and turns a Windows <c>\r\n</c> into two submissions, so the line
/// after the pasted one was handed to whatever prompt came next. <c>term.paste(text)</c> applies
/// both the bracket wrapping and the newline normalisation that xterm.js gives a native paste.</para>
/// <para>Shift+Insert is a paste gesture too. Left to the browser it lands in the hidden textarea
/// as a native paste event that xterm.js forwards straight to the shell, and the host never sees
/// it. It has to take the same <c>clipboard-read:</c> road as Ctrl+V.</para>
/// <para>Read from the shipped page because the script runs in WebView2, not in a test host. The
/// assertions are line-based on purpose: a statement that has been commented out still contains
/// the text, and a comment must not make these pass.</para>
/// </remarks>
public sealed class TerminalHtmlPasteRoutingTests
{
    private const string ClipboardPasteAnchor = "if (message.indexOf('clipboard-paste:') === 0)";
    private const string KeyHandlerAnchor = "term.attachCustomKeyEventHandler(function (event)";
    private const string ShiftInsertAnchor =
        "if (event.shiftKey && !event.ctrlKey && !event.altKey && event.code === 'Insert')";

    [Fact]
    public void AConfirmedPasteIsDeliveredThroughXtermPaste()
    {
        string block = ExtractBlock(TerminalAssetsLoader.TerminalHtml, ClipboardPasteAnchor);

        Assert.Contains(ExecutableLines(block), line => line == "term.paste(pasteText);");
        Assert.DoesNotContain(
            ExecutableLines(block),
            line => line.StartsWith("postMessage('input:'", StringComparison.Ordinal));
    }

    [Fact]
    public void ShiftInsertIsRoutedThroughTheHostClipboardRead()
    {
        string handler = ExtractBlock(TerminalAssetsLoader.TerminalHtml, KeyHandlerAnchor);
        string branch = ExtractBlock(handler, ShiftInsertAnchor);
        string[] lines = ExecutableLines(branch);

        Assert.Contains(lines, line => line == "event.preventDefault();");
        Assert.Contains(lines, line => line == "postMessage('clipboard-read:');");
        Assert.Contains(lines, line => line == "return false;");
    }

    /// <summary>
    /// The paste keeps taking the road the guard watches: Ctrl+V still asks the host.
    /// </summary>
    [Fact]
    public void ControlVStillAsksTheHostForTheClipboard()
    {
        string handler = ExtractBlock(TerminalAssetsLoader.TerminalHtml, KeyHandlerAnchor);
        string branch = ExtractBlock(handler, "if (event.ctrlKey && (event.code === 'KeyV'))");

        Assert.Contains(ExecutableLines(branch), line => line == "postMessage('clipboard-read:');");
    }

    /// <summary>
    /// Guards the guard: the vendored xterm.js has the API the page now relies on.
    /// </summary>
    [Fact]
    public void TheVendoredXtermExposesPaste()
    {
        Assert.Contains("paste(e){", TerminalAssetsLoader.XtermJs, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards the guard: the extraction reads the handlers it claims to read.
    /// </summary>
    [Fact]
    public void TheSourceBeingReadCarriesBothHandlers()
    {
        string html = TerminalAssetsLoader.TerminalHtml;

        Assert.Contains(ClipboardPasteAnchor, html, StringComparison.Ordinal);
        Assert.Contains(KeyHandlerAnchor, html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lines that can execute: trimmed, with blank and comment-only lines removed.
    /// </summary>
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
