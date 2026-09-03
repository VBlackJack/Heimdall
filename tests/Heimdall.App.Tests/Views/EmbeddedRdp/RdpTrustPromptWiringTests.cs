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

using System.Text.RegularExpressions;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Reads the view's own code to check that the pieces of the in-pane certificate question are
/// still attached to it, as steps of the methods that own them.
/// </summary>
/// <remarks>
/// <para><see cref="RdpTrustPromptSessionTests"/>, <see cref="RdpCertificateVerificationRequestBuilderTests"/>
/// and <c>PaneRdpCertificateTrustPromptTests</c> pin what each piece decides. Nothing in any of
/// them fails if the view stops calling them: that is the shape this repository has already
/// been bitten by, a protocol delivered complete and left attached to no host, with green
/// suites either side of a junction neither of them crosses.</para>
/// <para><b>What this measures, exactly.</b> The junction lives in a WPF code-behind whose
/// certificate path needs a live <c>Application</c>, an ActiveX host and a service provider
/// before it reaches any of this, so it is read from the view's own code rather than run. The
/// text is blanked of comments and literals first, and each call is required to stand as a
/// statement of the method body itself rather than merely to appear in it - a call left behind
/// in a comment, and a call moved inside <c>if (_disposed)</c>, both keep a substring search
/// green while live panes never register, never ask, and never refuse on teardown. The positive
/// controls below mutate the real source in memory and require this file's own predicate to
/// reject each mutant.</para>
/// <para><b>What it cannot see.</b> It does not establish that any of this RUNS. The predicate
/// walks straight past the conditional early returns above these sites, and past a caller that
/// never runs the method at all. It says the call stands as a step of the method; it does not
/// say the step is reached.</para>
/// </remarks>
public sealed class RdpTrustPromptWiringTests
{
    private const string Constructor = "public EmbeddedRdpView()";
    private const string InitializeSession = "public void InitializeSession(";
    private const string DisposeMember = "private void Dispose(DisconnectReason reason)";
    private const string RegisterMember = "private void RegisterTrustPromptSurface()";
    private const string VerifyCertificate =
        "private async Task<RdpCertificateCheckResult> VerifyServerCertificateAsync()";
    private const string StoppedMember =
        "private void HandleCertificateStopped(RdpVerificationOutcome? outcome)";
    private const string AnnouncedMember = "private string? AnnouncedTabTitle()";
    private const string ShowMember =
        "private void ShowCertificatePrompt(RdpCertificatePromptDialogViewModel question)";
    private const string HideMember = "private void HideCertificatePrompt()";
    private const string RestoreMember = "private void RestoreRdpSurfaceAfterTrustPrompt()";
    private const string KeyMember =
        "private void OnCertificatePromptPreviewKeyDown(object sender, KeyEventArgs e)";

    private const string RegisterCall = "RegisterTrustPromptSurface();";

    // The session built with this pane's own way of reaching the thread its question is drawn
    // on, carried whole. Drop the argument and the type name stays exactly where a bare Contains
    // would have anchored, while every withdrawal settles on the thread that raised it - which
    // is what put a live, enabled question in front of a person whose press then decided
    // nothing.
    private const string DisplayThreadStatement =
        "_trustPrompt = new RdpTrustPromptSession(PostTrustPromptWithdrawal);";

    // And what that hand-over actually does. Carried whole because the priority is the half that
    // decides whether a click already queued is dispatched before the withdrawal or lost to it:
    // Background sits below Input, Normal above it, and the method name is unchanged either way.
    private const string PostWithdrawalStatement =
        "DispatcherOperation operation = Dispatcher.BeginInvoke(DispatcherPriority.Background,"
        + " withdrawal);";

    // And the operation being watched, which the post above cannot imply. A dispatcher that is
    // shutting down aborts what is still queued WITHOUT throwing, so the catch around this post
    // - which only sees a dispatcher already shut down at the moment of posting - never runs and
    // the withdrawal silently never happens. The connection is then left waiting on a completion
    // nobody will settle, through an exit whose later cleanup runs only if its handler reaches
    // the end.
    private const string AbortedStatement = "operation.Aborted += (_, _) =>";

