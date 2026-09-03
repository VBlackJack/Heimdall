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
/// Freezes the decision taken on a connect that completes after the attempt was abandoned, and
/// checks that the view still hands its four connect events to the arbiter that takes it.
/// </summary>
/// <remarks>
/// <para>Cancel asks the control to disconnect, but a handshake already in flight can finish
/// first, so OnConnected can arrive afterwards. The watchdog's abort was guarded against exactly
/// that. The user's Cancel was not, and it is the same abort: the session came up live behind a
/// cancelled attempt, and the user-disconnect flag it had raised stayed raised, so the next
/// genuine drop of that live session was read as a user disconnect - no overlay, no diagnostic, a
/// session dying in silence.</para>
/// <para><b>What is measured where.</b> The decision itself is a pure function, asserted below.
/// The sequence built on it - cancel, then a retry, then a late connect - is run against a
/// recording runner in <see cref="RdpConnectAttemptArbiterTests"/>, which is where every
/// behavioural claim about this branch lives. What is left in the code-behind is one delegating
/// statement per event, in handlers whose surroundings need a live <c>Application</c> and an
/// ActiveX control, so those four statements are read from the view's source.</para>
/// <para><b>What that reading proves, and nothing beyond it: that the call site exists, as a step
/// of its method body.</b> It is blanked of comments and literals first, and each call is required
/// to stand at the body's own brace depth rather than merely to appear in the file, so a call left
/// behind in a comment or moved inside a condition is rejected - the positive controls below
/// mutate the real source and require this file's predicate to reject each mutant. It still cannot
/// say the call RUNS: every one of these handlers has conditional early returns above the site,
/// and deciding whether those are taken would mean evaluating them. An earlier version of this
/// file read raw handler text and claimed to detect "a call that has been deleted"; commenting the
/// call out left it green, which is why the blanking and the controls are here.</para>
/// </remarks>
public sealed class RdpLateConnectPolicyTests
{
    private const string CancelMember =
        "private void OnCancelConnectClick(object sender, RoutedEventArgs e)";
    private const string BeginMember = "private void BeginConnect()";
    private const string ContinueMember = "private void ContinueConnectAttempt(int attempt)";
    private const string ConnectedMember = "private void OnRdpConnected()";
    private const string RetryMember = "private async Task RetryBeginConnectAsync(int attempt)";

    // What the retry must dispatch, and the pre-fix shape it must not.
    private const string ResumeDispatch = "new Action(() => ContinueConnectAttempt(attempt))";
    private const string ReopenDispatch = "new Action(BeginConnect)";

    // The four statements that join the view to the arbiter. The two branching ones carry their
    // whole condition: what has to be written there is the refusal being acted on, not a call
    // whose answer is dropped.
    private const string CancelCall = "_connectAttempts.UserCancelled();";
    private const string BeginCall = "_connectAttempts.UserRequestedConnect();";
    private const string ContinueCall =
        "if (_connectAttempts.RetryArrived(attempt, _disposed) == RdpConnectRetryAdmission.Refuse)";
    private const string ConnectedCall =
        "if (_connectAttempts.ConnectArrived() == RdpLateConnectDecision.Refuse)";

    [Fact]
    public void AConnectAbandonedByTheUserIsRefusedJustLikeOneAbandonedByTheWatchdog()
    {
        Assert.Equal(
            RdpLateConnectDecision.Refuse,
            RdpLateConnectPolicy.Resolve(abandonedByWatchdog: false, abandonedByUser: true));
    }

    [Fact]
    public void AnAttemptNobodyAbandonedIsPromoted()
    {
        Assert.Equal(
            RdpLateConnectDecision.Promote,
            RdpLateConnectPolicy.Resolve(abandonedByWatchdog: false, abandonedByUser: false));
    }

    [Fact]
    public void TheWatchdogAbortStaysRefused()
    {
        Assert.Equal(
            RdpLateConnectDecision.Refuse,
            RdpLateConnectPolicy.Resolve(abandonedByWatchdog: true, abandonedByUser: false));
        Assert.Equal(
            RdpLateConnectDecision.Refuse,
            RdpLateConnectPolicy.Resolve(abandonedByWatchdog: true, abandonedByUser: true));
    }

