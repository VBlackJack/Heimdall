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
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// Which panes an answer binds, played against the real session that displays the question.
/// </summary>
/// <remarks>
/// <para><c>PaneRdpCertificateTrustPromptTests</c> pins where a question is asked and which
/// questions are one question, with a stub for the display. This file pins the seam between the
/// two types instead - the coalescer joining a pane, and <see cref="RdpTrustPromptSession"/>
/// deciding whether that pane ever draws anything - because the claim that turned out to be false
/// lives exactly there and is invisible from either side alone.</para>
/// <para><b>The claim that was false.</b> The design was written down as "every pane bound by an
/// answer was shown the question it answers". A pane joins under the coalescer's lock and reaches
/// its display afterwards, so an answer given in that gap withdraws the pane before it draws
/// anything, and the pane is bound all the same. The claim has been dropped from the coalescer's
/// own remarks and replaced by the property that does hold; what stands here is the behaviour, so
/// that decision cannot quietly turn back into the accident it was.</para>
/// <para>No window and no <c>UserControl</c> is built: building a WPF <c>Window</c> in a test
/// seals application-level styles onto the shared dispatcher and takes unrelated tests down with
/// it.</para>
/// </remarks>
public sealed class RdpTrustQuestionCoalescerTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task APaneWithdrawnBeforeItReachesItsDisplay_IsBoundByTheAnswerAnyway()
    {
        // The interleaving the review found, frozen as the decision it now is rather than left
        // as the accident it was. The second pane joins the open question, and the first pane's
        // answer publishes and withdraws it before it reaches its display: it shows nothing, it
        // records nothing, and it connects on the approval given next door.
        //
        // That is the coalescing at its narrowest, and it is safe because the two panes are
        // asking one question - same profile, same host, same certificate, which is all a
        // TrustPromptKey is. Making such a pane ask afresh instead would put a second question
        // about a certificate the same person approved a moment earlier in front of them.
        //
        // Two mutants make this red, and they are the two halves of that sentence. Disable the
        // already-cancelled check at the top of RdpTrustPromptSession.AskAsync and the pane goes
        // on to hold the question, so it raises the change its view draws from - twice, once for
        // a question and once for its immediate withdrawal - and the display assertion fails.
        // Stop the coalescer handing on the published answer and the pane stops connecting, so
        // the answer assertion fails.
        //
        // The display assertion counts the changes rather than sampling the property: what the
        // view knows about a question is exactly this event, and a pane that raises nothing has
        // told it nothing. Sampling Question inside the handler would have missed the mutant
        // entirely - by the time the first change is raised the withdrawal registered a line
        // earlier has already cleared it, so every sample reads null and the pane looks silent
        // while it is not.
        RdpTrustQuestionCoalescer coalescer = new();
        TrustPromptKey key = Key();

        RdpTrustPromptSession answering = new();
        RdpCertificatePromptDialogViewModel answeringQuestion = await QuestionAsync();

        RdpTrustPromptSession joining = new();
        RdpCertificatePromptDialogViewModel joiningQuestion = await QuestionAsync();
        List<bool> joiningToldItsView = [];
        joining.QuestionChanged += () => joiningToldItsView.Add(joining.Question is not null);

        TaskCompletionSource joined = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<RdpTrustAnswer> answered = coalescer.AskAsync(
            key,
            ct => answering.AskAsync(answeringQuestion, ct),
            CancellationToken.None);

        Task<RdpTrustAnswer> inherited = coalescer.AskAsync(
            key,
            async ct =>
            {
                // Joined, and held here. This is the gap: a participant of the open question
                // that has not yet reached the type which would draw it.
                await joined.Task.ConfigureAwait(false);
                return await joining.AskAsync(joiningQuestion, ct).ConfigureAwait(false);
            },
            CancellationToken.None);

        answeringQuestion.TrustCommand.Execute(null);
        Assert.Equal(
            RdpTrustAnswer.TrustPermanently,
            await answered.WaitAsync(CompletionTimeout));

        joined.SetResult();

        Assert.Equal(
            RdpTrustAnswer.TrustPermanently,
            await inherited.WaitAsync(CompletionTimeout));
        Assert.Empty(joiningToldItsView);
        Assert.Null(joiningQuestion.Answer);
        Assert.Equal(0, coalescer.OpenQuestionCount);
    }

    [Fact]
    public async Task APaneWhoseOwnConnectionWasGivenUp_TakesNoAnswerFromAnotherPane()
    {
        // The boundary of the decision above, and the reason it is a boundary rather than a
        // slope. A pane is bound by an answer to the question it joined; it is NOT bound by one
        // once its own user has given up on the connection that asked. Someone pressed Cancel
        // here, and a session opening afterwards - on an approval given in a pane they may not
        // even be looking at - is a session they stopped.
        //
        // The mutant: drop the coalescer's own-token check, which sits between this pane's
        // NotAsked and the published answer. Nothing else in the type tells the two ways a
        // display can be withdrawn apart, so without it this pane connects.
        //
        // The ordering is forced rather than waited for. The abandoned pane's session is handed
        // a queue for its display thread, so the withdrawal raised by the other pane's answer is
        // still in flight when this pane's own token is cancelled, and it settles only when the
        // queue is pumped.
        RdpTrustQuestionCoalescer coalescer = new();
        TrustPromptKey key = Key();

        RdpTrustPromptSession answering = new();
        RdpCertificatePromptDialogViewModel answeringQuestion = await QuestionAsync();

        Queue<Action> display = new();
        RdpTrustPromptSession abandoning = new(display.Enqueue);
        RdpCertificatePromptDialogViewModel abandoningQuestion = await QuestionAsync();
        using CancellationTokenSource givenUp = new();

        Task<RdpTrustAnswer> answered = coalescer.AskAsync(
            key,
            ct => answering.AskAsync(answeringQuestion, ct),
            CancellationToken.None);

        Task<RdpTrustAnswer> abandoned = coalescer.AskAsync(
            key,
            ct => abandoning.AskAsync(abandoningQuestion, ct),
            givenUp.Token);

        Assert.Same(abandoningQuestion, abandoning.Question);

        answeringQuestion.TrustCommand.Execute(null);
        Assert.Equal(
            RdpTrustAnswer.TrustPermanently,
            await answered.WaitAsync(CompletionTimeout));

        await givenUp.CancelAsync();
        Pump(display);

        Assert.Equal(RdpTrustAnswer.NotAsked, await abandoned.WaitAsync(CompletionTimeout));
        Assert.Null(abandoningQuestion.Answer);
    }

    /// <summary>Runs what the thread showing a question has been handed, in order.</summary>
    private static void Pump(Queue<Action> display)
    {
        while (display.Count > 0)
        {
            display.Dequeue()();
        }
    }

    /// <summary>One certificate met by one profile: what makes two panes ask one question.</summary>
    private static TrustPromptKey Key() => TrustPromptKey.Create(
        TrustPromptKind.RdpCertificate,
        "127.0.0.1",
        port: 0,
        "SHA256:AA:BB:01",
        scope: "profile-dc-pool");

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
