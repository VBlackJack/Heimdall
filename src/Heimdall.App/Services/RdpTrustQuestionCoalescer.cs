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

using Heimdall.Core.Certificates;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// Shares one answer between the panes that met the same certificate, without sharing the
/// question's display between them.
/// </summary>
/// <remarks>
/// <para><b>What this replaces, and why the earlier shape was wrong.</b> The first version of
/// the in-pane question reused <see cref="TrustPromptCoordinator"/>, which coalesces by running
/// the FIRST caller's display and handing its return value to everyone who joined. That is right
/// for a top-level modal window, which belongs to the application rather than to any one caller.
/// It is wrong for a question drawn inside a pane, and it was wrong in three ways that all come
/// from one fact - the display belonged to a single pane while the answer bound several:</para>
/// <list type="bullet">
/// <item><description>Cancelling the pane that was DISPLAYING did not withdraw the question,
/// because the display's lifetime ended only when the last waiter left. That pane kept a live
/// certificate question on screen over a collapsed session surface, and answering it granted
/// durable trust and opened a different pane's session.</description></item>
/// <item><description>Closing the displaying pane settled the shared question as a refusal, so
/// another pane's connection was refused by a teardown its user never saw.</description></item>
/// <item><description>That refusal reached the other pane's status line as "you did not approve
/// the certificate this server presented" - a sentence about an answer that pane was never
/// asked for.</description></item>
/// </list>
/// <para><b>The decision taken instead.</b> Every pane that meets the certificate displays the
/// question itself, and the first real answer settles it for all of them and withdraws it from
/// the others. Coalescing is kept where it was worth having - the user answers once, not once
/// per pane - and dropped where it was doing harm: no pane holds a question that decides
/// something happening elsewhere, and no pane is handed an outcome produced by a teardown in
/// another pane.</para>
/// <para><b>A pane whose answer settled reports that answer, never the shared one.</b> Two
/// answers racing is not a case one human produces, but the rule costs nothing and removes the
/// ordinary way this type could tell a user the opposite of what they pressed.</para>
/// <para><b>The one press that does not settle, and what this type then says about it.</b>
/// <c>RdpTrustPromptSession</c> declines an APPROVAL pressed after its question was withdrawn -
/// the certificate was decided here, and a grant would write the trust store from a question the
/// application had taken back. Such a pane reports <see cref="RdpTrustAnswer.NotAsked"/> from its
/// display, so it falls through to the shared answer below and can be told "you did not approve
/// the certificate" by a refusal given in the other pane. That is a false sentence, and it is
/// left standing knowingly: closing it means a fourth outcome and a fourth wording, and reaching
/// it costs one person two clicks in two panes inside a single dispatcher hop. A refusal in the
/// same window does settle, so the press that matters - the one after which a session must not
/// open - never falls through here.</para>
/// <para><b>An answer closes the question rather than lingering.</b> Once published, the entry is
/// forgotten immediately, so a pane arriving afterwards asks afresh. That matters for a refusal:
/// approval writes the trust store and a later pane never reaches a question at all, while a
/// refusal writes nothing, and a connection started after one deserves its own question rather
/// than an answer given to something else.</para>
/// <para>Thread-safe. Panes join and leave from whichever thread is running their connection. One
/// lock covers the open set, the participants and the published answer, because the three are one
/// state: a stale count and a live entry is how a question outlives the last pane asking it.
/// Cancellation is always raised after the lock is released, since a withdrawal runs the waiting
/// pane's own continuation.</para>
/// </remarks>
internal sealed class RdpTrustQuestionCoalescer
{
    private readonly object _sync = new();
    private readonly Dictionary<TrustPromptKey, Question> _open = [];

    /// <summary>How many questions are open, so a test can prove one was forgotten.</summary>
    internal int OpenQuestionCount
    {
        get
        {
            lock (_sync)
            {
                return _open.Count;
            }
        }
    }

