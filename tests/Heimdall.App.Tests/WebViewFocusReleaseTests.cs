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

using System.IO;
using System.Windows.Input;
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// Ctrl+Alt+Home leaves a session hosted in WebView2, as it already left an RDP session. Once a
/// terminal, a VNC viewer or a web console had the keyboard, Tab belonged to the page and the
/// session header could not be reached again without the mouse (WCAG 2.1.2).
/// </summary>
public sealed class WebViewFocusReleaseTests
{
    private const ModifierKeys CtrlAlt = ModifierKeys.Control | ModifierKeys.Alt;

    [Fact]
    public void TheGestureIsTheOneTheRdpSurfaceAnswers()
    {
        RdpShortcut rdp = RdpDefaultShortcuts.ReleaseFocus;

        Assert.True(WebViewFocusRelease.IsGesture(rdp.Key, Key.None, rdp.Modifiers));
    }

    [Fact]
    public void TheGestureIsRecognisedWhenAltTurnsTheKeyIntoASystemKey()
    {
        // With Alt held WPF reports Key.System and puts the real key in SystemKey.
        Assert.True(WebViewFocusRelease.IsGesture(Key.System, Key.Home, CtrlAlt));
    }

    [Theory]
    [InlineData(Key.Home, ModifierKeys.Control)]
    [InlineData(Key.Home, ModifierKeys.Alt)]
    [InlineData(Key.Home, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)]
    [InlineData(Key.End, ModifierKeys.Control | ModifierKeys.Alt)]
    public void AnythingElseReachesThePage(Key key, ModifierKeys modifiers)
    {
        Assert.False(WebViewFocusRelease.IsGesture(key, Key.None, modifiers));
    }

    /// <summary>
    /// The terminal and VNC pages post exactly the message their views listen for, from the
    /// gesture's own block, with the exact modifiers RDP uses.
    /// </summary>
    [Theory]
    [InlineData("terminal.html")]
    [InlineData("vnc.html")]
    public void ThePagePostsTheReleaseMessageOnTheGesture(string page)
    {
        string script = File.ReadAllText(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Assets", page));

        StatementsOf(BlockAfter(script, GestureCondition)).Should().Contain([
            "event.preventDefault();",
            $"postMessage('{WebViewFocusRelease.Message}');",
        ]);
    }

    private const string GestureCondition =
        "if (event.ctrlKey && event.altKey && !event.shiftKey && !event.metaKey && event.key === 'Home')";

    private static string BlockAfter(string source, string anchor)
    {
        int at = source.IndexOf(anchor, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the page catches {anchor}");

        int open = source.IndexOf('{', at + anchor.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[(open + 1)..i];
            }
        }

        throw new InvalidOperationException($"The block after {anchor} is not closed.");
    }

    /// <summary>Each line of a block, trimmed, so a statement matches only when it stands alone.</summary>
    private static IEnumerable<string> StatementsOf(string block) =>
        block.Split('\n').Select(line => line.Trim());

    [Fact]
    public void TheTerminalViewActsOnTheReleaseMessage()
    {
        string logic = Logic("EmbeddedSshView.xaml.cs", SshMessageHandler);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, ReleaseMessageTest), "the SSH view drops the release message");
    }

    [Fact]
    public void TheVncViewActsOnTheReleaseMessage()
    {
        string logic = Logic("EmbeddedVncView.xaml.cs", VncMessageHandler);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, ReleaseMessageTest), "the VNC view drops the release message");
    }

    [Fact]
    public void TheWebViewListensForTheGestureFromConstruction()
    {
        string logic = Logic("EmbeddedWebView.xaml.cs", WebViewConstructor);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, SubscribeGestureStatement), "the web view never listens for the gesture");
    }

    private const string SshMessageHandler = "private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)";
    private const string VncMessageHandler = "private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)";
    private const string WebViewConstructor = "public EmbeddedWebView()";
    private const string ReleaseMessageTest = "if (message == WebViewFocusRelease.Message)";
    private const string SubscribeGestureStatement = "ConsoleWebView.PreviewKeyDown += OnConsoleWebViewPreviewKeyDown;";

    private static string Logic(string file, string signature)
    {
        string source = File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", file));
        return ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), signature);
    }
}