    private const string PostWithdrawalMember =
        "private void PostTrustPromptWithdrawal(Action withdrawal)";

    // The registration itself, carried whole. A registration under some other token routes
    // every question of this pane to nobody, and the presenter refuses rather than asking:
    // sessions stop connecting with no visible cause.
    private const string RegisterStatement =
        "_trustPromptRegistration = registry?.Register(_trustPromptScopeId, this);";

    // The request, carried whole rather than by the builder's bare name. Drop the scope
    // argument and the name stays exactly where it was while every question is refused; pass
    // another pane's scope and the question is asked at a machine the user is not connecting.
    //
    // Which profile the approval belongs to is no longer an argument here at all: it is settled
    // inside the builder from the codec's record of the mint, and measured behaviourally in
    // RdpTrustIdentityCollisionTests. A predicate used to be passed in from an inventory read,
    // and the inventory turned out to be unable to answer the question - a profile deleted while
    // its own connection was still being established is missing from it exactly as a minted
    // identifier is.
    private const string RequestStatement =
        "RdpCertificateVerificationRequest request = RdpCertificateVerificationRequestBuilder"
        + ".Build(server, target.Value, _trustPromptScopeId);";

    private const string UnregisterStatement = "_trustPromptRegistration?.Dispose();";
    private const string CloseStatement = "_trustPrompt.Close();";
    private const string AnnounceStatement = "_ = RdpLiveRegion.Announce(CertificatePromptMessageText);";
    private const string CollapseHostStatement =
        "FormsHost.Visibility = System.Windows.Visibility.Collapsed;";
    private const string RestoreHostStatement =
        "FormsHost.Visibility = System.Windows.Visibility.Visible;";
    private const string RestoreCall = "RestoreRdpSurfaceAfterTrustPrompt();";
    private const string DismissalStatement = "question.RefuseFromDismissal();";

    // The sentence chosen from the outcome. Carried whole rather than by the mapper's name:
    // pass a constant instead of the outcome and the name stays exactly where it was while
    // every stopped connection reports a refusal again.
    private const string StoppedStatusStatement =
        "SetStatusText(L(RdpCertificateStoppedStatus.StatusKey(outcome)));";

    // The name the owner line is given, carried whole. DisplayTitle is identical by construction
    // for two sessions of one profile, so a pane that reads it directly makes the line say the
    // same thing twice in exactly the case it exists for - and the field read looks entirely
    // reasonable at the site.
    private const string AnnouncedStatement =
        "return RdpTrustPromptOwner.AnnouncedName(tab.AccessibleName, tab.DisplayTitle);";

    // The extraction itself, so nothing below can pass by finding an empty body. Existence is
    // established by the extraction rather than by a search for the signature: HandlerBody
    // fails on a member it cannot find, and a bare Contains on the signature would be an
    // anchor this repository's guard-over-the-guards rejects for good reason.
    [Fact]
    public void EveryMemberThisFileMeasuresStillExists()
    {
        foreach (string member in new[]
        {
            Constructor, InitializeSession, DisposeMember, RegisterMember, VerifyCertificate,
            ShowMember, HideMember, RestoreMember, KeyMember, StoppedMember, AnnouncedMember,
            PostWithdrawalMember,
        })
        {
            Assert.NotEqual(string.Empty, ViewSource.HandlerBody(member).Trim());

            // Blanking must remove comments and literals, not code: a member that came back
            // empty from it would satisfy every absence assertion for the wrong reason.
            Assert.NotEqual(string.Empty, ViewSource.HandlerLogic(member).Trim());
        }
    }

