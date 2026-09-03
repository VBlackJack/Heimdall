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
/// Puts the RDP certificate question inside the session pane that raised it.
/// </summary>
/// <remarks>
/// <para><b>Every path that is not an answer returns <see cref="RdpTrustAnswer.NotAsked"/>, and
/// none of them is approval.</b> No registered surface, a cancelled token, a pane closed by its
/// user: nobody decided anything in that pane. What the separate value buys is the sentence the
/// pane then shows. Refusing on those paths made "you did not approve the certificate this server
/// presented" the report of a question nobody was ever shown, which is a false claim shipped by a
/// change whose subject is false claims.</para>
/// <para><b>What a display returning it does NOT settle is whether the connection stops.</b> This
/// type hands the value to <see cref="RdpTrustQuestionCoalescer"/>, which reads it as "nobody
/// answered in that pane" and then looks for an answer given in another pane holding the same
/// question. A pane whose question was withdrawn because someone else approved is handed that
/// approval and connects. Only a pane asking alone, or one whose own connection was given up,
/// stops on it. The value this type finally RETURNS does stop the connection, because by then it
/// is the answer nobody gave anywhere - but that is a property of the return, not of the paths
/// above, and the two were written as one claim for long enough to be worth separating here.
/// </para>
/// <para><b>The question is asked where the connection is, and nowhere else.</b> It used to be a
/// top-level <c>Window</c> shown with <c>ShowDialog()</c>, owned by whatever the application
/// called its main window. Two consequences, and the second is the reason this type exists.
/// <c>ShowDialog()</c> is application-modal whatever its owner, so while any question was on
/// screen every other window was disabled at the Win32 level and the Cancel button each
/// connecting session displays reported itself enabled and could not be clicked. And the
/// question identified its subject by profile name and the address that was dialled - which,
/// for a session tunnelled over SSH, is 127.0.0.1 for every profile in the application. Two
/// tunnelled profiles both named "Production", detached into separate windows, both connecting,
/// produced two identical questions at the main window; a certificate could be approved for the
/// wrong machine, and no amount of re-owning the window would have changed that.</para>
/// <para><b>The answer is shared; the display is not, and neither is the queue.</b> Two panes
/// meeting the same certificate for the same profile each draw the question, and the first answer
/// settles both - see <see cref="RdpTrustQuestionCoalescer"/> for why one shared display across
/// two panes could not be made honest. The application-wide queue is gone as well: each question
/// is drawn inside its own pane, so none can hide another, while queueing cost the panes at the
/// back an unexplained wait - which is what the connect watchdog then had to be suspended
/// over.</para>
/// </remarks>
internal sealed class PaneRdpCertificateTrustPrompt(
    RdpTrustQuestionCoalescer coalescer,
    RdpTrustPromptSurfaceRegistry surfaces) : IRdpCertificateTrustPrompt
{
    private readonly RdpTrustQuestionCoalescer _coalescer =
        coalescer ?? throw new ArgumentNullException(nameof(coalescer));

    private readonly RdpTrustPromptSurfaceRegistry _surfaces =
        surfaces ?? throw new ArgumentNullException(nameof(surfaces));

    /// <inheritdoc />
    public async Task<RdpTrustAnswer> AskAsync(
        RdpCertificatePromptContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (cancellationToken.IsCancellationRequested)
        {
            return RdpTrustAnswer.NotAsked;
        }

        try
        {
            return await _coalescer.AskAsync(
                BuildKey(context),
                displayCt => DisplayAsync(context, displayCt),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return RdpTrustAnswer.NotAsked;
        }
    }

    /// <summary>Builds the key that decides which questions are the same question.</summary>
    /// <param name="context">The certificate being asked about.</param>
    /// <remarks>
    /// <b>The profile is part of the key.</b> RDP trust is stored per profile, so two
    /// profiles that meet the same unknown certificate at the same moment are asking two
    /// different questions. Coalescing them would show one question, naming one profile,
    /// and write the answer into both trust sets - durable trust granted from a question
    /// the user was never shown.
    /// <para>
    /// The scope token is deliberately NOT part of it. It identifies a pane, and two panes of
    /// one profile meeting one certificate are asking one question: keying on the pane would
    /// turn the coalescing off exactly where it is wanted, and make the user answer the same
    /// question twice. Each pane runs its own display rather than sharing one - that part is not
    /// coalesced - and the first answer settles the question everywhere and takes it off the
    /// other screens. What is NOT promised is that every bound pane drew anything: a pane joins
    /// under the lock and reaches its display afterwards, so an answer given in that gap
    /// withdraws it before it draws, and it takes the answer without having shown it. That is
    /// deliberate, and <see cref="RdpTrustQuestionCoalescer"/> is where the reasoning lives.
    /// </para>
    /// <para>
    /// The port is not part of it either: the thumbprint already identifies the certificate,
    /// and the same certificate on two ports of one host is the same fact.
    /// </para>
    /// </remarks>
    internal static TrustPromptKey BuildKey(RdpCertificatePromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TrustPromptKey.Create(
            TrustPromptKind.RdpCertificate,
            context.Host,
            port: 0,
            context.Thumbprint,
            scope: context.ProfileId ?? string.Empty);
    }

    private Task<RdpTrustAnswer> DisplayAsync(
        RdpCertificatePromptContext context,
        CancellationToken ct)
    {
        IRdpTrustPromptSurface? surface = _surfaces.Find(context.PromptScopeId);
        if (surface is null)
        {
            // Nothing to ask on, so nothing was asked - and that is exactly what is reported.
            // Falling back to a window of this layer's choosing is the defect being removed,
            // not a graceful degradation: it is how a question about one machine came to be
            // answered at another machine's window. Reporting it as a refusal was a smaller
            // version of the same lie, told to the user instead of to the trust store.
            // The sentence stops at what this method knows. It used to end "the connection
            // stops", which the coalescer above may well not do: another pane holding the same
            // question can have answered, and this connection is then handed that answer.
            FileLogger.Warn(
                "[RdpCertPrompt] no session surface is registered for scope "
                + $"'{context.PromptScopeId}' (profile '{context.ProfileName}'); the question "
                + "was not asked here, and no answer was recorded for it.");
            return Task.FromResult(RdpTrustAnswer.NotAsked);
        }

        return surface.AskAsync(context, ct);
    }
}
