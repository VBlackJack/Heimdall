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
using System.Text.RegularExpressions;
using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes what the connection phase says while the user is cancelling.
/// </summary>
/// <remarks>
/// <para>Cancelling a reconnection used to move the view into <c>Preparing</c>, a phase that means a
/// connection is being prepared. Three surfaces read it literally: the phase stepper lit its first
/// segment, the connect-cancel button appeared in place of the reconnect-cancel button the user had
/// just clicked and carries the identical label, and the connect watchdog was armed, whose expiry
/// raises a reconnect overlay on a session the user asked to abandon.</para>
/// <para>The sibling handler for cancelling an in-progress connection had always used <c>None</c>.
/// Two handlers doing the same kind of thing said different things, and each was self-consistent, so
/// nothing failed.</para>
/// <para>What is frozen below is not the name <c>None</c>. It is that the two handlers agree, and
/// that whatever phase they agree on does not read as a connection in progress. A phase added later
/// specifically for cancellation would satisfy both, which is the point.</para>
/// </remarks>
public sealed class RdpCancelPhaseAgreementTests
{
    private const string ReconnectCancelHandler = "private void OnCancelReconnectClick";
    private const string ConnectCancelHandler = "private void OnCancelConnectClick";

    [Fact]
    public void BothCancelHandlersLeaveTheViewInTheSamePhase()
    {
        RdpConnectionPhase reconnectCancel = PhaseSetBy(ReconnectCancelHandler);
        RdpConnectionPhase connectCancel = PhaseSetBy(ConnectCancelHandler);

        Assert.True(
            reconnectCancel == connectCancel,
            $"Cancelling a reconnection leaves the view in {reconnectCancel} while cancelling a "
                + $"connection leaves it in {connectCancel}. Both are cancellations and every "
                + "surface reading the phase will describe them differently.");
    }

    [Fact]
    public void TheCancelPhaseDoesNotReadAsAConnectionInProgress()
    {
        RdpConnectionPhase phase = PhaseSetBy(ReconnectCancelHandler);

        Assert.Equal(0, RdpConnectionPhasePolicy.GetLitSegmentCount(phase));
        Assert.Null(RdpConnectionPhasePolicy.GetStatusKey(phase));

        (bool cancelConnectVisible, bool disconnectVisible) =
            RdpConnectionPhasePolicy.ResolveVisibility(phase);
        Assert.False(
            cancelConnectVisible,
            "The connect-cancel button would appear in place of the reconnect-cancel button the "
                + "user just clicked, and both are labelled _Cancel.");
        Assert.False(disconnectVisible);

        Assert.False(
            RdpConnectWatchdogPolicy.ShouldArm(phase),
            "Arming the connect watchdog on a cancellation means its expiry raises a reconnect "
                + "overlay on a session the user asked to abandon.");
        Assert.True(RdpConnectWatchdogPolicy.ShouldCancel(phase));
    }

    /// <summary>
    /// Both handlers must reach the phase transition, not merely contain one.
    /// </summary>
    /// <remarks>
    /// <para>The extraction below finds the first textual occurrence, which is the same whether the
    /// statement always runs or sits behind four calls that can throw. The two handlers already
    /// differed that way: one sets the phase before its try block, the other set it inside, after
    /// four calls, with the catch routing to the failure handler and the phase never cleared.</para>
    /// <para>The consequences are the three this file already names: the connect-cancel button
    /// stays on screen carrying the same _Cancel label, and the watchdog stays armed, so its expiry
    /// raises a reconnect overlay on the session the user just abandoned.</para>
    /// </remarks>
    [Fact]
    public void BothCancelHandlersSetThePhaseBeforeAnythingThatCanThrow()
    {
        AssertPhaseIsSetBeforeTheTryBlock(ReconnectCancelHandler);
        AssertPhaseIsSetBeforeTheTryBlock(ConnectCancelHandler);
    }

    private static void AssertPhaseIsSetBeforeTheTryBlock(string handlerSignature)
    {
        string body = HandlerBody(handlerSignature);

        Match phase = Regex.Match(body, @"TransitionPhase\(RdpConnectionPhase\.(\w+)\)");
        Assert.True(phase.Success, $"{handlerSignature} no longer sets a connection phase.");

        // The try block itself, not the word: prose above the statement mentions it.
        Match tryBlock = Regex.Match(body, @"(?m)^\s*try\s*$");
        Assert.True(tryBlock.Success, $"{handlerSignature} no longer has a try block to measure against.");

        Assert.True(
            phase.Index < tryBlock.Index,
            $"{handlerSignature} sets the connection phase inside its try block, so a throw from "
                + "any statement before it leaves the view describing a connection in progress that "
                + "the user has just abandoned.");
    }

    // The phase this all turns on is read out of the source, so the two tests above assert nothing
    // if that read stops working. This one fails loudly instead of letting them pass empty.
    [Fact]
    public void ThePhaseIsActuallyReadFromBothHandlers()
    {
        string source = ReadViewSource();

        Assert.Contains(ReconnectCancelHandler, source, System.StringComparison.Ordinal);
        Assert.Contains(ConnectCancelHandler, source, System.StringComparison.Ordinal);

        // And the extraction really finds a phase in each, rather than defaulting to one.
        Assert.True(System.Enum.IsDefined(PhaseSetBy(ReconnectCancelHandler)));
        Assert.True(System.Enum.IsDefined(PhaseSetBy(ConnectCancelHandler)));
    }

    private static string HandlerBody(string handlerSignature)
    {
        string source = ReadViewSource();
        int start = source.IndexOf(handlerSignature, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"handler not found in the view: {handlerSignature}");

        // The handler body ends at the next method declaration at the same indentation.
        Match next = Regex.Match(
            source[(start + handlerSignature.Length)..],
            @"(?m)^    (private|public|internal|protected)\s");
        return next.Success
            ? source.Substring(start, handlerSignature.Length + next.Index)
            : source[start..];
    }

    private static RdpConnectionPhase PhaseSetBy(string handlerSignature)
    {
        string body = HandlerBody(handlerSignature);

        Match phase = Regex.Match(body, @"TransitionPhase\(RdpConnectionPhase\.(\w+)\)");
        Assert.True(
            phase.Success,
            $"{handlerSignature} no longer sets a connection phase, so this file is asserting "
                + "nothing about it.");

        return System.Enum.Parse<RdpConnectionPhase>(phase.Groups[1].Value);
    }

    private static string ReadViewSource() => File.ReadAllText(Path.Combine(
        FindRepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedRdpView.xaml.cs"));

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root from test binary directory: {AppContext.BaseDirectory}");
    }
}
