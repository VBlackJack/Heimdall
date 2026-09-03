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

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Reads the view's own code to check that its certificate path still calls the suspension
/// arbiter, as a step of the method rather than as text, and in the right order.
/// </summary>
/// <remarks>
/// <para><see cref="RdpConnectWatchdogSuspensionTests"/> pins what the arbiter decides. Nothing
/// there fails if the view stops calling it: that is the shape this repository has already been
/// bitten by, a guard delivered complete and left attached to no host, with green suites either
/// side of a junction neither of them crosses.</para>
/// <para><b>What this measures, exactly.</b> The junction lives in a WPF code-behind whose
/// certificate path needs a live <c>Application</c>, an ActiveX host and a service provider
/// before it reaches the arbiter, so it is read from the view's own code rather than run. That
/// makes the reading's honesty the whole question, and an earlier version of this file failed it:
/// it searched the method's raw text for substrings, so a call left behind in a comment, or moved
/// inside <c>if (_disposed)</c>, kept every assertion here green while live views never
/// suspended. The text is now blanked of comments and literals before anything is read from it,
/// and each call is required to stand as a statement of the method body itself rather than merely
/// to appear in it. <see cref="TheSuspensionIsNotFoundWhenItIsOnlyLeftInAComment"/> and its two
/// siblings are the positive controls: they mutate the real source in memory and require this
/// file's own predicate to reject each mutant.</para>
/// <para><b>What it cannot see, stated so no reader takes more from it than it gives.</b> It does
/// not establish that the suspension RUNS. The predicate rejects a site that is commented out,
/// wrapped in a condition, or written below an unconditional <c>return</c>; it walks straight past
/// the four conditional early returns that stand above the suspension in
/// <c>VerifyServerCertificateAsync</c>, because deciding whether those are taken would mean
/// evaluating their conditions. Invert one of them and every assertion in this file stays green
/// while no live connect ever suspends. Nor can it see a caller that never runs the method at all.
/// Only extracting the sequence out of the code-behind, into a type a test can drive, closes
/// either gap, and the arbiter and its policy type already show the shape that takes.</para>
/// </remarks>
public sealed class RdpConnectWatchdogSuspensionWiringTests
{
    private const string TransitionPhase = "private void TransitionPhase(RdpConnectionPhase newPhase)";
    private const string StartVerifiedConnect = "private async Task StartVerifiedConnectAsync()";
    private const string VerifyCertificate =
        "private async Task<RdpCertificateCheckResult> VerifyServerCertificateAsync()";
    private const string SuspendMember = "private void SuspendConnectWatchdog()";
    private const string CancelMember = "private void CancelConnectWatchdog()";

    private const string SuspendCall = "_connectWatchdogArbiter.CertificateCheckStarted()";
    private const string SuspendStatement = SuspendCall + ";";
    private const string PhaseCall = "_connectWatchdogArbiter.PhaseChanged(newPhase)";

    // The abandonment door of the certificate path, carried whole. What has to be written there
    // is the refusal being acted on, not a call whose answer is dropped and not a condition with
    // an extra term folded into it.
    private const string SettledCall =
        "if (_connectAttempts.CertificateCheckSettled(_disposed) "
        + "== RdpVerifiedConnectAdmission.Refuse)";

    // The other three steps whose order is asserted below, each carried whole for the same
    // reason. An IndexOf on a bare name - "RdpCertificateGate.VerificationRequired",
    // "BeginConnect" - and a dot-all regex reaching from "finally" to a call both see text at an
    // offset rather than a step of a body: fold the statement behind a term that is false by
    // construction and the name stays exactly where it was, the ordering still holds, the regex
    // still matches, and this file stays green while the step is gone.
    private const string OwedTest =
        "if (!RdpCertificateGate.VerificationRequired(auth.AuthenticationLevel))";
    private const string ResumeStatement =
        "_connectWatchdogArbiter.CertificateCheckCompleted(_connectionPhase, _disposed);";
    private const string BeginConnectDispatch =
        "_ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(BeginConnect));";

