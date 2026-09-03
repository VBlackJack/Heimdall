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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// The certificate question one pane is waiting on: how it is answered, and every way of not
/// answering it.
/// </summary>
/// <remarks>
/// <para>This is what replaced <c>Window.ShowDialog()</c>. The window did the waiting with its
/// own message pump and the blocking with application modality; neither survives the move into
/// the pane, so the wait is a completion source and the blocking is "this connection, and
/// nothing else". The edges that used to be the window's - the title-bar cross, a dispatcher
/// shutdown - are the ones played here.</para>
/// <para>No window and no <c>UserControl</c> is constructed: building a WPF <c>Window</c> in a
/// test seals application-level styles onto the shared dispatcher and takes unrelated tests
/// down with it.</para>
/// </remarks>
public sealed class RdpTrustPromptSessionTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AskAsync_PutsTheQuestionOnScreenAndWaits()
    {
        RdpTrustPromptSession session = new();
        int changes = 0;
        session.QuestionChanged += () => changes++;
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();

        Task<RdpTrustAnswer> pending = session.AskAsync(question, CancellationToken.None);

        Assert.Same(question, session.Question);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData(RdpTrustAnswer.TrustPermanently)]
    [InlineData(RdpTrustAnswer.TrustForSession)]
    [InlineData(RdpTrustAnswer.Refuse)]
    public async Task Answering_CompletesTheWaitAndTakesTheQuestionOffTheScreen(
        RdpTrustAnswer expected)
    {
        RdpTrustPromptSession session = new();
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();
        Task<RdpTrustAnswer> pending = session.AskAsync(question, CancellationToken.None);

        Execute(question, expected);

        Assert.Equal(expected, await pending.WaitAsync(CompletionTimeout));
        Assert.Null(session.Question);
    }

    [Fact]
    public async Task ClosingThePane_StopsTheQuestionWithoutCallingItAnAnswer()
    {
        // "Closing the pane is not approval", which is the rule the window used to carry as
        // "the title-bar cross is not an answer". The alternative is a connection opened on a
        // certificate nobody approved.
        //
        // NotAsked rather than Refuse, and that is the whole point of the value. Refuse is what
        // a person pressed, and the pane says so out loud - "you did not approve the certificate
        // this server presented". Reporting a teardown as Refuse puts that sentence in front of
        // a user who was asked nothing. Both still stop the connection.
        RdpTrustPromptSession session = new();
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();
        Task<RdpTrustAnswer> pending = session.AskAsync(question, CancellationToken.None);

        session.Close();

        RdpTrustAnswer settled = await pending.WaitAsync(CompletionTimeout);
        Assert.Equal(RdpTrustAnswer.NotAsked, settled);
        Assert.NotEqual(RdpTrustAnswer.Refuse, settled);
        Assert.Null(session.Question);

        // And nothing was recorded on the question either: a teardown is not a decision.
        Assert.Null(question.Answer);
    }

    [Fact]
    public async Task AQuestionArrivingAfterTheCloseIsTurnedAwayAndNeverShown()
    {
        RdpTrustPromptSession session = new();
        session.Close();

        Task<RdpTrustAnswer> pending =
            session.AskAsync(await QuestionAsync(), CancellationToken.None);

        Assert.Equal(RdpTrustAnswer.NotAsked, await pending.WaitAsync(CompletionTimeout));
        Assert.Null(session.Question);
    }

    [Fact]
    public async Task ClosingIsPermanent_SoATornDownPaneCannotBeMadeToAskAgain()
    {
        RdpTrustPromptSession session = new();
        session.Close();
        session.Close();

        Assert.Equal(
            RdpTrustAnswer.NotAsked,
            await session.AskAsync(await QuestionAsync(), CancellationToken.None)
                .WaitAsync(CompletionTimeout));
    }

    [Fact]
    public async Task Withdrawing_StopsTheConnectionAndAlsoTakesTheQuestionOffTheScreen()
    {
        // The mutant this exists for: a withdrawal that only completes the task. The connection
        // stops and moves on, and the question stays on screen over a pane that is no longer
        // waiting for it - a dialog with no owner, whose buttons answer nothing.
        //
        // A withdrawal is now also the ordinary way a coalesced question leaves a pane once
        // another pane has answered it, so calling it a refusal would put "you did not approve
        // the certificate" in front of a user whose sibling pane approved it.
        RdpTrustPromptSession session = new();
        using CancellationTokenSource cts = new();
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();
        Task<RdpTrustAnswer> pending = session.AskAsync(question, cts.Token);

        await cts.CancelAsync();

        Assert.Equal(RdpTrustAnswer.NotAsked, await pending.WaitAsync(CompletionTimeout));
        Assert.Null(session.Question);
        Assert.Null(question.Answer);
    }

    [Fact]
    public async Task EscapeIsStillARefusal_BecauseAPersonPressedIt()
    {
        // The other side of the same distinction, and the reason it cannot be "every exit that
        // is not a button is NotAsked". Escape reaches a question the user is looking at, so it
        // is an answer and is reported as one.
        RdpTrustPromptSession session = new();
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();
        Task<RdpTrustAnswer> pending = session.AskAsync(question, CancellationToken.None);

        question.RefuseFromDismissal();

        Assert.Equal(RdpTrustAnswer.Refuse, await pending.WaitAsync(CompletionTimeout));
        Assert.Equal(RdpTrustAnswer.Refuse, question.Answer);
        Assert.Null(session.Question);
    }

    [Fact]
    public async Task AnAlreadyCancelledRequest_NeverLeavesAQuestionOnScreen()
    {
        RdpTrustPromptSession session = new();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Task<RdpTrustAnswer> pending = session.AskAsync(await QuestionAsync(), cts.Token);

        Assert.Equal(RdpTrustAnswer.NotAsked, await pending.WaitAsync(CompletionTimeout));
        Assert.Null(session.Question);
    }

    [Fact]
    public async Task AnAnswerArrivingAfterAWithdrawal_DoesNotChangeTheDecision()
    {
        // The race that matters in the safe direction: the connection has already been told
        // "refused", so a later click must not be able to turn that into durable trust.
        RdpTrustPromptSession session = new();
        using CancellationTokenSource cts = new();
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();
        Task<RdpTrustAnswer> pending = session.AskAsync(question, cts.Token);

        await cts.CancelAsync();
        question.TrustCommand.Execute(null);

        Assert.Equal(RdpTrustAnswer.NotAsked, await pending.WaitAsync(CompletionTimeout));
    }

    [Fact]
    public async Task AWithdrawalIsNotAppliedUntilTheThreadShowingTheQuestionRunsIt()
    {
        // The premise the two tests below stand on, and the whole shape of the fix. A
        // withdrawal arrives on whichever thread another pane's answer completed on; the
        // buttons that answer THIS question are hit-tested somewhere else entirely. Until the
        // display thread runs the settlement the question is still up, and it must still be
        // answerable - the alternative is what shipped: a live overlay whose buttons decide
        // nothing.
        Queue<Action> display = new();
        RdpTrustPromptSession session = new(display.Enqueue);
        using CancellationTokenSource cts = new();
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();
        Task<RdpTrustAnswer> pending = session.AskAsync(question, cts.Token);

        await cts.CancelAsync();

        Assert.Same(question, session.Question);
        Assert.False(pending.IsCompleted);

        Pump(display);

        Assert.Equal(RdpTrustAnswer.NotAsked, await pending.WaitAsync(CompletionTimeout));
        Assert.Null(session.Question);
    }

    [Fact]
    public async Task ARefusalPressedBeforeTheWithdrawalReachesTheScreen_IsThePanesOwnAnswer()
    {
        // Someone pressed "Do not connect" and the session opened. That is what this pins out
        // of existence, and it is the same wrong attribution the whole lot exists to close,
        // arriving through a race rather than through identity.
        //
        // The ordering is the real one, forced rather than waited for: another pane answers, the
        // withdrawal is raised on that pane's continuation thread, and the person - looking at a
        // question that is still on screen, still enabled - presses Do not connect before this
        // pane's own thread has taken it down. Their refusal is the pane's answer, so the
        // coalescer never reaches the step that would hand this pane the approval given
        // elsewhere.
        Queue<Action> display = new();
        RdpTrustPromptSession session = new(display.Enqueue);
        using CancellationTokenSource cts = new();
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();
        Task<RdpTrustAnswer> pending = session.AskAsync(question, cts.Token);

        await cts.CancelAsync();
        question.RefuseCommand.Execute(null);
        Pump(display);

        Assert.Equal(RdpTrustAnswer.Refuse, await pending.WaitAsync(CompletionTimeout));
        Assert.Equal(RdpTrustAnswer.Refuse, question.Answer);
        Assert.Null(session.Question);
    }

    [Theory]
    [InlineData(RdpTrustAnswer.TrustPermanently)]
    [InlineData(RdpTrustAnswer.TrustForSession)]
    public async Task AnApprovalPressedAfterTheWithdrawalHasBegun_IsNotAGrant(
        RdpTrustAnswer pressed)
    {
        // The other direction of the same decision, and the reason it is not "the last press
        // wins". A refusal decides something still open - whether this pane connects. An
        // approval decides the certificate, which was decided elsewhere the moment the
        // withdrawal was raised, and honouring it would write the trust store from a question
        // the application had already taken back. The connection stops, and one reconnect asks
        // again.
        Queue<Action> display = new();
        RdpTrustPromptSession session = new(display.Enqueue);
        using CancellationTokenSource cts = new();
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();
        Task<RdpTrustAnswer> pending = session.AskAsync(question, cts.Token);

        await cts.CancelAsync();
        Execute(question, pressed);
        Pump(display);

        Assert.Equal(RdpTrustAnswer.NotAsked, await pending.WaitAsync(CompletionTimeout));
    }

    [Fact]
    public async Task AnAnswerPressedBeforeAnyWithdrawal_StillSettlesWithoutTheDisplayThread()
    {
        // An answer is already on the display thread, so it settles where it is pressed. Send
        // it round the queue as well and every ordinary approval would wait for a hop that a
        // pane with nothing else to do may not run promptly.
        Queue<Action> display = new();
        RdpTrustPromptSession session = new(display.Enqueue);
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();
        Task<RdpTrustAnswer> pending = session.AskAsync(question, CancellationToken.None);

        question.TrustCommand.Execute(null);

        Assert.Equal(
            RdpTrustAnswer.TrustPermanently,
            await pending.WaitAsync(CompletionTimeout));
        Assert.Empty(display);
    }

    [Fact]
    public async Task ClosingThePane_SettlesEvenWhenTheDisplayThreadNeverRunsAgain()
    {
        // The liveness the hop costs, and where it is bought back. A dispatcher that has
        // stopped leaves a posted withdrawal unrun; the pane's own teardown closes the session,
        // and that settles it on the spot rather than through the queue.
        Queue<Action> display = new();
        RdpTrustPromptSession session = new(display.Enqueue);
        using CancellationTokenSource cts = new();
        Task<RdpTrustAnswer> pending =
            session.AskAsync(await QuestionAsync(), cts.Token);

        await cts.CancelAsync();
        session.Close();

        Assert.Equal(RdpTrustAnswer.NotAsked, await pending.WaitAsync(CompletionTimeout));
        Assert.Null(session.Question);
    }

    /// <summary>Runs what the thread showing the question has been handed, in order.</summary>
    private static void Pump(Queue<Action> display)
    {
        while (display.Count > 0)
        {
            display.Dequeue()();
        }
    }

    [Fact]
    public async Task ASecondQuestionWhileOneIsOpen_IsTurnedAwayAndDoesNotReplaceIt()
    {
        // Unreachable by construction today - one verification runs per view, once - and
        // pinned so it stops the second connection rather than becoming a hang if that
        // changes. Replacing the open question would leave its caller waiting on something
        // nobody can see.
        RdpTrustPromptSession session = new();
        RdpCertificatePromptDialogViewModel first = await QuestionAsync();
        RdpCertificatePromptDialogViewModel second = await QuestionAsync();
        Task<RdpTrustAnswer> pending = session.AskAsync(first, CancellationToken.None);

        Task<RdpTrustAnswer> turnedAway = session.AskAsync(second, CancellationToken.None);

        Assert.Equal(RdpTrustAnswer.NotAsked, await turnedAway.WaitAsync(CompletionTimeout));
        Assert.Same(first, session.Question);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task QuestionChanged_IsRaisedOnceWhenItOpensAndOnceWhenItSettles()
    {
        // The view hides and shows the overlay off this event, so a settlement that raises it
        // twice paints twice and one that never raises it leaves the overlay up for good.
        RdpTrustPromptSession session = new();
        List<bool> visible = [];
        session.QuestionChanged += () => visible.Add(session.Question is not null);
        RdpCertificatePromptDialogViewModel question = await QuestionAsync();

        Task<RdpTrustAnswer> pending = session.AskAsync(question, CancellationToken.None);
        question.RefuseCommand.Execute(null);
        _ = await pending.WaitAsync(CompletionTimeout);

        Assert.Equal([true, false], visible);
    }

    [Fact]
    public async Task ClosingAPaneThatWasAskingNothing_RaisesNoChange()
    {
        RdpTrustPromptSession session = new();
        int changes = 0;
        session.QuestionChanged += () => changes++;

        session.Close();

        Assert.Equal(0, changes);
        await Task.CompletedTask;
    }

    private static void Execute(
        RdpCertificatePromptDialogViewModel question,
        RdpTrustAnswer answer)
    {
        switch (answer)
        {
            case RdpTrustAnswer.TrustPermanently:
                question.TrustCommand.Execute(null);
                break;
            case RdpTrustAnswer.TrustForSession:
                question.TrustOnceCommand.Execute(null);
                break;
            default:
                question.RefuseCommand.Execute(null);
                break;
        }
    }

    private static async Task<RdpCertificatePromptDialogViewModel> QuestionAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

        return new RdpCertificatePromptDialogViewModel(
            localizer,
            new RdpCertificatePromptContext(
                "DC pool", "127.0.0.1", "SHA256:AA:BB:01", "CN=dc04", 0),
            new RdpTrustPromptOrigin(
                "dc-pool.example.com:3389 via localhost:53211",
                "gw-paris",
                "Production",
                "Heimdall"));
    }
}