    [Fact]
    public void InitialisingASessionRegistersThePaneAsItsOwnTrustSurface()
    {
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ViewSource.HandlerLogic(InitializeSession), RegisterCall),
            "A pane that does not register itself has no surface for its certificate question, "
                + "so every such question is refused and the session stops connecting with "
                + "nothing on screen to explain it.");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ViewSource.HandlerLogic(RegisterMember), RegisterStatement),
            "The pane no longer registers under its own scope token as a step of its own body, "
                + "so the token the verification request carries addresses nothing.");
    }

    [Fact]
    public void TheVerificationRequestCarriesThisPanesScope()
    {
        // Carried whole rather than by the builder's name, because the name stays where it is
        // when the scope argument is dropped, and a request with no scope is refused.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ViewSource.HandlerLogic(VerifyCertificate), RequestStatement),
            "The certificate check no longer builds its request with this pane's scope as a "
                + "step of its own body: the statement is absent, or it now sits inside a "
                + "condition, or its arguments changed. Either way the question is no longer "
                + "routed to the pane that asked it.");
    }

    // Positive control 10. The pane's own scope replaced by another pane's, which leaves the
    // builder's name, the argument count and every other token standing exactly where a laxer
    // reading would have anchored - and routes this pane's question to a surface the user is not
    // looking at.
    [Fact]
    public void TheRequestIsNotFoundWhenItIsBuiltWithAnotherPanesScope()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    Mutate(
                        RequestStatement,
                        "RdpCertificateVerificationRequest request = "
                        + "RdpCertificateVerificationRequestBuilder.Build(server, target.Value, "
                        + "_otherPaneScopeId);"),
                    VerifyCertificate),
                RequestStatement),
            "A request built with a scope that is not this pane's satisfies this file's reading "
                + "of the view, so the reading cannot tell a question asked where the user is "
                + "looking from one asked somewhere else.");

    [Fact]
    public void TheStoppedConnectionSaysWhichOfTheTwoWaysItStopped()
    {
        // The false claim this pins out of existence. Every way of failing to reach a person
        // came back as a refusal and the pane wrote "you did not approve the certificate this
        // server presented" - about a question that was never put to anyone.
        //
        // Only the choice is read here. That the outcome REACHES this method is the gate's own
        // property and is measured behaviourally in RdpCertificateGateTests: it is why
        // CheckConnectionAsync returns the outcome rather than the view keeping a copy from
        // inside the check's lambda, where nothing at this depth could read it.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ViewSource.HandlerLogic(StoppedMember), StoppedStatusStatement),
            "The stopped connection no longer chooses its sentence from the outcome as a step "
                + "of its own body, so a pane that was never able to ask tells its user they "
                + "refused a certificate they were never shown.");
    }

    // Positive control 6. The mapper called with a constant instead of the observed outcome,
    // with its name standing exactly where a bare Contains would have anchored.
    [Fact]
    public void TheStoppedStatusIsNotFoundWhenTheOutcomeIsReplacedByAConstant()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    Mutate(
                        StoppedStatusStatement,
                        "SetStatusText(L(RdpCertificateStoppedStatus.StatusKey("
                        + "RdpVerificationOutcome.RefusedByUser)));"),
                    StoppedMember),
                StoppedStatusStatement),
            "A stopped connection that always reports a refusal satisfies this file's reading "
                + "of the view, so the reading cannot tell an answer a person gave from a "
                + "question that reached nobody.");

    [Fact]
    public void TheOwnerLineIsGivenTheAnnouncedNameRatherThanTheDisplayedOne()
        => Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ViewSource.HandlerLogic(AnnouncedMember), AnnouncedStatement),
            "The pane names its tab by what it displays. DisplayTitle is identical by "
                + "construction for two sessions of one profile, so the owner line then reads "
                + "the same twice in exactly the case it exists for.");

    // Positive control 7. The field read put back, which is the shape that shipped and which
    // looks entirely reasonable at the site.
    [Fact]
    public void TheAnnouncedNameIsNotFoundWhenThePaneReadsTheDisplayedTitle()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    Mutate(AnnouncedStatement, "return tab.DisplayTitle;"),
                    AnnouncedMember),
                AnnouncedStatement),
            "A pane reading the displayed title satisfies this file's reading of the view.");

    [Fact]
    public void ThePaneHandsTheSessionTheThreadItsQuestionIsDrawnOn()
    {
        // Without this argument the session settles a withdrawal wherever the cancellation was
        // raised - a pool thread, running another pane's continuation - while this pane's
        // overlay comes down a dispatcher hop later. In between, three enabled answer buttons
        // stand in front of a question that has already been settled: someone pressed "Do not
        // connect" there, the press was discarded, and the pane adopted the approval given in
        // the other pane and opened the session.
        //
        // RdpTrustPromptSessionTests pins what the session then does with the press. Nothing in
        // it fails if the pane stops handing over its dispatcher, which is what this reads.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ViewSource.HandlerLogic(Constructor), DisplayThreadStatement),
            "The pane no longer builds its trust-prompt session with its own dispatcher as a "
                + "step of its constructor, so a withdrawal settles off the UI thread and a "
                + "person can press an answer on a question that has stopped answering.");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                TryBlock(ViewSource.HandlerLogic(PostWithdrawalMember)),
                PostWithdrawalStatement),
            "The hand-over no longer posts the withdrawal at Background as a step of its own "
                + "try. Above Input, the withdrawal overtakes a click already queued and the "
                + "press is lost again; not posting at all puts the settlement back on the "
                + "cancelling thread.");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                TryBlock(ViewSource.HandlerLogic(PostWithdrawalMember)),
                AbortedStatement),
            "The hand-over no longer watches the operation it posted, so a withdrawal the "
                + "dispatcher drops during shutdown is never applied and never reported.");
    }

    // Positive control 8. The wiring commented out, which is the mutant a wiring test is owed:
    // the statement stays in the file, reading exactly as it should, while the session is built
    // by the parameterless constructor and every withdrawal settles wherever it was raised.
    [Fact]
    public void TheDisplayThreadIsNotFoundWhenTheWiringIsOnlyLeftInAComment()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    Mutate(
                        DisplayThreadStatement,
                        "// " + DisplayThreadStatement),
                    Constructor),
                DisplayThreadStatement),
            "A commented-out hand-over satisfies this file's reading of the view, so the "
                + "reading proves nothing about where a withdrawal is applied.");

    // Positive control 9. The priority raised above Input, with the post still written and the
    // member name untouched - the mutant a reading anchored on "BeginInvoke" cannot see.
    [Fact]
    public void TheWithdrawalPostIsNotFoundWhenItIsRaisedAboveInput()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    Mutate(
                        PostWithdrawalStatement,
                        "DispatcherOperation operation = Dispatcher.BeginInvoke("
                        + "DispatcherPriority.Normal, withdrawal);"),
                    PostWithdrawalMember,
                    TryBlock),
                PostWithdrawalStatement),
            "A withdrawal posted above Input satisfies this file's reading of the view, so the "
                + "reading cannot tell a press that is honoured from one the withdrawal "
                + "overtakes.");

    // Positive control 9b. The operation discarded again, which is the shape that shipped: the
    // post reads identically, the priority is right, and an abort during shutdown is silent.
    [Fact]
    public void TheAbortWatchIsNotFoundWhenTheOperationIsDiscarded()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    Mutate(
                        AbortedStatement,
                        "// " + AbortedStatement),
                    PostWithdrawalMember,
                    TryBlock),
                AbortedStatement),
            "A hand-over that drops the operation on the floor satisfies this file's reading, "
                + "so the reading cannot tell a withdrawal that always happens from one the "
                + "dispatcher may discard.");

    [Fact]
    public void TearingThePaneDownUnregistersItAndSettlesWhateverItWasAsking()
    {
        string logic = ViewSource.HandlerLogic(DisposeMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, UnregisterStatement),
            "A torn-down pane stays registered, so a later question is routed to a surface "
                + "whose elements are gone.");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, CloseStatement),
            "Closing the pane no longer settles the question it was holding, as a step of its "
                + "own body. The connection then waits on an answer nobody can give, which is a "
                + "hang - and it is also the one settlement that does not go through the display "
                + "thread, so it is what stops a posted withdrawal outliving the dispatcher.");

        int unregister = logic.IndexOf(UnregisterStatement, StringComparison.Ordinal);
        int close = logic.IndexOf(CloseStatement, StringComparison.Ordinal);
        Assert.True(
            unregister < close,
            "The pane must stop being reachable before it refuses what it was holding, so no "
                + "new question can be routed into a surface that is already closing.");
    }

    [Fact]
    public void ShowingTheQuestionAnnouncesItAndHidesTheNativeSurfaceBehindIt()
    {
        string logic = ViewSource.HandlerLogic(ShowMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, CollapseHostStatement),
            "WindowsFormsHost is a child HWND and paints over WPF whatever the z-order, so a "
                + "question shown without collapsing it is a question nobody can see - and the "
                + "connection blocks on an answer to something invisible.");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, AnnounceStatement),
            "LiveSetting on its own publishes a property that nothing reads: a screen reader is "
                + "told a region changed by the event and by nothing else. Without it the "
                + "question is silent, and a question the user cannot hear is one they answer "
                + "blind.");
    }

    [Fact]
    public void HidingTheQuestionGivesTheNativeSurfaceBack()
    {
        // The other half of the airspace pairing. Collapse without restore leaves every
        // approved session with a blank pane, which looks exactly like a failed connection.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ViewSource.HandlerLogic(HideMember), RestoreCall),
            "The hide path no longer gives the RDP surface back as a step of its own body, so a "
                + "session whose certificate was just approved comes up blank.");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ViewSource.HandlerLogic(RestoreMember), RestoreHostStatement),
            "The restore itself no longer makes the surface visible as a step of its own body, "
                + "so the call above restores nothing.");
    }

    [Fact]
    public void TheKeyHandlerLeavesEnterToTheAnswerThatHasTheFocus()
    {
        // Enter is answered by ButtonBase itself, through KeyboardNavigation.AcceptsReturn on
        // each answer - measured on a real Button in RdpCertificatePromptSurfaceTests, together
        // with the fact that makes it necessary: the handler used to raise
        // ButtonBase.ClickEvent on the focused button, and OnClick raises that event AND THEN
        // executes the command source, so the raise announced a click and ran no command. Enter
        // on "Do not connect" recorded nothing, and the pane adopted the approval given in
        // another pane and connected.
        //
        // Any test on Enter here takes the keystroke off the button before it can see it,
        // whatever it then does with the keystroke, so what this guard forbids is the test.
        //
        // An assertion of absence, which inverts the risk the presence readings above carry:
        // folding a statement away cannot break it, and the only way to is to write back the
        // text it forbids.
        Assert.DoesNotContain("Key.Enter", ViewSource.HandlerLogic(KeyMember));
    }

    [Fact]
    public void EscapeRefusesTheQuestionRatherThanDismissingIt()
    {
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                EscapeBranch(ViewSource.HandlerLogic(KeyMember)), DismissalStatement),
            "Escape no longer refuses as a step of its own branch. A dismissal that answers "
                + "nothing leaves the connection waiting for good, and one that answers "
                + "anything else grants trust from a keystroke.");
    }

    // Positive control 1. The evasion a substring search cannot see: the call is still written
    // in the file, and no longer runs.
    [Fact]
    public void TheRegistrationIsNotFoundWhenItIsOnlyLeftInAComment()
        => Assert.False(
            RegistrationIsTakenBy(Mutate(RegisterCall, "// " + RegisterCall)),
            "A commented-out registration satisfies this file's reading of the view, so the "
                + "reading proves nothing about what runs.");

    // Positive control 2. The call kept, moved behind a condition that is false on every live
    // initialisation.
    [Fact]
    public void TheRegistrationIsNotFoundWhenItIsGuardedWithoutBraces()
        => Assert.False(
            RegistrationIsTakenBy(Mutate(RegisterCall, "if (_disposed) " + RegisterCall)),
            "A registration trailing a braceless condition satisfies this file's reading of the "
                + "view.");

    // Positive control 3. The scope argument dropped, with the builder's name - which an
    // ordering or a bare Contains would have anchored on - standing exactly where it was.
    [Fact]
    public void TheRequestIsNotFoundWhenItLosesTheScopeArgument()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    Mutate(
                        RequestStatement,
                        "RdpCertificateVerificationRequest request = "
                        + "RdpCertificateVerificationRequestBuilder.Build(server, target.Value, "
                        + "string.Empty);"),
                    VerifyCertificate),
                RequestStatement),
            "A request built without this pane's scope satisfies this file's reading of the "
                + "view, so the reading cannot tell a question that reaches the pane from one "
                + "that reaches nobody.");

    // Positive control 4. The teardown settlement deleted, which is the one that turns a closed
    // pane into a connection waiting for an answer forever.
    [Fact]
    public void TheTeardownSettlementIsNotFoundWhenItIsOnlyLeftInAComment()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(Mutate(CloseStatement, "// " + CloseStatement), DisposeMember),
                CloseStatement),
            "A commented-out teardown settlement satisfies this file's reading of the view.");

    // Positive control 5. The announcement folded behind a condition, with the call still
    // written where it was.
    [Fact]
    public void TheAnnouncementIsNotFoundWhenItIsFoldedBehindACondition()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    Mutate(AnnounceStatement, "if (_disposed) " + AnnounceStatement),
                    ShowMember),
                AnnounceStatement),
            "An announcement trailing a braceless condition satisfies this file's reading of "
                + "the view, so the question could go silent with this file green.");

    /// <summary>One member of any version of the view, blanked of comments and literals.</summary>
    private static string LogicOf(string source, string member) =>
        ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), member);

    /// <summary>The same, narrowed to one block of that member.</summary>
    private static string LogicOf(string source, string member, Func<string, string> block) =>
        block(LogicOf(source, member));

    /// <summary>
    /// The block guarded by the member's own <c>try</c>, brace matched, as a body of its own.
    /// </summary>
    /// <remarks>
    /// Same reason as <see cref="EscapeBranch"/>: the reading works at a body's own brace depth,
    /// and a statement written inside a try lives one level deeper. Handing it the block asks
    /// the same question there - is this written as a step of what encloses it - rather than
    /// whether the call appears somewhere in the method.
    /// </remarks>
    private static string TryBlock(string logic)
    {
        Match test = Regex.Match(logic, @"(?m)^\s*try");
        Assert.True(test.Success, "The member carries no try block.");

        return BracedBlockAt(logic, test.Index);
    }

    private static bool RegistrationIsTakenBy(string source) =>
        ViewSource.IsStatementOfTheMethodBody(LogicOf(source, InitializeSession), RegisterCall);

    /// <summary>
    /// The block guarded by the Escape test, brace matched, as a body of its own.
    /// </summary>
    /// <remarks>
    /// <see cref="ViewSource.IsStatementOfTheMethodBody"/> reads a body at its own brace depth,
    /// and the refusal lives one level deeper than that. Handing it the block makes the same
    /// question askable there - is this written as a step of what encloses it - rather than
    /// asking whether the call appears somewhere after the word Escape.
    /// </remarks>
    private static string EscapeBranch(string logic)
    {
        Match test = Regex.Match(logic, @"if\s*\(\s*e\.Key\s*==\s*Key\.Escape\s*\)");
        Assert.True(
            test.Success,
            "The certificate question no longer tests for Escape at all, so the keystroke that "
                + "used to close its window now does nothing.");

        return BracedBlockAt(logic, test.Index);
    }

    /// <summary>The brace-matched block that opens at or after <paramref name="from"/>.</summary>
    private static string BracedBlockAt(string logic, int from)
    {
        int open = logic.IndexOf('{', from);
        Assert.True(open >= 0, "The construct carries no block.");

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

        throw new InvalidOperationException("The block read from the view is unbalanced.");
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
