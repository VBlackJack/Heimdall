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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Certificates;
using Heimdall.Core.Logging;

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// Holds the certificate question one session pane is waiting on, and settles every way of
/// not answering it as a question nobody answered.
/// </summary>
/// <remarks>
/// <para><b>Why this is a type and not three fields in the code-behind.</b> The question used
/// to be a <c>Window.ShowDialog()</c>, whose own message pump did the waiting and whose
/// application modality did the blocking. Neither survives the move into the pane: the wait is
/// now a completion source, and the blocking is now "this connection, and nothing else". Both
/// are decisions with edges - a pane torn down mid-question, a token cancelled, an answer
/// arriving after either - and all of them are playable here against no WPF at all.</para>
/// <para><b>Every exit that is not an answer is <see cref="RdpTrustAnswer.NotAsked"/>, and every
/// one of them still stops the connection.</b> The pane closing, the request being withdrawn, a
/// second question arriving while one is open: none of those is approval, and the alternative is
/// opening a session nobody approved. This is the same rule the window enforced through "the
/// title-bar cross is not an answer", moved to where the question now lives.</para>
/// <para><b>Why it is not <see cref="RdpTrustAnswer.Refuse"/>.</b> Refuse is what a person
/// pressed, and the pane says so out loud: "you did not approve the certificate this server
/// presented". A teardown reported as Refuse puts that sentence in front of a user who was asked
/// nothing. Escape and the pane's own Do-not-connect button still record a real refusal, because
/// there a person really did decide.</para>
/// <para><b>Dismissal by keyboard is still an answer.</b>
/// <see cref="RdpCertificatePromptDialogViewModel.RefuseFromDismissal"/> is reached from Escape
/// on a question the user is looking at; the teardown paths below never go through it.</para>
/// <para><b>A withdrawal is applied where the question is displayed, and that is what makes it
/// safe.</b> A withdrawal arrives on a pool thread - another pane's answer runs the coalescer's
/// continuation - while the buttons that answer this one are hit-tested on the UI thread. Settle
/// it on the arriving thread and there is a window, one dispatcher hop wide, in which the question
/// is still on screen, still enabled and no longer answerable: a person who pressed Do-not-connect
/// inside it had their refusal dropped, and the pane went on to adopt the approval given in the
/// other pane and open the session. Handing the withdrawal to
/// <see cref="RdpTrustPromptSession(Action{Action})"/>'s display thread closes the window rather
/// than narrowing it - the settlement and the hiding are then one work item on the thread that
/// dispatches the click, so the question stops being answerable at the instant it stops being
/// visible, and a press that reaches it first is simply a press on a live question.</para>
/// <para><b>What a press arriving after the withdrawal has begun means.</b> Once the withdrawal is
/// in flight the certificate has already been decided elsewhere, and the person can no longer
/// approve anything: a grant would write the trust store on the strength of a question the
/// application had already taken back. A refusal still counts, because it decides something that
/// is still open - whether THIS pane connects - and a person who presses Do-not-connect must not
/// watch the session open. Both directions are the same rule the lot is built on: a pane reports
/// what its own user did, and the safe direction is a stopped connection.</para>
/// <para>Thread-safe. <see cref="AskAsync"/> is called by the trust-prompt coordinator, the
/// cancellation callback fires on whichever thread cancelled and hands its settlement to the
/// display thread, and an answer arrives on the display thread already.
/// <see cref="QuestionChanged"/> is raised by whichever settlement cleared the question, so the
/// view still marshals it before touching any element - <see cref="Close"/> is the one
/// settlement that runs wherever it is called.</para>
/// </remarks>
internal sealed class RdpTrustPromptSession
{
    private readonly object _sync = new();
    private readonly Action<Action> _onDisplayThread;
    private Pending? _pending;
    private bool _closed;

    /// <summary>Creates a session whose question is displayed on the calling thread.</summary>
    /// <remarks>
    /// For a caller with no display of its own, and for the tests, which drive the ordering by
    /// hand rather than owning a dispatcher.
    /// </remarks>
    public RdpTrustPromptSession()
        : this(static step => step())
    {
    }

    /// <summary>Creates a session whose question is displayed elsewhere.</summary>
    /// <param name="onDisplayThread">
    /// Runs one step where the question is drawn and where its buttons are clicked. The view
    /// passes its dispatcher; a withdrawal is handed to this rather than applied in place, so
    /// no press can land on a question that has already stopped being answerable.
    /// </param>
    public RdpTrustPromptSession(Action<Action> onDisplayThread)
    {
        ArgumentNullException.ThrowIfNull(onDisplayThread);
        _onDisplayThread = onDisplayThread;
    }

    /// <summary>Raised whenever <see cref="Question"/> starts or stops being non-null.</summary>
    public event Action? QuestionChanged;

    /// <summary>The question awaiting an answer, or null when the pane is asking nothing.</summary>
    public RdpCertificatePromptDialogViewModel? Question
    {
        get
        {
            lock (_sync)
            {
                return _pending?.ViewModel;
            }
        }
    }

