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
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The terminal's half of "save the selection to the Command Library": what the page posts on
/// Ctrl+Shift+L and Shift+right-click, how the host reads it, the host's one-item menu, and the
/// command line a multi-line selection becomes.
/// </summary>
/// <remarks>
/// The page script runs in WebView2, not in a test host, so the page tests pin the text of the
/// shipped page, line by line so that a commented-out statement does not satisfy them. No test
/// here presses the keys in a rendered terminal.
/// </remarks>
public sealed class TerminalLibraryCaptureTests
{
    private const string ChordAnchor =
        "if (event.ctrlKey && event.shiftKey && !event.altKey && !event.metaKey && event.keyCode === libraryCaptureKeyCode)";

    private const string ContextMenuAnchor = "terminalElement.addEventListener('contextmenu', function (event)";

    private const string ShiftAnchor = "if (event.shiftKey)";

    // ── Page ─────────────────────────────────────────────────────

    [Fact]
    public void TheChordPostsTheSelectionOnlyWhenThereIsOne()
    {
        string[] lines = ExecutableLines(ExtractBlock(TerminalAssetsLoader.TerminalHtml, ChordAnchor));

        Assert.Equal(
            [
                "{",
                "event.preventDefault();",
                "var librarySelection = term.getSelection();",
                "if (librarySelection) postMessage('library-save:' + stringToBase64(librarySelection));",
                "return false;",
                "}",
            ],
            lines);
    }

    [Fact]
    public void TheChordIsMatchedByTheKeyCodeOfL()
    {
        Assert.Contains(
            ExecutableLines(TerminalAssetsLoader.TerminalHtml),
            line => line == "var libraryCaptureKeyCode = 76;");
    }

    [Fact]
    public void ShiftRightClickAsksForTheMenuWithTheSelectionAndPlainRightClickStillPastes()
    {
        string handler = ExtractBlock(TerminalAssetsLoader.TerminalHtml, ContextMenuAnchor);

        Assert.Equal(
            [
                "{",
                "postMessage('library-menu:' + stringToBase64(term.getSelection()));",
                "return;",
                "}",
            ],
            ExecutableLines(ExtractBlock(handler, ShiftAnchor)));

        string[] lines = ExecutableLines(handler);
        Assert.Equal("event.preventDefault();", lines[1]);
        Assert.Equal("postMessage('clipboard-read:');", lines[^2]);
    }

    [Fact]
    public void TheHostListensForThePrefixesThePagePosts()
    {
        Assert.Equal("library-save:", TerminalLibraryCapture.SaveMessagePrefix);
        Assert.Equal("library-menu:", TerminalLibraryCapture.MenuMessagePrefix);
    }

    // ── Host: reading the messages ───────────────────────────────

    [Fact]
    public void ASaveMessageCarriesTheDecodedSelection()
    {
        string message = "library-save:" + Base64("systemctl status nginx\njournalctl -u nginx");

        bool consumed = TerminalLibraryCapture.TryRead(message, out TerminalLibraryRequest request, out string selection);

        Assert.True(consumed);
        Assert.Equal(TerminalLibraryRequest.Save, request);
        Assert.Equal("systemctl status nginx\njournalctl -u nginx", selection);
    }

    [Fact]
    public void ASaveWithABlankSelectionIsConsumedAndAsksNothing()
    {
        bool consumed = TerminalLibraryCapture.TryRead("library-save:" + Base64("  \n "), out TerminalLibraryRequest request, out _);

        Assert.True(consumed);
        Assert.Equal(TerminalLibraryRequest.None, request);
    }

    [Fact]
    public void AMenuMessageWithoutASelectionStillOpensTheMenu()
    {
        bool consumed = TerminalLibraryCapture.TryRead("library-menu:", out TerminalLibraryRequest request, out string selection);

        Assert.True(consumed);
        Assert.Equal(TerminalLibraryRequest.Menu, request);
        Assert.Equal(string.Empty, selection);
    }

    [Fact]
    public void AMalformedPayloadIsConsumedAndAsksNothing()
    {
        bool consumed = TerminalLibraryCapture.TryRead("library-save:%%%", out TerminalLibraryRequest request, out _);

        Assert.True(consumed);
        Assert.Equal(TerminalLibraryRequest.None, request);
    }

    [Fact]
    public void AnotherMessageIsLeftToTheOtherHandlers()
    {
        Assert.False(TerminalLibraryCapture.TryRead("clipboard-write:" + Base64("ls"), out _, out _));
    }

    // ── Host: the menu ───────────────────────────────────────────

    [Fact]
    public void TheMenuEntryIsDisabledWithoutASelection()
    {
        StaDispatcherRunner.Run(() =>
        {
            MenuItem item = SingleItem(TerminalLibraryCapture.CreateMenu(string.Empty, "Save", _ => { }));

            Assert.False(item.IsEnabled);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void TheMenuEntrySavesTheSelectionItWasOpenedWith()
    {
        StaDispatcherRunner.Run(() =>
        {
            string? saved = null;
            MenuItem item = SingleItem(TerminalLibraryCapture.CreateMenu("df -h", "Save", text => saved = text));

            Assert.True(item.IsEnabled);
            Assert.Equal("Save", item.Header);
            Assert.Equal("Save", System.Windows.Automation.AutomationProperties.GetName(item));

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal("df -h", saved);
            return Task.CompletedTask;
        });
    }

    // ── The command line a selection becomes ─────────────────────

    [Fact]
    public void AMultiLineLinuxSelectionIsChainedWithAnd()
    {
        string line = CommandCapture.ToCommandLine("  cd /var/log \r\n\r\n tail -n 50 syslog\n", RemoteOs.Linux);

        Assert.Equal("cd /var/log && tail -n 50 syslog", line);
    }

    [Fact]
    public void AMultiLineWindowsSelectionIsChainedWithASemicolon()
    {
        string line = CommandCapture.ToCommandLine("Get-Service\r\nGet-Process", RemoteOs.Windows);

        Assert.Equal("Get-Service ; Get-Process", line);
    }

    [Fact]
    public void AOneLineSelectionIsOnlyTrimmed()
    {
        Assert.Equal("uptime", CommandCapture.ToCommandLine("  uptime \r\n", RemoteOs.Linux));
    }

    [Theory]
    [InlineData(RemoteOs.Auto, "SSH", RemoteOs.Linux)]
    [InlineData(RemoteOs.Auto, "RDP", RemoteOs.Windows)]
    [InlineData(RemoteOs.Auto, "WINRM", RemoteOs.Windows)]
    [InlineData(RemoteOs.Auto, "LOCAL", RemoteOs.Windows)]
    [InlineData(RemoteOs.Auto, "TELNET", RemoteOs.Linux)]
    [InlineData(RemoteOs.Auto, null, RemoteOs.Linux)]
    [InlineData(RemoteOs.Windows, "SSH", RemoteOs.Windows)]
    [InlineData(RemoteOs.Linux, "LOCAL", RemoteOs.Linux)]
    public void ACaptureIsWrittenForOnePlatform(RemoteOs declared, string? connectionType, RemoteOs expected)
    {
        Assert.Equal(expected, CommandCapture.ResolvePlatform(declared, connectionType));
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static MenuItem SingleItem(ContextMenu menu) => Assert.IsType<MenuItem>(Assert.Single(menu.Items));

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

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
