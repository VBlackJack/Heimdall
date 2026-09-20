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

namespace Heimdall.App.Tests;

/// <summary>
/// Every surface that submits a line to a terminal submits it the same way.
/// </summary>
/// <remarks>
/// <para>Two of them disagreed. The local file browser went through
/// <see cref="TerminalCommandFormatter"/>, the Command Library and the broadcast panel went
/// through the view's own <c>WriteCommand</c>, and both spelled their own terminator. Measured
/// 2026-09-20 against a live ConPTY, a line feed leaves Windows PowerShell on its
/// "&gt;&gt; " continuation prompt with the command typed and unexecuted, so on a Local Shell
/// or a WinRM session neither surface ran anything.</para>
/// <para>Fixing one of them would not have reached the other, which is the reason the decision
/// now has a single name and these tests pin the name rather than each spelling.</para>
/// </remarks>
public sealed class TerminalSubmitKeyTests
{
    /// <summary>
    /// The submit key is a carriage return and carries no line feed.
    /// </summary>
    /// <remarks>
    /// Stated here rather than only inside each caller: this is the decision the callers share,
    /// and the value a revert would change.
    /// </remarks>
    [Fact]
    public void TheSubmitKey_IsACarriageReturn()
    {
        Assert.Equal("\r", AppConstants.TerminalSubmitKey);
        Assert.DoesNotContain('\n', AppConstants.TerminalSubmitKey);
    }

    /// <summary>
    /// The command formatter submits with the shared key, for every shell it knows.
    /// </summary>
    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("cmd.exe")]
    [InlineData("bash.exe")]
    public void TheCommandFormatter_SubmitsWithTheSharedKey(string shell)
    {
        string cd = TerminalCommandFormatter.FormatCd(shell, @"C:\Temp");
        string run = TerminalCommandFormatter.FormatRun(shell, @"C:\Temp\tool.exe");

        Assert.EndsWith(AppConstants.TerminalSubmitKey, cd, StringComparison.Ordinal);
        Assert.EndsWith(AppConstants.TerminalSubmitKey, run, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', cd);
        Assert.DoesNotContain('\n', run);
    }

    /// <summary>
    /// The view's WriteCommand appends the shared key, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>Read from the source because the terminator lives below
    /// <see cref="ITerminalCommandSink"/>: every existing test of the send path writes through
    /// a fake sink that records the command and appends nothing, so none of them could see
    /// what the real implementor sent. That is exactly how the line feed survived.</para>
    /// <para><see cref="SourceStatements.ViewLogic"/> blanks literals, so a revert to a
    /// <c>"\n"</c> literal reads as <c>+ ""</c> here and the anchor fails. That is the
    /// discriminating mutant, and the reason the decision had to become a named constant
    /// before it could be guarded at all.</para>
    /// </remarks>
    [Fact]
    public void TheViewsWriteCommand_AppendsTheSharedKey()
    {
        string logic = SourceStatements.Method(
            SourceStatements.ViewLogic(),
            "public void WriteCommand(");

        SourceStatements.AssertStatementChain(
            logic,
            "WriteToSession(command + AppConstants.TerminalSubmitKey);");
    }
}