    /// <summary>Puts <paramref name="question"/> to this pane and waits for its answer.</summary>
    /// <param name="question">The question, already worded for this pane.</param>
    /// <param name="cancellationToken">Withdraws the question.</param>
    public Task<RdpTrustAnswer> AskAsync(
        RdpCertificatePromptDialogViewModel question,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (cancellationToken.IsCancellationRequested)
        {
            // Withdrawn before it was put, which is the ordinary way a pane joins a question a
            // person has just answered in another pane. Settled here rather than through the
            // registration below because that one is now applied on the display thread: a
            // question withdrawn one hop after being shown would flash up and vanish.
            return Task.FromResult(RdpTrustAnswer.NotAsked);
        }

        Pending pending;
        lock (_sync)
        {
            if (_closed)
            {
                // The pane is gone, so the question reached nobody. It still stops the
                // connection; it is simply not reported as an answer the user gave.
                FileLogger.Info(
                    "[RdpCertPrompt] question arrived after the pane closed; it was not asked.");
                return Task.FromResult(RdpTrustAnswer.NotAsked);
            }

            if (_pending is not null)
            {
                // Unreachable by construction: one verification runs per view, once. Turning it
                // away rather than replacing keeps the invariant honest if that ever changes -
                // overwriting would leave the first caller waiting on a question nobody can
                // see, which is a hang, and the safe direction here is a stopped connection.
                FileLogger.Warn(
                    "[RdpCertPrompt] a second question arrived while one was open; it was not "
                    + "asked.");
                return Task.FromResult(RdpTrustAnswer.NotAsked);
            }

            pending = new Pending(question);
            _pending = pending;
        }

        question.Answered += OnAnswered;

        // Registered after the field is set, and routed through the same settlement as an
        // answer. A withdrawal that only completed the task would leave the question on
        // screen for a connection that has already given up on it.
        pending.Registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => Withdraw(pending))
            : default;

        QuestionChanged?.Invoke();
        return pending.Completion.Task;
    }

    /// <summary>Ends the pane, abandoning anything it was still asking.</summary>
    /// <remarks>
    /// Idempotent, and permanent: a pane does not reopen. The question it was holding stops the
    /// connection but is not recorded as an answer, because closing a pane is something the user
    /// did to the pane and not something they said about the certificate.
    /// </remarks>
    public void Close()
    {
        Pending? pending;
        lock (_sync)
        {
            _closed = true;
            pending = _pending;
        }

        if (pending is not null)
        {
            Settle(pending, RdpTrustAnswer.NotAsked);
        }
    }

    /// <summary>Takes the question back, on the thread that draws it.</summary>
    /// <remarks>
    /// <para>The marking and the settling are deliberately apart. The mark is what makes the
    /// withdrawal visible to a press that beats the posted step - the certificate is decided
    /// from the moment the cancellation is raised, whatever thread raised it - while the settling
    /// waits for the display thread, so it cannot land between a person seeing the question and
    /// their click being dispatched.</para>
    /// <para>The one liveness this adds: a pane whose display thread never runs the step is left
    /// holding a question that never resolves. <see cref="Close"/> settles it, and the pane's own
    /// teardown calls <see cref="Close"/>, so the case is a dispatcher that has stopped while the
    /// pane it belongs to is still alive - the application on its way out, where the connection
    /// this would have resumed has nowhere to go either. The overlay already depended on that
    /// same hop to be hidden at all.</para>
    /// </remarks>
    private void Withdraw(Pending pending)
    {
        lock (_sync)
        {
            pending.Withdrawing = true;
        }

        _onDisplayThread(() => Settle(pending, RdpTrustAnswer.NotAsked));
    }

    private void OnAnswered(RdpTrustAnswer answer)
    {
        Pending? pending;
        bool withdrawing;
        lock (_sync)
        {
            pending = _pending;
            withdrawing = pending?.Withdrawing == true;
        }

        if (pending is null)
        {
            return;
        }

        if (withdrawing && answer != RdpTrustAnswer.Refuse)
        {
            // Pressed on a question the application has already taken back, because the
            // certificate was decided in another pane. A refusal below still settles this pane -
            // whether THIS session opens is still the person's to decide - but an approval here
            // would write the trust store from a question that no longer exists, and the
            // reconnect that asks it again costs them one click.
            //
            // The press does stay on the question: RdpCertificatePromptDialogViewModel.Record
            // has already written it, and its first-press rule then holds for anything pressed
            // after it. What is refused here is letting it settle the connection.
            FileLogger.Info(
                $"[RdpCertPrompt] '{answer}' was pressed after the question had been withdrawn; "
                + "it does not settle this pane, and the connection stops.");
            return;
        }

        Settle(pending, answer);
    }

    private void Settle(Pending pending, RdpTrustAnswer answer)
    {
        bool cleared;
        lock (_sync)
        {
            cleared = ReferenceEquals(_pending, pending);
            if (cleared)
            {
                _pending = null;
            }
        }

        pending.ViewModel.Answered -= OnAnswered;
        pending.Registration.Dispose();

        // The result may already be set - a teardown landing on a question an answer or a
        // posted withdrawal has already settled - and the first one wins. What must not race is
        // the view being told: only the caller that actually cleared the field raises the
        // change, so the overlay is hidden exactly once.
        _ = pending.Completion.TrySetResult(answer);

        if (cleared)
        {
            QuestionChanged?.Invoke();
        }
    }

    private sealed class Pending(RdpCertificatePromptDialogViewModel viewModel)
    {
        public RdpCertificatePromptDialogViewModel ViewModel { get; } = viewModel;

        public TaskCompletionSource<RdpTrustAnswer> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }

        /// <summary>Whether the question has been taken back but not yet settled.</summary>
        /// <remarks>Written and read under the session's lock, like every other shared field.</remarks>
        public bool Withdrawing { get; set; }
    }
}
