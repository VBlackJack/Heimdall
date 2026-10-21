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
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins Insert: the command reaches the prompt and nothing runs.
/// </summary>
/// <remarks>
/// Nothing running is a property of the bytes, and the bytes are decided below
/// <see cref="ITerminalCommandSink"/>, where every fake sink is blind. So the formatter is
/// asserted on what it returns, and the view's <c>WriteText</c> is read from the source the way
/// <see cref="TerminalSubmitKeyTests"/> reads <c>WriteCommand</c>.
/// </remarks>
public sealed class CommandLibraryInsertTests
{
    private static readonly TimeSpan RecordWait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Insert_WritesTheTextWithoutTheSubmitKey()
    {
        var inserted = TerminalCommandFormatter.Insert("ls -la");

        Assert.Equal("ls -la", inserted);
        Assert.DoesNotContain(AppConstants.TerminalSubmitKey, inserted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ls -la\n")]
    [InlineData("ls -la\r\n")]
    [InlineData("ls -la\r")]
    public void Insert_StripsATrailingLineBreak(string text)
    {
        Assert.Equal("ls -la", TerminalCommandFormatter.Insert(text));
    }

    // A break inside the text would submit the line before it, so it becomes a space.
    [Fact]
    public void Insert_LeavesNoLineBreakInside()
    {
        Assert.Equal("echo one echo two", TerminalCommandFormatter.Insert("echo one\necho two"));
    }

    [Fact]
    public void TheViewsWriteText_GoesThroughInsert()
    {
        string logic = SourceStatements.Method(
            SourceStatements.ViewLogic(),
            "public void WriteText(");

        SourceStatements.AssertStatementChain(
            logic,
            "WriteToSession(Services.TerminalCommandFormatter.Insert(text));");
    }

    [Fact]
    public void TheSessionHelper_WritesTextToTheFirstSink_Unsubmitted()
    {
        var sink = new RecordingSink();
        var root = new SessionPaneModel { HostControl = sink };

        Assert.True(EmbeddedSessionManager.TryInsertTextIntoFirstSink(root, "uptime"));

        Assert.Equal(["uptime"], sink.Inserted);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task Insert_HandsTheCommandToTheInsertHandler_AndRecordsItAsNotExecuted()
    {
        var history = new CommandLibraryHistoryHostTests.RecordingHistoryService();
        using var viewModel = await CommandLibraryHistoryHostTests.CreateSelectedViewModelAsync(history, "srv-01");
        var inserted = new List<string>();
        var sent = new List<string>();
        viewModel.InsertTextHandler = inserted.Add;
        viewModel.SendCommandHandler = sent.Add;
        viewModel.CanSendToTerminalProbe = () => true;

        viewModel.Insert();

        Assert.Equal(["uptime"], inserted);
        Assert.Empty(sent);
        var recorded = await history.NextAsync().WaitAsync(RecordWait);
        Assert.False(recorded.IsExecuted);
        Assert.Equal("srv-01", recorded.TargetHost);
    }

    // Nothing runs, so even a command applied from an example - which Send always confirms -
    // goes to the prompt without a question.
    [Fact]
    public async Task Insert_NeverAsksTheDangerousConfirmation()
    {
        var history = new CommandLibraryHistoryHostTests.RecordingHistoryService();
        using var viewModel = await CommandLibraryHistoryHostTests.CreateSelectedViewModelAsync(history, "srv-01");
        var inserted = new List<string>();
        viewModel.InsertTextHandler = inserted.Add;
        viewModel.CanSendToTerminalProbe = () => true;
        viewModel.ApplyExample("rm -rf /tmp/scratch");

        viewModel.Insert();

        Assert.Equal(["rm -rf /tmp/scratch"], inserted);
    }

    // Same gate as Send: no terminal in the tab, nothing to insert into.
    [Fact]
    public async Task Insert_WithoutATerminal_DoesNothing()
    {
        var history = new CommandLibraryHistoryHostTests.RecordingHistoryService();
        using var viewModel = await CommandLibraryHistoryHostTests.CreateSelectedViewModelAsync(history, "srv-01");
        var inserted = new List<string>();
        viewModel.InsertTextHandler = inserted.Add;
        viewModel.CanSendToTerminalProbe = () => false;

        viewModel.Insert();

        Assert.Empty(inserted);
    }

    private sealed class RecordingSink : ITerminalCommandSink
    {
        public List<string> Submitted { get; } = [];

        public List<string> Inserted { get; } = [];

        public void WriteCommand(string command) => Submitted.Add(command);

        public void WriteText(string text) => Inserted.Add(text);
    }
}