    /// <summary>Asks this pane's question, sharing an answer with the panes asking the same one.</summary>
    /// <param name="key">What makes two panes' questions the same question.</param>
    /// <param name="askAsync">
    /// Puts the question inside the calling pane. Its token is cancelled when the caller's own
    /// token is, and also when another pane answers - which is how a question stops standing in
    /// front of a connection it no longer decides.
    /// </param>
    /// <param name="cancellationToken">Withdraws this pane's participation.</param>
    /// <returns>
    /// The answer a person gave in this pane; failing that, the answer a person gave in another
    /// pane holding the same question; failing that, <see cref="RdpTrustAnswer.NotAsked"/>.
    /// </returns>
    public async Task<RdpTrustAnswer> AskAsync(
        TrustPromptKey key,
        Func<CancellationToken, Task<RdpTrustAnswer>> askAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(askAsync);

        Question question;
        Participation participation = new(cancellationToken);
        bool alreadySettled;
        lock (_sync)
        {
            if (!_open.TryGetValue(key, out Question? existing))
            {
                existing = new Question();
                _open.Add(key, existing);
            }

            question = existing;
            alreadySettled = question.Join(participation);
        }

        if (alreadySettled)
        {
            // Joined a question a person has just answered. The display is withdrawn before it
            // is ever entered, so this pane shows nothing and takes the answer below.
            participation.Withdraw();
        }

        try
        {
            RdpTrustAnswer own = await askAsync(participation.Token).ConfigureAwait(false);
            if (own != RdpTrustAnswer.NotAsked)
            {
                Publish(key, question, own);
                return own;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // This pane's own user gave up on the connection. Whatever another pane decided
                // about the certificate, it did not decide this.
                return RdpTrustAnswer.NotAsked;
            }

            RdpTrustAnswer? shared = Published(question);
            if (shared is not null)
            {
                FileLogger.Info(
                    $"[RdpCertPrompt] pane adopted the answer '{shared}' given in another pane "
                    + "holding the same question.");
            }

            return shared ?? RdpTrustAnswer.NotAsked;
        }
        finally
        {
            Leave(key, question, participation);
            participation.Dispose();
        }
    }

    /// <summary>Records the answer, forgets the question, and withdraws it from every pane.</summary>
    /// <remarks>
    /// Forgotten before it is withdrawn, so a pane arriving during the withdrawal starts a
    /// question of its own instead of inheriting one that is already over.
    /// </remarks>
    private void Publish(TrustPromptKey key, Question question, RdpTrustAnswer answer)
    {
        Participation[] withdrawing;
        lock (_sync)
        {
            withdrawing = question.Settle(answer);
            Drop(key, question);
        }

        foreach (Participation participation in withdrawing)
        {
            participation.Withdraw();
        }
    }

    private RdpTrustAnswer? Published(Question question)
    {
        lock (_sync)
        {
            return question.Answer;
        }
    }

    private void Leave(TrustPromptKey key, Question question, Participation participation)
    {
        lock (_sync)
        {
            if (question.Leave(participation))
            {
                Drop(key, question);
            }
        }
    }

    /// <summary>Removes <paramref name="question"/>, and only it, from the open set.</summary>
    /// <remarks>Called under <c>_sync</c>.</remarks>
    private void Drop(TrustPromptKey key, Question question)
    {
        if (_open.TryGetValue(key, out Question? current)
            && ReferenceEquals(current, question))
        {
            _ = _open.Remove(key);
        }
    }

    /// <summary>One question, as many displays as there are panes asking it.</summary>
    /// <remarks>Every member is touched under the coalescer's lock and nowhere else.</remarks>
    private sealed class Question
    {
        private readonly List<Participation> _asking = [];
        private int _participants;

        /// <summary>The answer a person gave, or null while nobody has given one.</summary>
        public RdpTrustAnswer? Answer { get; private set; }

        /// <summary>Adds a pane.</summary>
        /// <returns>True when the question is already answered, so its display is pointless.</returns>
        public bool Join(Participation participation)
        {
            _participants++;
            if (Answer is not null)
            {
                return true;
            }

            _asking.Add(participation);
            return false;
        }

        /// <summary>Records the answer.</summary>
        /// <returns>The panes still displaying the question, which must now stop.</returns>
        public Participation[] Settle(RdpTrustAnswer answer)
        {
            Answer ??= answer;
            Participation[] withdrawing = [.. _asking];
            _asking.Clear();
            return withdrawing;
        }

        /// <summary>Removes a pane.</summary>
        /// <returns>True when it was the last one, so the question may be forgotten.</returns>
        public bool Leave(Participation participation)
        {
            _ = _asking.Remove(participation);
            _participants--;
            return _participants <= 0;
        }
    }

    /// <summary>
    /// One pane's stake in a question: the token its display runs under, cancelled by the pane's
    /// own withdrawal or by an answer arriving elsewhere.
    /// </summary>
    private sealed class Participation(CancellationToken cancellationToken) : IDisposable
    {
        private readonly CancellationTokenSource _cts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        public CancellationToken Token => _cts.Token;

        public void Withdraw()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The pane finished first and disposed its own stake. Nothing left to withdraw.
            }
        }

        public void Dispose() => _cts.Dispose();
    }
}