    // The question itself, carried whole. The ordering below used to end on the bare name
    // "RdpCertificateGate.CheckConnectionAsync", and a bare name is text at an offset: fold the
    // question behind a term that is false by construction and the name stays exactly where it
    // was, the ordering still holds, and this file stays green while no question is ever asked.
    //
    // CheckConnectionAsync rather than DecideConnectionAsync: the check now hands back what it
    // concluded as well as whether to connect, because two outcomes stop the connection - an
    // answer a person gave, and a question that reached nobody - and the pane has a different
    // sentence for each.
    private const string CertificateQuestion =
        "return await RdpCertificateGate.CheckConnectionAsync(";

    // The credential-wait reset of the cancel path, carried whole for the same reason. A bare
    // Assert.Contains on the assignment sees it wherever it stands, so a reset folded behind a
    // term that is false by construction leaves a cancelled attempt holding a promotion that
    // belongs to the attempt it ended.
    private const string CredentialWaitReset = "_watchdogCredentialWaitActive = false;";

    // The extraction itself, so nothing below can pass by finding an empty body.
    [Fact]
    public void EveryMemberThisFileMeasuresStillExists()
    {
        string source = ViewSource.Code();

        foreach (string member in new[]
        {
            TransitionPhase, StartVerifiedConnect, VerifyCertificate, SuspendMember, CancelMember,
        })
        {
            Assert.Contains(member, source, StringComparison.Ordinal);
            Assert.NotEqual(string.Empty, ViewSource.HandlerBody(member).Trim());

            // Blanking must remove comments and literals, not code: a member that comes back
            // empty from it would satisfy every absence assertion below for the wrong reason.
            Assert.NotEqual(string.Empty, ViewSource.HandlerLogic(member).Trim());
        }
    }