    /// <summary>
    /// Each connect event of the view is written as a call to the arbiter, and that is all this
    /// proves.
    /// </summary>
    /// <remarks>
    /// Not "the arbiter is consulted on every cancel": these four handlers return early on a
    /// disposed view, a missing host and a missing profile, and this predicate walks past all of
    /// them. It says the site is there, at the method's own level, in code rather than in a
    /// comment. What the arbiter then decides, and what the control is asked to do about it, is
    /// measured in <see cref="RdpConnectAttemptArbiterTests"/>.
    /// </remarks>
    [Theory]
    [InlineData(CancelMember, CancelCall)]
    [InlineData(BeginMember, BeginCall)]
    [InlineData(ContinueMember, ContinueCall)]
    [InlineData(ConnectedMember, ConnectedCall)]
    public void EachConnectEventIsWrittenAsAStatementHandingItToTheArbiter(
        string member,
        string call)
    {
        Assert.True(
            SiteIsTakenBy(ViewSource.Code(), member, call),
            $"'{call}' is no longer a statement of {member}: it is absent, or commented out, or "
                + "it now sits inside a condition, so the arbiter is not handed this event where "
                + "the view takes it.");
    }

    /// <summary>
    /// The controls: the reading above rejects a call that is only left behind in a comment.
    /// </summary>
    /// <remarks>
    /// This is the evasion the previous version of this file could not see, and the one a
    /// maintainer reaches for while debugging a stuck connect. Without these, every assertion
    /// above is a presence with nothing proving an absence can be observed.
    /// </remarks>
    [Theory]
    [InlineData(CancelMember, CancelCall)]
    [InlineData(BeginMember, BeginCall)]
    [InlineData(ContinueMember, ContinueCall)]
    [InlineData(ConnectedMember, ConnectedCall)]
    public void EachSiteIsNotFoundWhenItIsOnlyLeftInAComment(string member, string call)
    {
        Assert.False(
            SiteIsTakenBy(Mutate(call, "// " + call), member, call),
            $"A commented-out '{call}' satisfies this file's reading of {member}, so the reading "
                + "proves nothing about what is written as code.");
    }

    /// <summary>
    /// The controls: the reading above rejects a call moved inside a condition that is false on
    /// every live connect.
    /// </summary>
    [Theory]
    [InlineData(CancelMember, CancelCall)]
    [InlineData(BeginMember, BeginCall)]
    public void EachPlainSiteIsNotFoundWhenItIsMovedInsideACondition(string member, string call)
    {
        Assert.False(
            SiteIsTakenBy(Mutate(call, "if (_disposed) " + call), member, call),
            $"A '{call}' trailing a braceless condition satisfies this file's reading of "
                + $"{member}.");
    }

