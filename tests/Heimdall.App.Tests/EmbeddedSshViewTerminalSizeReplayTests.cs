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
/// The size the page reports before a session is attached is kept and replayed at attach time.
/// </summary>
/// <remarks>
/// <para>The page posts <c>ready:cols,rows</c> as soon as xterm has measured its surface, which is
/// before the SSH session exists for a view mounted in its "Connecting" state. The resize went to
/// a null session and was lost, so the PTY stayed at 80x24 until the user resized the window.</para>
/// <para>The view now remembers the last size it heard, from <c>ready:</c> and from <c>resize:</c>,
/// exposes it for the handler to create the PTY with, and replays it on both attach paths. Read
/// from source because the view needs a desktop to construct; every assertion is on an executable
/// line.</para>
/// </remarks>
public sealed class EmbeddedSshViewTerminalSizeReplayTests
{
    private const string MessageHandlerSignature =
        "private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)";

    [Theory]
    [InlineData("RememberTerminalSize(readyCols, readyRows);")]
    [InlineData("RememberTerminalSize(cols, rows);")]
    public void TheSizeThePageReportsIsRemembered(string statement)
    {
        string body = EmbeddedSshViewSourceReader.ExtractMethodBody(
            EmbeddedSshViewSourceReader.ReadViewSource(),
            MessageHandlerSignature);

        Assert.Contains(EmbeddedSshViewSourceReader.ExecutableLines(body), line => line == statement);
    }

    [Theory]
    [InlineData("public void AttachSession(")]
    [InlineData("public void AttachTerminalSession(")]
    public void EveryAttachReplaysTheRememberedSize(string signature)
    {
        string body = EmbeddedSshViewSourceReader.ExtractMethodBody(
            EmbeddedSshViewSourceReader.ReadViewSource(),
            signature);

        Assert.Contains(
            EmbeddedSshViewSourceReader.ExecutableLines(body),
            line => line == "ReplayLastKnownTerminalSize();");
    }

    [Fact]
    public void TheReplayResizesTheSessionToTheRememberedSize()
    {
        string body = EmbeddedSshViewSourceReader.ExtractMethodBody(
            EmbeddedSshViewSourceReader.ReadViewSource(),
            "private void ReplayLastKnownTerminalSize()");

        Assert.Contains(
            "ResizeSession(size.Columns, size.Rows);",
            EmbeddedSshViewSourceReader.ExecutableText(body),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The handler reads the remembered size through this member, so it has to be the same store.
    /// </summary>
    [Fact]
    public void TheRememberedSizeIsExposedForTheHandler()
    {
        string source = EmbeddedSshViewSourceReader.ReadViewSource();

        Assert.Contains("internal TerminalSize? LastKnownTerminalSize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultSizeIsTheOneTheSessionsAlreadyAssumed()
    {
        Assert.Equal(80, TerminalSize.DefaultColumns);
        Assert.Equal(24, TerminalSize.DefaultRows);
        Assert.Equal(new TerminalSize(80, 24), TerminalSize.Default);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(-1, 24)]
    public void ASizeWithoutAPositiveDimensionIsRefused(int columns, int rows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalSize(columns, rows));
    }

    /// <summary>
    /// Guards the guards above: the source being read carries the members they extract.
    /// </summary>
    [Fact]
    public void TheSourceBeingReadCarriesTheMessageHandlerAndBothAttaches()
    {
        string source = EmbeddedSshViewSourceReader.ReadViewSource();

        Assert.Contains(MessageHandlerSignature, source, StringComparison.Ordinal);
        Assert.Contains("public void AttachSession(", source, StringComparison.Ordinal);
        Assert.Contains("public void AttachTerminalSession(", source, StringComparison.Ordinal);
    }
}
