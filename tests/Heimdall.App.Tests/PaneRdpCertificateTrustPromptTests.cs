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
using Heimdall.Core.Certificates;

namespace Heimdall.App.Tests;

/// <summary>
/// Where an RDP certificate question is asked, which questions are one question, and what each
/// pane is told about the answer.
/// </summary>
/// <remarks>
/// <para><b>The routing is the security property.</b> The question used to be a top-level window
/// owned by whatever the application called its main window, identified by profile name and by
/// the address that was dialled. For an SSH-tunnelled profile that address is 127.0.0.1, so two
/// tunnelled profiles both named "Production" produced two indistinguishable questions at one
/// window and either answer could be given to the wrong machine. Every test below that names a
/// scope is measuring that this cannot happen again.</para>
/// <para><b>The coalescing was then reconsidered, and this file is where the new decision
/// lives.</b> The first in-pane version shared one DISPLAY between the panes: it ran the display
/// of whichever pane got there first and handed that one return value to every pane waiting. Two
/// things followed, and both were reported to a user as facts. Cancelling the displaying pane did
/// not withdraw the question, so an abandoned pane kept a live certificate question over a
/// collapsed surface and answering it opened a different pane's session. Closing the displaying
/// pane settled the shared question as a refusal, so the other pane reported "you did not approve
/// the certificate this server presented" about a question it had never shown.</para>
/// <para><b>What is shared now is the answer, not the display.</b> Each pane runs its own
/// display rather than waiting on another pane's; the first real answer settles them all and
/// takes it off the other screens. The user still answers once, no pane holds a question that
/// decides something elsewhere, and no pane is handed an outcome produced by a teardown in
/// another pane.</para>
/// <para><b>"Every pane draws the question" is NOT what is claimed, and this file is not where
/// that claim would be measured.</b> A pane joins the question under the lock and reaches its
/// display afterwards, so an answer given in that gap withdraws it before it draws anything and
/// it takes the answer having shown nothing. That is a deliberate decision - the alternative
/// asks a second question about a certificate the same person approved a moment earlier - and it
/// is stated in <c>RdpTrustQuestionCoalescer</c> and measured in
/// <c>RdpTrustQuestionCoalescerTests</c>. What the tests below establish is the property that
/// does hold: an answer binds a pane only when that pane was asking the SAME question and was
/// still asking it when the answer was given.</para>
/// <para>No window and no <c>UserControl</c> is built here: the surface is an interface, and
/// building a WPF <c>Window</c> in a test seals application-level styles onto the shared
/// dispatcher and takes unrelated tests down with it.</para>
/// </remarks>
public sealed class PaneRdpCertificateTrustPromptTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AskAsync_PutsTheQuestionToThePaneThatAskedIt_AndToNoOther()
    {
        RdpTrustPromptSurfaceRegistry registry = new();
        RecordingSurface asked = new(RdpTrustAnswer.TrustPermanently);
        RecordingSurface other = new(RdpTrustAnswer.TrustPermanently);
        using IDisposable one = registry.Register("pane-asked", asked);
        using IDisposable two = registry.Register("pane-other", other);

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        RdpTrustAnswer answer = await prompt
            .AskAsync(Context("profile-a", scopeId: "pane-asked"), CancellationToken.None)
            .WaitAsync(CompletionTimeout);

        Assert.Equal(RdpTrustAnswer.TrustPermanently, answer);
        Assert.Equal(1, asked.Displays);
        Assert.Equal(0, other.Displays);
    }

    [Fact]
    public async Task AskAsync_NoSurfaceForTheScope_IsNotAskedRatherThanAskedSomewhereElse()
    {
        // The behaviour that replaced "own the dialog to the main window". A question nobody
        // can place is not a question, and answering it at an unrelated window is how a
        // certificate came to be approved for a machine the user was not looking at.
        //
        // NotAsked and not Refuse: reporting it as a refusal was a smaller version of the same
        // lie, told to the user instead of to the trust store - the pane would have said "you
        // did not approve the certificate this server presented" about a question that reached
        // nobody.
        //
        // Nothing is opened here, because this is the only pane asking and so there is no other
        // answer to fall back on. That is a property of THIS arrangement and not of NotAsked: a
        // pane sharing its question with another is handed the answer given there.
        RdpTrustPromptSurfaceRegistry registry = new();
        RecordingSurface elsewhere = new(RdpTrustAnswer.TrustPermanently);
        using IDisposable registration = registry.Register("pane-elsewhere", elsewhere);

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        RdpTrustAnswer answer = await prompt
            .AskAsync(Context("profile-a", scopeId: "pane-gone"), CancellationToken.None)
            .WaitAsync(CompletionTimeout);

        Assert.Equal(RdpTrustAnswer.NotAsked, answer);
        Assert.NotEqual(RdpTrustAnswer.Refuse, answer);
        Assert.Equal(0, elsewhere.Displays);
    }

    [Fact]
    public async Task AskAsync_ContextWithNoScopeAtAll_IsNotAsked()
    {
        RdpTrustPromptSurfaceRegistry registry = new();
        RecordingSurface surface = new(RdpTrustAnswer.TrustPermanently);
        using IDisposable registration = registry.Register("pane-a", surface);

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        RdpTrustAnswer answer = await prompt
            .AskAsync(Context("profile-a", scopeId: null), CancellationToken.None)
            .WaitAsync(CompletionTimeout);

        Assert.Equal(RdpTrustAnswer.NotAsked, answer);
        Assert.Equal(0, surface.Displays);
    }

    [Fact]
    public async Task AskAsync_TwoPanesOfOneProfile_BothSeeIt_AndOneAnswerSettlesBoth()
    {
        // The reconsidered decision, stated as behaviour. The user answers once - that is the
        // coalescing, and it is kept - but each pane draws the question itself, so neither is
        // waiting silently on something happening in a window it cannot see. The pane that did
        // not answer has its question withdrawn rather than left standing.
        RdpTrustPromptSurfaceRegistry registry = new();
        GatedSurface first = new();
        GatedSurface second = new();
        using IDisposable one = registry.Register("pane-1", first);
        using IDisposable two = registry.Register("pane-2", second);

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        Task<RdpTrustAnswer> a = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-1"), CancellationToken.None);
        Task<RdpTrustAnswer> b = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-2"), CancellationToken.None);

        await Task.WhenAll(first.Displayed.Task, second.Displayed.Task)
            .WaitAsync(CompletionTimeout);
        Assert.Equal(1, first.Displays);
        Assert.Equal(1, second.Displays);

        first.Answer(RdpTrustAnswer.TrustForSession);
        RdpTrustAnswer[] answers = await Task.WhenAll(a, b).WaitAsync(CompletionTimeout);

        Assert.All(answers, answer => Assert.Equal(RdpTrustAnswer.TrustForSession, answer));
        Assert.Equal(1, second.Withdrawals);
    }

    [Fact]
    public async Task AskAsync_CancellingTheDisplayingPane_LeavesTheOtherPaneItsOwnQuestion()
    {
        // Finding: a per-pane Cancel did not withdraw a shared display, because the display's
        // lifetime ended only when the LAST waiter left. The pane whose Cancel button the user
        // pressed kept a live certificate question on screen over a collapsed session surface,
        // and clicking "Trust this certificate" there to get rid of it granted durable trust
        // and opened the OTHER pane's session.
        RdpTrustPromptSurfaceRegistry registry = new();
        GatedSurface abandoned = new();
        GatedSurface surviving = new();
        using IDisposable one = registry.Register("pane-1", abandoned);
        using IDisposable two = registry.Register("pane-2", surviving);
        using CancellationTokenSource cancelledByItsUser = new();

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        Task<RdpTrustAnswer> a = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-1"), cancelledByItsUser.Token);
        Task<RdpTrustAnswer> b = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-2"), CancellationToken.None);

        await Task.WhenAll(abandoned.Displayed.Task, surviving.Displayed.Task)
            .WaitAsync(CompletionTimeout);
        await cancelledByItsUser.CancelAsync();

        // The cancelled pane's own question goes with it.
        Assert.Equal(RdpTrustAnswer.NotAsked, await a.WaitAsync(CompletionTimeout));
        Assert.Equal(1, abandoned.Withdrawals);

        // And the pane still connecting keeps a question that still decides its own connection.
        Assert.Equal(0, surviving.Withdrawals);
        Assert.False(b.IsCompleted);

        surviving.Answer(RdpTrustAnswer.TrustPermanently);
        Assert.Equal(RdpTrustAnswer.TrustPermanently, await b.WaitAsync(CompletionTimeout));
    }

    [Fact]
    public async Task AskAsync_TearingDownOnePane_DoesNotDecideForTheOther()
    {
        // Finding: closing the pane that was DISPLAYING settled the shared question as a
        // refusal, and that refusal was the return value of the shared display, so it was
        // published to every pane waiting. The user closed one tab without answering and a tab
        // they had never touched ended with "the server certificate was refused".
        //
        // A teardown is modelled the way RdpTrustPromptSession models it: the pane's own
        // question settles as NotAsked.
        RdpTrustPromptSurfaceRegistry registry = new();
        GatedSurface tornDown = new();
        GatedSurface untouched = new();
        using IDisposable one = registry.Register("pane-1", tornDown);
        using IDisposable two = registry.Register("pane-2", untouched);

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        Task<RdpTrustAnswer> a = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-1"), CancellationToken.None);
        Task<RdpTrustAnswer> b = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-2"), CancellationToken.None);

        await Task.WhenAll(tornDown.Displayed.Task, untouched.Displayed.Task)
            .WaitAsync(CompletionTimeout);
        tornDown.Answer(RdpTrustAnswer.NotAsked);

        Assert.Equal(RdpTrustAnswer.NotAsked, await a.WaitAsync(CompletionTimeout));

        // Nothing was decided for the untouched pane, and nothing was taken off its screen.
        Assert.False(b.IsCompleted);
        Assert.Equal(0, untouched.Withdrawals);

        untouched.Answer(RdpTrustAnswer.Refuse);
        Assert.Equal(RdpTrustAnswer.Refuse, await b.WaitAsync(CompletionTimeout));
    }

    [Fact]
    public async Task AskAsync_APaneThatAnswers_ReportsItsOwnAnswerAndNotTheSharedOne()
    {
        // Not a case one human produces, and the rule costs nothing: it removes the only way
        // this path could tell a user the opposite of what they pressed.
        RdpTrustPromptSurfaceRegistry registry = new();
        GatedSurface first = new();
        GatedSurface stubborn = new(honourWithdrawal: false);
        using IDisposable one = registry.Register("pane-1", first);
        using IDisposable two = registry.Register("pane-2", stubborn);

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        Task<RdpTrustAnswer> a = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-1"), CancellationToken.None);
        Task<RdpTrustAnswer> b = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-2"), CancellationToken.None);

        await Task.WhenAll(first.Displayed.Task, stubborn.Displayed.Task)
            .WaitAsync(CompletionTimeout);

        first.Answer(RdpTrustAnswer.TrustPermanently);
        Assert.Equal(RdpTrustAnswer.TrustPermanently, await a.WaitAsync(CompletionTimeout));

        stubborn.Answer(RdpTrustAnswer.Refuse);
        Assert.Equal(RdpTrustAnswer.Refuse, await b.WaitAsync(CompletionTimeout));
    }

    [Fact]
    public async Task AskAsync_AfterAnAnswer_ALaterConnectionIsAskedAfresh()
    {
        // A refusal writes nothing to the trust store, so a connection started afterwards meets
        // the same unknown certificate. It gets its own question rather than inheriting an answer
        // given to something else - and, in the refusal case, rather than being told it was
        // refused by a person who never saw it.
        //
        // A third pane is what makes this measure anything. Forgetting the question when the last
        // pane leaves happens anyway; forgetting it the moment it is ANSWERED is the separate
        // rule, and it is only visible while some pane is still winding down - which is exactly
        // when a fresh connection is most likely to arrive.
        RdpTrustPromptSurfaceRegistry registry = new();
        GatedSurface answered = new();
        GatedSurface stillWindingDown = new(honourWithdrawal: false);

        // A pane, not a stub that answers whatever it is handed: the failure to catch is a pane
        // whose question is withdrawn the instant it opens, and a surface that ignores
        // withdrawal reports a display that the user never had a chance to use.
        GatedSurface later = new();
        using IDisposable one = registry.Register("pane-1", answered);
        using IDisposable two = registry.Register("pane-2", stillWindingDown);
        using IDisposable three = registry.Register("pane-3", later);

        RdpTrustQuestionCoalescer coalescer = new();
        PaneRdpCertificateTrustPrompt prompt = new(coalescer, registry);
        Task<RdpTrustAnswer> a = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-1"), CancellationToken.None);
        Task<RdpTrustAnswer> b = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-2"), CancellationToken.None);

        await Task.WhenAll(answered.Displayed.Task, stillWindingDown.Displayed.Task)
            .WaitAsync(CompletionTimeout);
        answered.Answer(RdpTrustAnswer.Refuse);
        Assert.Equal(RdpTrustAnswer.Refuse, await a.WaitAsync(CompletionTimeout));

        // Pane 2 has been told to take the question down and has not finished doing so.
        Assert.Equal(1, stillWindingDown.Withdrawals);
        Assert.False(b.IsCompleted);

        Task<RdpTrustAnswer> c = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-3"), CancellationToken.None);
        await later.Displayed.Task.WaitAsync(CompletionTimeout);
        later.Answer(RdpTrustAnswer.TrustPermanently);

        Assert.Equal(1, later.Displays);
        Assert.Equal(0, later.Withdrawals);
        Assert.Equal(RdpTrustAnswer.TrustPermanently, await c.WaitAsync(CompletionTimeout));

        stillWindingDown.Answer(RdpTrustAnswer.NotAsked);
        _ = await b.WaitAsync(CompletionTimeout);
        Assert.Equal(0, coalescer.OpenQuestionCount);
    }

    [Fact]
    public async Task AskAsync_TwoProfilesMeetingOneCertificate_AreAskedSeparately()
    {
        // The rule that must survive the coalescing: RDP trust is per profile, so one question
        // naming profile A may never supply the answer for profile B - that would be durable
        // trust granted from a question the user was never shown.
        RdpTrustPromptSurfaceRegistry registry = new();
        GatedSurface first = new();
        GatedSurface second = new();
        using IDisposable one = registry.Register("pane-1", first);
        using IDisposable two = registry.Register("pane-2", second);

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        Task<RdpTrustAnswer> a = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-1"), CancellationToken.None);
        Task<RdpTrustAnswer> b = prompt.AskAsync(
            Context("profile-b", scopeId: "pane-2"), CancellationToken.None);

        await first.Displayed.Task.WaitAsync(CompletionTimeout);
        await second.Displayed.Task.WaitAsync(CompletionTimeout);
        first.Answer(RdpTrustAnswer.TrustPermanently);
        second.Answer(RdpTrustAnswer.Refuse);

        Assert.Equal(RdpTrustAnswer.TrustPermanently, await a.WaitAsync(CompletionTimeout));
        Assert.Equal(RdpTrustAnswer.Refuse, await b.WaitAsync(CompletionTimeout));
    }

    [Fact]
    public async Task AskAsync_TwoDifferentQuestions_AreBothOnScreenBeforeEitherIsAnswered()
    {
        // The application-wide queue, gone, measured rather than described. Under the old
        // serialization the second question was not displayed until the first had returned, so
        // this test cannot pass by luck: it waits for BOTH displays before answering either,
        // and would hang until its timeout with the queue in place.
        //
        // Why the queue had to go: each question is drawn inside its own pane, so none can hide
        // another, while queueing them left the pane at the back of the queue in Preparing for
        // minutes with nothing on screen to answer - which is the symptom the queue was
        // introduced to cure, moved one layer down.
        RdpTrustPromptSurfaceRegistry registry = new();
        GatedSurface first = new();
        GatedSurface second = new();
        using IDisposable one = registry.Register("pane-1", first);
        using IDisposable two = registry.Register("pane-2", second);

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        Task<RdpTrustAnswer> a = prompt.AskAsync(
            Context("profile-a", scopeId: "pane-1"), CancellationToken.None);
        Task<RdpTrustAnswer> b = prompt.AskAsync(
            Context("profile-b", scopeId: "pane-2"), CancellationToken.None);

        await Task.WhenAll(first.Displayed.Task, second.Displayed.Task)
            .WaitAsync(CompletionTimeout);

        first.Answer(RdpTrustAnswer.Refuse);
        second.Answer(RdpTrustAnswer.Refuse);
        _ = await Task.WhenAll(a, b).WaitAsync(CompletionTimeout);
    }

    [Fact]
    public async Task AskAsync_AlreadyCancelled_IsNotAskedAndNotShown()
    {
        RdpTrustPromptSurfaceRegistry registry = new();
        RecordingSurface surface = new(RdpTrustAnswer.TrustPermanently);
        using IDisposable registration = registry.Register("pane-1", surface);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        RdpTrustAnswer answer = await prompt
            .AskAsync(Context("profile-a", scopeId: "pane-1"), cts.Token)
            .WaitAsync(CompletionTimeout);

        Assert.Equal(RdpTrustAnswer.NotAsked, answer);
        Assert.Equal(0, surface.Displays);
    }

    [Fact]
    public void PromptKey_DiffersByProfile_SoTwoProfilesAreAskedSeparately()
        => Assert.NotEqual(
            PaneRdpCertificateTrustPrompt.BuildKey(Context("profile-a", scopeId: "pane-1")),
            PaneRdpCertificateTrustPrompt.BuildKey(Context("profile-b", scopeId: "pane-1")));

    [Fact]
    public void PromptKey_TwoPanesOfOneProfile_IsStillOneQuestion()
    {
        // The trap named in the brief, pinned: the scope token identifies a pane, and putting
        // it in the key would silently delete the coalescing - the same question answered
        // twice by the same user about the same certificate.
        Assert.Equal(
            PaneRdpCertificateTrustPrompt.BuildKey(Context("profile-a", scopeId: "pane-1")),
            PaneRdpCertificateTrustPrompt.BuildKey(Context("profile-a", scopeId: "pane-2")));
    }

    [Fact]
    public void PromptKey_DifferentCertificate_IsADifferentQuestion()
        => Assert.NotEqual(
            PaneRdpCertificateTrustPrompt.BuildKey(
                Context("profile-a", scopeId: "pane-1", thumbprint: "SHA256:AA:BB:01")),
            PaneRdpCertificateTrustPrompt.BuildKey(
                Context("profile-a", scopeId: "pane-1", thumbprint: "SHA256:CC:DD:02")));

    [Fact]
    public void PromptKey_NoProfile_IsStillBuildable()
    {
        // A context without a profile must not throw its way out of the prompt: the fallback
        // loses the separation, it does not lose the question.
        TrustPromptKey key = PaneRdpCertificateTrustPrompt.BuildKey(
            new RdpCertificatePromptContext("DC pool", "dc-pool.example.com", "SHA256:AA", null, 0));

        Assert.Equal(string.Empty, key.Scope);
    }

    private static RdpCertificatePromptContext Context(
        string profileId,
        string? scopeId,
        string thumbprint = "SHA256:AA:BB:01")
        => new("DC pool", "dc-pool.example.com", thumbprint, "CN=dc04", 0)
        {
            ProfileId = profileId,
            PromptScopeId = scopeId,
        };

    /// <summary>A surface that answers at once and counts how often it was asked.</summary>
    private sealed class RecordingSurface(RdpTrustAnswer answer) : IRdpTrustPromptSurface
    {
        private int _displays;

        public int Displays => Volatile.Read(ref _displays);

        public Task<RdpTrustAnswer> AskAsync(
            RdpCertificatePromptContext context,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _displays);
            return Task.FromResult(answer);
        }
    }

    /// <summary>A surface that keeps the question on screen until it is answered or withdrawn.</summary>
    /// <param name="honourWithdrawal">
    /// Whether the surface takes the question off its screen when the request is withdrawn, as
    /// a real pane does. False models the pane whose user answers in the same instant another
    /// pane does, which is the only way to observe which of the two answers it is told about.
    /// </param>
    /// <remarks>
    /// Withdrawal settles as <see cref="RdpTrustAnswer.NotAsked"/>, which is exactly what
    /// <c>RdpTrustPromptSession</c> does when its token fires: the question leaves the screen
    /// and nobody answered it.
    /// </remarks>
    private sealed class GatedSurface(bool honourWithdrawal = true) : IRdpTrustPromptSurface
    {
        private readonly TaskCompletionSource<RdpTrustAnswer> _answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _displays;
        private int _withdrawals;

        public TaskCompletionSource Displayed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Displays => Volatile.Read(ref _displays);

        /// <summary>How often this pane was told to take the question off its screen.</summary>
        public int Withdrawals => Volatile.Read(ref _withdrawals);

        public void Answer(RdpTrustAnswer answer) => _answer.TrySetResult(answer);

        public async Task<RdpTrustAnswer> AskAsync(
            RdpCertificatePromptContext context,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _displays);
            _ = Displayed.TrySetResult();

            using CancellationTokenRegistration withdrawal = cancellationToken.Register(() =>
            {
                _ = Interlocked.Increment(ref _withdrawals);
                if (honourWithdrawal)
                {
                    _ = _answer.TrySetResult(RdpTrustAnswer.NotAsked);
                }
            });

            return await _answer.Task;
        }
    }
}