    /// <summary>
    /// The scheduled retry resumes the attempt it carries instead of re-entering the user's
    /// connect.
    /// </summary>
    /// <remarks>
    /// <para>This is the junction the whole remedy hangs on, and it is one statement wide. Every
    /// other test on this branch stays green when <c>ContinueConnectAttempt(attempt)</c> is
    /// swapped back for <c>BeginConnect</c> in the retry's dispatch: the arbiter still refuses the
    /// retries it is handed, and nothing hands it any. That swap is the exact shape the code had
    /// before the fix, and the shape a refactor that drops the now-unused <c>attempt</c> parameter
    /// reaches on its own.</para>
    /// <para>Like the tests above it, this is a reading of source text: it cannot say the retry
    /// runs. It is the difference between a regression a rebuild catches and one that ships
    /// green.</para>
    /// </remarks>
    [Fact]
    public void TheSurfaceRetryResumesItsOwnAttemptRatherThanReopeningOne()
    {
        Assert.True(
            RetryResumesItsOwnAttempt(ViewSource.Code()),
            "The surface retry no longer dispatches ContinueConnectAttempt(attempt). If it went "
                + "back through BeginConnect, a Cancel pressed inside the retry window is cleared "
                + "by the retry and the session the user stopped comes up live.");

        // The same separation one step further in: resuming an attempt is not asking for a new
        // one, so the retry's own handler must not open one either.
        Assert.DoesNotContain(
            "UserRequestedConnect",
            ViewSource.HandlerLogic(ContinueMember),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: the reading above rejects the dispatch the defect actually had.
    /// </summary>
    /// <remarks>
    /// Without it the assertion above is an absence with nothing proving the absence can be
    /// observed. The mutant is built from the view's real source, and the replacement count is
    /// asserted inside <see cref="Mutate"/> so a mutant that failed to land cannot be read as a
    /// rejection of unmutated code.
    /// </remarks>
    [Fact]
    public void TheRetryReadingRejectsTheDispatchThatCausedTheDefect()
    {
        Assert.False(
            RetryResumesItsOwnAttempt(Mutate(ResumeDispatch, ReopenDispatch)),
            "A retry dispatching BeginConnect satisfies this file's reading of the view, so the "
                + "reading says nothing about which entry point the retry uses.");
    }

    /// <summary>
    /// The control: the reading above rejects the dispatch kept exactly where it stands and
    /// folded behind a term that is false on every live retry.
    /// </summary>
    /// <remarks>
    /// This is the mutant a census found alive against the whole branch. Without this control the
    /// assertion above is a presence with nothing proving that a dispatch which never fires can
    /// be observed.
    /// </remarks>
    [Fact]
    public void TheRetryReadingRejectsADispatchFoldedBehindAnotherTerm()
    {
        string statement = ResumeStatement(ViewSource.Code());

        Assert.False(
            RetryResumesItsOwnAttempt(Mutate(statement, "if (_disposed) " + statement)),
            "A retry dispatch trailing a braceless condition satisfies this file's reading of "
                + "the view, so the reading cannot tell a retry that resumes the attempt from "
                + "one that is written to and never runs.");
    }

    /// <summary>Whether a call stands as a step of a member, in any version of the view.</summary>
    private static bool SiteIsTakenBy(string source, string member, string call) =>
        ViewSource.IsStatementOfTheMethodBody(
            ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), member),
            call);

    /// <summary>Whether the retry's dispatch resumes its attempt, in any version of the view.</summary>
    private static bool RetryResumesItsOwnAttempt(string source)
    {
        string retry = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(source), RetryMember);

        return ViewSource.IsStatementOfTheMethodBody(retry, ResumeStatement(retry))
            && !retry.Contains(ReopenDispatch, StringComparison.Ordinal);
    }

    /// <summary>The retry's dispatch as it is written, across its three lines.</summary>
    /// <remarks>
    /// A bare <c>Contains</c> over the retry's text stood here, and it is satisfied by the same
    /// dispatch folded behind <c>if (_disposed)</c> - there is an <c>if (_disposed) return;</c>
    /// eleven lines above - so the text stays exactly where it was while a surface that is not
    /// laid out yet never connects. Carrying the whole statement, and requiring it to stand at
    /// the method's own brace depth, rejects that fold. The newline is taken from the source
    /// being read rather than from the running machine, so a checkout with other line endings
    /// gives a real verdict instead of a false red.
    /// </remarks>
    private static string ResumeStatement(string source) => string.Join(
        source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
        "_ = Dispatcher.BeginInvoke(",
        "            DispatcherPriority.Render,",
        "            " + ResumeDispatch + ");");

    /// <summary>The view's real source with one single-occurrence statement replaced.</summary>
    /// <remarks>
    /// The count is asserted because a replacement that matched nothing would leave the source
    /// intact, and a mutant that never landed reports the unmutated code as rejected.
    /// </remarks>
    private static string Mutate(string original, string replacement)
    {
        string source = ViewSource.Code();
        int occurrences = Regex.Matches(source, Regex.Escape(original)).Count;
        Assert.True(
            occurrences == 1,
            $"Expected exactly one '{original}' in the view, found {occurrences}. A mutant built "
                + "from this would not measure what its test claims.");

        string mutated = source.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(source, mutated);
        return mutated;
    }
}
