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

using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.Views;

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
/// from source because the view needs a desktop to construct; each anchor is carried through the
/// statement predicate, nested sites as a chain.</para>
/// </remarks>
public sealed class EmbeddedSshViewTerminalSizeReplayTests
{
    private const string MessageHandlerSignature =
        "private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)";

    [Fact]
    public void TheReadySizeIsRemembered()
    {
        string logic = SourceStatements.Method(SourceStatements.ViewLogic(), MessageHandlerSignature);

        SourceStatements.AssertStatementChain(
            logic,
            "if (message.StartsWith(MsgReady, StringComparison.Ordinal))",
            "if (TryParseSize(message.AsSpan(MsgReady.Length), out int readyCols, out int readyRows))",
            "RememberTerminalSize(readyCols, readyRows);");
    }

    [Fact]
    public void TheResizeSizeIsRemembered()
    {
        string logic = SourceStatements.Method(SourceStatements.ViewLogic(), MessageHandlerSignature);

        SourceStatements.AssertStatementChain(
            logic,
            "if (message.StartsWith(MsgResize, StringComparison.Ordinal))",
            "if (TryParseSize(message.AsSpan(MsgResize.Length), out int cols, out int rows))",
            "RememberTerminalSize(cols, rows);");
    }

    [Theory]
    [InlineData("public void AttachSession(")]
    [InlineData("public void AttachTerminalSession(")]
    public void EveryAttachReplaysTheRememberedSize(string signature)
    {
        string logic = SourceStatements.Method(SourceStatements.ViewLogic(), signature);

        SourceStatements.AssertStatementChain(logic, "ReplayLastKnownTerminalSize();");
    }

    [Fact]
    public void TheReplayResizesTheSessionToTheRememberedSize()
    {
        string logic = SourceStatements.Method(
            SourceStatements.ViewLogic(),
            "private void ReplayLastKnownTerminalSize()");

        SourceStatements.AssertStatementChain(
            logic,
            "if (Volatile.Read(ref _lastKnownTerminalSize) is { } size)",
            "ResizeSession(size.Columns, size.Rows);");
    }

    /// <summary>
    /// The handler reads the remembered size through this member, so it has to be the same store.
    /// </summary>
    [Fact]
    public void TheRememberedSizeIsExposedForTheHandler()
    {
        PropertyInfo? property = typeof(EmbeddedSshView).GetProperty(
            "LastKnownTerminalSize",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(property);
        Assert.Equal(typeof(TerminalSize), property.PropertyType);
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
}
