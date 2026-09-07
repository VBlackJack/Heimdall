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

using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// Every privileged control command carries a finite bound of its own.
/// </summary>
/// <remarks>
/// <para>SSH.NET leaves <c>SshCommand.CommandTimeout</c> at
/// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>, measured against the shipped package on
/// a live server: a freshly created command reports the infinite value, and a command run against a
/// wedged server was still running when the harness gave up. The caller's token does not close that
/// gap. A token handed to <c>Task.Run</c> only stops a delegate that has not started yet, and
/// several sudo call sites (mkdir, chmod) pass no token at all, so nothing outside the command can
/// end an exec channel that the server accepted and then never answered.</para>
/// <para>Read from source because reaching either path needs an authenticated
/// <c>SshClient</c> against a real server, which the blocking lane has not got. Each anchor is
/// carried through the statement predicate, so a bound folded behind a term that is false by
/// construction does not keep this green; <see cref="TheReaderRefusesABoundFoldedOutOfTheBody"/>
/// proves that predicate can still go red on exactly this anchor.</para>
/// </remarks>
public sealed class EmbeddedSftpSudoCommandTimeoutTests
{
    private const string KeyOnlySignature =
        "private async Task<Renci.SshNet.SshCommand> ExecuteSudoBodyAsync(";

    private const string PasswordSignature =
        "private static async Task<Renci.SshNet.SshCommand> ExecuteSudoBodyWithPasswordAsync(";

    private const string KeyOnlyBranch = "if (!authenticateViaStdin)";

    private const string BoundStatement = "command.CommandTimeout = SudoCommandTimeout;";

    private static string ViewModelLogic() =>
        SourceStatements.Logic("src", "Heimdall.App", "ViewModels", "EmbeddedSftpViewModel.cs");

    /// <summary>
    /// The bound the two paths share is a real deadline, not the infinite default it replaced.
    /// </summary>
    [Fact]
    public void ThePrivilegedCommandBoundIsFinite()
    {
        Assert.NotEqual(Timeout.InfiniteTimeSpan, EmbeddedSftpViewModel.SudoCommandTimeout);
        Assert.True(
            EmbeddedSftpViewModel.SudoCommandTimeout > TimeSpan.Zero,
            $"A privileged command bound of {EmbeddedSftpViewModel.SudoCommandTimeout} would refuse "
                + "every command instead of bounding it.");
    }

    /// <summary>
    /// The key-only path bounds the command it is about to run.
    /// </summary>
    [Fact]
    public void TheKeyOnlySudoPathBoundsItsCommand()
    {
        string logic = SourceStatements.Method(ViewModelLogic(), KeyOnlySignature);

        SourceStatements.AssertStatementChain(logic, KeyOnlyBranch, BoundStatement);
    }

    /// <summary>
    /// The key-only path no longer runs the shape that could not be bounded: <c>RunCommand</c>
    /// builds its command internally, so there is no object to set a deadline on before it blocks.
    /// </summary>
    [Fact]
    public void TheKeyOnlySudoPathNoLongerRunsAnUnboundableCommand()
    {
        string logic = SourceStatements.Method(ViewModelLogic(), KeyOnlySignature);

        Assert.DoesNotContain("ssh.RunCommand(", logic, StringComparison.Ordinal);
    }

    /// <summary>
    /// The password path bounds its command too. It is interruptible where the key-only path was
    /// not, but interruptible is not bounded: a call site that passes no token had nothing.
    /// </summary>
    [Fact]
    public void ThePasswordSudoPathBoundsItsCommand()
    {
        string logic = SourceStatements.Method(ViewModelLogic(), PasswordSignature);

        SourceStatements.AssertStatementChain(logic, "try", BoundStatement);
    }

    /// <summary>
    /// Guards the guard: the anchor these tests read would be refused if it were folded behind a
    /// term that is false by construction, rather than merely deleted.
    /// </summary>
    [Fact]
    public void TheReaderRefusesABoundFoldedOutOfTheBody()
    {
        const string Written = "void M()\r\n{\r\n    " + BoundStatement + "\r\n}\r\n";
        const string Folded =
            "void M()\r\n{\r\n    if (commandText.Length < 0) { " + BoundStatement + " }\r\n}\r\n";

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(Written, BoundStatement),
            "The statement predicate refused a bound written as a step of the body, so the tests "
                + "above pass for a reason other than the one they name.");
        Assert.False(
            ViewSource.IsStatementOfTheMethodBody(Folded, BoundStatement),
            "The statement predicate accepted a bound that only runs inside a false branch, so "
                + "nothing these tests read means anything.");
    }
}