    // Blanking is what every reading below stands on. An interpolation hole holding a string -
    // the view writes several - ends the literal early for a naive scan and leaves the hole's
    // closing brace behind as code, which would silently shift every brace depth after it.
    [Fact]
    public void BlankingTheViewLeavesItsBracesBalancedAndItsMembersIntact()
    {
        string source = ViewSource.Code();
        string logic = ViewSource.WithoutCommentsAndLiterals(source);

        Assert.Equal(source.Length, logic.Length);
        Assert.Equal(
            logic.Count(character => character == '{'),
            logic.Count(character => character == '}'));

        // The precondition first, so this stops measuring loudly rather than silently if the
        // view ever loses the literal that makes the case hard.
        Assert.Contains(
            @"$""fullscreen-toggle-{(isFullscreen ? ""enter"" : ""exit"")}""",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("fullscreen-toggle-", logic, StringComparison.Ordinal);

        // Code survives it.
        Assert.Contains(SuspendStatement, logic, StringComparison.Ordinal);
    }

    // The phase transition no longer drives the timer itself: if it did, Preparing would arm a
    // connect budget over a certificate question however the arbiter had decided.
    [Fact]
    public void TransitionPhaseDelegatesTheWatchdogDecisionToTheArbiter()
    {
        string logic = ViewSource.HandlerLogic(TransitionPhase);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, PhaseCall),
            "Entering a phase no longer tells the arbiter, or tells it only on some branch, so "
                + "the watchdog decision the arbiter owns is not taken on every transition.");
        Assert.DoesNotContain("StartConnectWatchdog()", logic, StringComparison.Ordinal);
        Assert.DoesNotContain("StopConnectWatchdog()", logic, StringComparison.Ordinal);
    }

    // The suspension is written after the test of whether a check is owed at all, and before the
    // call that can ask a human. Both are orderings of the text, not proof that either is reached.
    [Fact]
    public void TheSuspensionIsWrittenAfterTheOwedTestAndBeforeTheQuestion()
    {
        string logic = ViewSource.HandlerLogic(VerifyCertificate);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, SuspendCall),
            "The certificate check no longer suspends the connect watchdog as a step of its own "
                + "body: the call is absent, or it now sits inside a condition, or an "
                + "unconditional return stands above it, which leaves the question charged to the "
                + "connect budget.");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, CertificateQuestion),
            "The verification no longer asks the gate as a step of its own body: the call is "
                + "absent, or it now sits inside a condition, or an unconditional return stands "
                + "above it. An ordering anchored on the bare name would end at the same offset "
                + "either way, so it could not tell a question that is asked from one that is "
                + "only written.");

        int suspend = logic.IndexOf(SuspendCall, StringComparison.Ordinal);
        int decide = logic.IndexOf(CertificateQuestion, StringComparison.Ordinal);
        Assert.True(
            suspend < decide,
            "The watchdog is suspended after the check has already been started, so the question "
                + "is still charged to the connect budget.");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, OwedTest),
            "The profile no longer decides whether a check is owed as a step of this body: the "
                + "test is absent, or it has grown a term, or it now sits inside another "
                + "condition. A profile that owes nothing then runs the whole certificate path "
                + "anyway, and an ordering anchored on the bare name would hold either way.");

        int required = logic.IndexOf(OwedTest, StringComparison.Ordinal);
        Assert.True(
            required < suspend,
            "A profile that owes no certificate check would suspend its watchdog anyway, which "
                + "is the regression that changes the behaviour of every other profile.");
    }

    // Resumed in a finally, before the abandonment door and before BeginConnect: a refusal, a
    // cancellation and a teardown must all leave the watchdog in a chosen state.
    [Fact]
    public void TheWatchdogIsResumedInAFinallyBeforeTheAbandonmentDoorAndBeforeBeginConnect()
    {
        string logic = ViewSource.HandlerLogic(StartVerifiedConnect);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(FinallyBlock(logic), ResumeStatement),
            "The connect watchdog is not resumed as a step of the finally block: the call is "
                + "absent, or it now sits inside a condition. A throw from the certificate check "
                + "then leaves the session in Preparing with no watchdog at all, and so does a "
                + "resume folded behind a term that is false by construction.");

        // The door itself, carried whole rather than probed by the name of a latch. An IndexOf
        // on "AbandonedByUser" stood here, and it sees text rather than a condition: fold the
        // check behind a term that is always false and the name stays exactly where it was, the
        // ordering below still holds, and the guard stays green while a cancelled connect starts.
        // A census found that mutant alive against every test on this branch.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, SettledCall),
            "The certificate path no longer hands its abandonment door to the arbiter as written: "
                + "the condition is absent, or it has grown a term, or it now sits inside another "
                + "condition. A Cancel taken during the check then falls through to BeginConnect, "
                + "which opens a fresh attempt and clears the latch the late-connect refusal "
                + "reads.");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, BeginConnectDispatch),
            "The connect is no longer dispatched as a step of this body: the statement is "
                + "absent, or it now sits inside a condition. An ordering anchored on the bare "
                + "name would end in the same place either way.");

        int completed = logic.IndexOf(ResumeStatement, StringComparison.Ordinal);
        int settled = logic.IndexOf(SettledCall, StringComparison.Ordinal);
        int beginConnect = logic.IndexOf(BeginConnectDispatch, StringComparison.Ordinal);

        Assert.True(
            completed < settled && settled < beginConnect,
            "The watchdog must be resumed before the abandonment door, and the connect must "
                + "start after both.");
    }

    // Positive control 5. The mutant a census found alive against the whole branch: the check
    // kept exactly where it stands, folded behind a term, so both the text and its ordering
    // survive while the refusal can never fire. Whether the extra term is false by construction
    // is not this predicate's business - what it rejects is a condition that is no longer the
    // one written here.
    [Fact]
    public void TheAbandonmentDoorIsNotFoundWhenItIsFoldedBehindAnotherTerm()
    {
        Assert.False(
            SettledDoorIsTakenBy(Mutate(SettledCall, "if (_disposed && " + SettledCall[4..])),
            "A door carrying an extra term satisfies this file's reading of the view, so the "
                + "reading cannot tell the refusal that fires from the one that cannot.");
    }

    // Positive control 6. The same door, deleted outright.
    [Fact]
    public void TheAbandonmentDoorIsNotFoundWhenItIsOnlyLeftInAComment()
    {
        Assert.False(
            SettledDoorIsTakenBy(Mutate(SettledCall, "// " + SettledCall)),
            "A commented-out abandonment door satisfies this file's reading of the view.");
    }

    // The two ways of stopping the timer are not interchangeable. Suspending must keep the
    // credential-wait promotion: it is a pause on the same attempt, not its end.
    [Fact]
    public void SuspendingKeepsTheCredentialWaitPromotionAndCancellingDropsIt()
    {
        // An absence, which inverts the risk: folding a statement can only keep this passing, and
        // the only way to break it is to add the promotion back to the suspension.
        Assert.DoesNotContain(
            "_watchdogCredentialWaitActive",
            ViewSource.HandlerLogic(SuspendMember),
            StringComparison.Ordinal);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ViewSource.HandlerLogic(CancelMember), CredentialWaitReset),
            "Cancelling no longer drops the credential-wait promotion as a step of its own body: "
                + "the assignment is absent, or it now sits inside a condition. A cancelled "
                + "attempt then keeps a promotion that belongs to the attempt it ended, and the "
                + "bare Contains that stood here would hold either way.");
    }

    // Positive control 1. The evasion a substring search cannot see: the call is still written
    // in the file, and no longer runs.
    [Fact]
    public void TheSuspensionIsNotFoundWhenItIsOnlyLeftInAComment()
    {
        Assert.False(
            SuspensionIsTakenBy(Mutate(SuspendStatement, "// " + SuspendStatement)),
            "A commented-out suspension satisfies this file's reading of the view, so the "
                + "reading proves nothing about what runs.");
    }

    // Positive control 2. The evasion named in review: the call kept, moved behind the check
    // that is false on every live connect, so it never runs where it is needed.
    [Fact]
    public void TheSuspensionIsNotFoundWhenItIsMovedInsideACondition()
    {
        Assert.False(
            SuspensionIsTakenBy(Mutate(
                SuspendStatement,
                "if (_disposed)"
                + Environment.NewLine + "        {"
                + Environment.NewLine + "            " + SuspendStatement
                + Environment.NewLine + "        }")),
            "A suspension taken only inside a branch satisfies this file's reading of the view.");
    }

    // Positive control 3. The same evasion without braces, which no brace-depth rule alone
    // would catch.
    [Fact]
    public void TheSuspensionIsNotFoundWhenItIsGuardedWithoutBraces()
    {
        Assert.False(
            SuspensionIsTakenBy(Mutate(SuspendStatement, "if (_disposed) " + SuspendStatement)),
            "A suspension trailing a braceless condition satisfies this file's reading of the "
                + "view.");
    }

    // Positive control 4. The one unreachability a text reading can settle: the call kept, at the
    // method's own level, under a return that ends the method before it.
    [Fact]
    public void TheSuspensionIsNotFoundWhenAnUnconditionalReturnStandsAboveIt()
    {
        Assert.False(
            SuspensionIsTakenBy(Mutate(
                SuspendStatement,
                "return RdpConnectionDecision.Proceed;"
                + Environment.NewLine + Environment.NewLine + "        " + SuspendStatement)),
            "A suspension written below a return that always fires satisfies this file's reading "
                + "of the view, so the reading cannot even reject dead code.");
    }

    // Positive control 7. The owed test folded behind a term, so the name an ordering used to be
    // anchored on stays exactly where it was while a profile owing no check can no longer return
    // early and every profile pays for a certificate path it does not need.
    [Fact]
    public void TheOwedTestIsNotFoundWhenItIsFoldedBehindAnotherTerm()
    {
        Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(Mutate(OwedTest, "if (_disposed && " + OwedTest[4..]), VerifyCertificate),
                OwedTest),
            "An owed test carrying an extra term satisfies this file's reading of the view, so "
                + "the reading cannot tell the early return that fires from the one that cannot.");
    }

    // Positive control 8. The resume folded inside its own finally, which the dot-all regex that
    // stood here matched just as happily as the real thing.
    [Fact]
    public void TheWatchdogResumeIsNotFoundWhenItIsFoldedInsideTheFinally()
    {
        Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                FinallyBlock(LogicOf(
                    Mutate(ResumeStatement, "if (!_disposed) " + ResumeStatement),
                    StartVerifiedConnect)),
                ResumeStatement),
            "A resume trailing a braceless condition inside the finally satisfies this file's "
                + "reading of the view, so the reading says nothing about the watchdog being "
                + "left in a chosen state.");
    }

    // Positive control 9. The connect dispatch folded, with the bare name the ordering used to
    // end on still standing at the same offset.
    [Fact]
    public void TheConnectDispatchIsNotFoundWhenItIsFoldedBehindACondition()
    {
        Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    Mutate(BeginConnectDispatch, "if (_disposed) " + BeginConnectDispatch),
                    StartVerifiedConnect),
                BeginConnectDispatch),
            "A connect dispatch trailing a braceless condition satisfies this file's reading of "
                + "the view, so a verified profile could stop connecting with this file green.");
    }

    // Positive control 10. The certificate question folded, with the bare name the ordering used
    // to end on still standing at the same offset.
    [Fact]
    public void TheCertificateQuestionIsNotFoundWhenItIsFoldedBehindACondition()
    {
        Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    Mutate(CertificateQuestion, "if (_disposed) " + CertificateQuestion),
                    VerifyCertificate),
                CertificateQuestion),
            "A question trailing a braceless condition satisfies this file's reading of the view, "
                + "so a profile that owes a certificate check could stop asking with this file "
                + "green.");
    }

    // Positive control 11. The credential-wait reset folded, with the text the bare Contains that
    // stood here matched still standing exactly where it was.
    [Fact]
    public void TheCredentialWaitResetIsNotFoundWhenItIsFoldedBehindACondition()
    {
        Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                MutatedMemberLogic(
                    CancelMember, CredentialWaitReset, "if (_disposed) " + CredentialWaitReset),
                CredentialWaitReset),
            "A credential-wait reset trailing a braceless condition satisfies this file's reading "
                + "of the cancel path, so the reading cannot tell a promotion that is dropped "
                + "from one that is only written about.");
    }

    /// <summary>One member of any version of the view, blanked of comments and literals.</summary>
    private static string LogicOf(string source, string member) =>
        ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), member);

    /// <summary>The predicate the assertions above rest on, applied to any version of the view.</summary>
    private static bool SuspensionIsTakenBy(string source) =>
        ViewSource.IsStatementOfTheMethodBody(LogicOf(source, VerifyCertificate), SuspendCall);

    /// <summary>The same, for the abandonment door of the certificate path.</summary>
    private static bool SettledDoorIsTakenBy(string source) =>
        ViewSource.IsStatementOfTheMethodBody(
            LogicOf(source, StartVerifiedConnect), SettledCall);

    /// <summary>The text of the finally block of a method, brace matched, as a body of its own.</summary>
    /// <remarks>
    /// <see cref="ViewSource.IsStatementOfTheMethodBody"/> reads a body at its own brace depth,
    /// and the resume lives one level deeper than that. Handing it the block makes the same
    /// question - is this written as a step of what encloses it - askable there, rather than
    /// asking a dot-all regex whether the call appears somewhere after the word "finally".
    /// </remarks>
    private static string FinallyBlock(string logic)
    {
        Match keyword = Regex.Match(logic, @"(?ms)^\s*finally\s*$");
        Assert.True(
            keyword.Success,
            "The certificate check no longer runs under a finally, so nothing resumes the "
                + "watchdog when it throws.");

        int open = logic.IndexOf('{', keyword.Index);
        Assert.True(open >= 0, "The finally carries no block.");

        int depth = 0;
        for (int index = open; index < logic.Length; index++)
        {
            if (logic[index] == '{')
            {
                depth++;
            }
            else if (logic[index] == '}' && --depth == 0)
            {
                return logic[open..(index + 1)];
            }
        }

        throw new InvalidDataException("The finally block of the certificate path is unbalanced.");
    }

    /// <summary>
    /// One member's blanked text with a single-occurrence fragment replaced.
    /// </summary>
    /// <remarks>
    /// <see cref="Mutate"/> requires the fragment to be unique in the whole view, and the
    /// credential-wait assignment is written in three different members. Scoping the count to the
    /// member keeps the same protection - a replacement that matched nothing would leave the text
    /// intact and report unmutated code as rejected - where a file-wide count cannot be taken.
    /// </remarks>
    private static string MutatedMemberLogic(string member, string original, string replacement)
    {
        string logic = ViewSource.HandlerLogic(member);
        int occurrences = Regex.Matches(logic, Regex.Escape(original)).Count;
        Assert.True(
            occurrences == 1,
            $"Expected exactly one '{original}' in {member}, found {occurrences}. The mutant "
                + "built from it would not measure what it claims.");

        string mutated = logic.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(logic, mutated);
        return mutated;
    }

    /// <summary>
    /// The view's real source with one single-occurrence fragment replaced.
    /// </summary>
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
            $"Expected exactly one '{original}' in the view, found {occurrences}. The mutants "
                + "built from it would not measure what they claim.");

        string mutated = source.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(source, mutated);
        return mutated;
    }
}
