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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the question asked when an RDP endpoint presents an unknown certificate.
/// </summary>
/// <remarks>
/// <para>Everything decidable lives here rather than in the view, so it can be tested without
/// constructing one: a WPF <c>Window</c> built in a test seals application-level styles
/// onto the shared dispatcher and takes unrelated tests down with it.</para>
/// <para><b>The question is still a dialog; it is no longer a window.</b> It is shown inside
/// the session pane that asked it, declares itself a dialog to UI Automation, and blocks that
/// one connection. It used to be a top-level <c>Window</c> shown with <c>ShowDialog()</c>,
/// which disabled every other window in the application for as long as any question was on
/// screen, and which could only ever be owned by the main window - so a question about a
/// tunnelled session named a profile and the address 127.0.0.1, at a window that had nothing
/// to do with it.</para>
/// </remarks>
public partial class RdpCertificatePromptDialogViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;

    /// <summary>Creates the ViewModel for one certificate question.</summary>
    /// <param name="localizer">Supplies the wording.</param>
    /// <param name="context">What the user needs in order to answer.</param>
    /// <param name="origin">
    /// Which machine the session actually reaches and where the question is being asked, or
    /// null when the caller knows neither - in which case the question falls back to the
    /// address that was dialled, which is what it showed before any of this existed.
    /// </param>
    public RdpCertificatePromptDialogViewModel(
        LocalizationManager localizer,
        RdpCertificatePromptContext context,
        RdpTrustPromptOrigin? origin = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(context);

        _localizer = localizer;
        Context = context;
        Origin = origin;
    }

    /// <summary>What was observed, and what the profile already trusts.</summary>
    public RdpCertificatePromptContext Context { get; }

    /// <summary>Which machine is being reached, and from which tab or window.</summary>
    public RdpTrustPromptOrigin? Origin { get; }

    /// <summary>
    /// The machine the question is about, as the user would name it.
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="RdpCertificatePromptContext.Host"/>.</b> That is the address that was
    /// dialled, and for a session tunnelled over SSH it is 127.0.0.1 for every profile in the
    /// application. A question that identifies its subject as 127.0.0.1 identifies nothing,
    /// and two such questions side by side are indistinguishable.
    /// </remarks>
    public string RemoteEndpoint => Origin?.RemoteEndpointLabel ?? Context.Host;

    /// <summary>Label above the machine name.</summary>
    public string RemoteEndpointLabel =>
        _localizer[RdpCertificatePromptLocaleKeys.RemoteEndpointLabel];

    /// <summary>
    /// The SSH gateways this session reaches the machine through, or null when it is direct.
    /// </summary>
    /// <remarks>
    /// <b>The part of the identity the endpoint could not carry.</b> Two profiles reaching one
    /// short name through two different gateways are two machines; their endpoint text differs
    /// only by an ephemeral local tunnel port, which the user has never seen and cannot map to
    /// anything. This is the line that tells those two questions apart.
    /// </remarks>
    public string? Route => Origin?.RouteLabel;

    /// <summary>Whether there is a route to show at all.</summary>
    public bool HasRoute => !string.IsNullOrWhiteSpace(Route);

    /// <summary>Label above the route.</summary>
    public string RouteLabel => _localizer[RdpCertificatePromptLocaleKeys.RouteLabel];

    /// <summary>Title of the question.</summary>
    public string Title => _localizer[RdpCertificatePromptLocaleKeys.Title];

    /// <summary>The body: this profile has never approved this certificate.</summary>
    public string Message => _localizer.Format(
        RdpCertificatePromptLocaleKeys.Message,
        Context.ProfileName,
        RemoteEndpoint);

    /// <summary>Which tab or window this question belongs to, or null when neither is known.</summary>
    /// <remarks>
    /// The question no longer arrives at a single shared window, so it has to say where it
    /// came from: a pane the user is not looking at can be holding one, and two panes of two
    /// similarly named profiles can be holding one each.
    /// </remarks>
    public string? OwnerText
    {
        get
        {
            RdpTrustPromptOwnerText? owner =
                RdpTrustPromptOwner.Describe(Origin?.TabTitle, Origin?.WindowTitle);
            return owner is null
                ? null
                : _localizer.Format(owner.Value.Key, owner.Value.Arguments);
        }
    }

    /// <summary>Whether there is an owner line to show at all.</summary>
    public bool HasOwnerText => OwnerText is not null;

    /// <summary>
    /// The reassurance line, or null when the profile trusts nothing yet for this name.
    /// </summary>
    public string? AlreadyTrustedText
    {
        get
        {
            string? key = RdpCertificatePromptText.AlreadyTrustedKey(Context.AlreadyTrustedCount);
            return key is null
                ? null
                : _localizer.Format(key, Context.AlreadyTrustedCount);
        }
    }

    /// <summary>Whether there is a reassurance line to show at all.</summary>
    public bool HasAlreadyTrustedText => AlreadyTrustedText is not null;

    /// <summary>Label above the thumbprint.</summary>
    public string ThumbprintLabel => _localizer[RdpCertificatePromptLocaleKeys.ThumbprintLabel];

    /// <summary>The thumbprint just observed.</summary>
    public string Thumbprint => Context.Thumbprint;

    /// <summary>Certificate subject, when the probe read one.</summary>
    public string? Subject => Context.Subject;

    /// <summary>Whether a subject is worth showing.</summary>
    public bool HasSubject => !string.IsNullOrWhiteSpace(Subject);

    /// <summary>Text of the durable-trust button.</summary>
    public string TrustButtonText => _localizer[RdpCertificatePromptLocaleKeys.Trust];

    /// <summary>Text of the this-run-only button.</summary>
    public string TrustOnceButtonText => _localizer[RdpCertificatePromptLocaleKeys.TrustOnce];

    /// <summary>Text of the refusal button.</summary>
    public string RefuseButtonText => _localizer[RdpCertificatePromptLocaleKeys.Refuse];

    /// <summary>What the user answered, or null while nobody has answered.</summary>
    /// <remarks>
    /// <para>Null is not an answer, and it is not a refusal either. It means nobody decided
    /// anything, which the connection path carries as <see cref="RdpTrustAnswer.NotAsked"/>:
    /// the question was withdrawn because another pane answered it, or the pane holding it was
    /// torn down. Reading null as a refusal is what made a pane tell its user "you did not
    /// approve the certificate this server presented" about a question they were never shown.
    /// Both still stop the connection; only one of them is true.</para>
    /// <para>There is no window to close any more, so none of the window's exits reach this
    /// type: <c>RdpTrustPromptSession</c> settles them, and never through
    /// <see cref="RefuseFromDismissal"/>.</para>
    /// <para>Non-null is a press, not an outcome. This records what the person pressed; whether
    /// it settles their connection is the session's decision, and it declines an approval
    /// pressed after the question was withdrawn. Nothing may read this property as the answer
    /// the connection was given.</para>
    /// </remarks>
    public RdpTrustAnswer? Answer { get; private set; }

    /// <summary>Raised once, when the user gives an answer.</summary>
    /// <remarks>
    /// Carries the answer rather than a dialog result. The question is no longer a window, so
    /// there is nothing whose <c>DialogResult</c> could carry it, and a boolean could not have
    /// carried three answers in the first place.
    /// </remarks>
    public event Action<RdpTrustAnswer>? Answered;

    /// <summary>Refuses on the user's behalf, for a way out that is not a button.</summary>
    /// <remarks>
    /// <para>Escape, and Escape only. It is a refusal because a person pressed a key on a
    /// question they were looking at, exactly as the title-bar cross of the window this
    /// replaces was.</para>
    /// <para><b>The pane being closed does not come through here.</b> It used to, while the
    /// question was a window and closing the pane closed the window with it.
    /// <c>RdpTrustPromptSession.Close()</c> now settles that path itself, with
    /// <see cref="RdpTrustAnswer.NotAsked"/>: a teardown is something the user did to the pane,
    /// not something they said about the certificate. Routing it back through here would put
    /// "you did not approve the certificate this server presented" in front of someone who was
    /// asked nothing - the false claim this whole change exists to remove.</para>
    /// </remarks>
    public void RefuseFromDismissal() => Record(RdpTrustAnswer.Refuse);

    [RelayCommand]
    private void Trust() => Record(RdpTrustAnswer.TrustPermanently);

    [RelayCommand]
    private void TrustOnce() => Record(RdpTrustAnswer.TrustForSession);

    [RelayCommand]
    private void Refuse() => Record(RdpTrustAnswer.Refuse);

    private void Record(RdpTrustAnswer answer)
    {
        if (Answer is not null)
        {
            // A second press, or Escape landing on top of a click. The first answer is the one
            // the user gave; repeating it would raise the event again for a question the
            // session has already settled.
            return;
        }

        Answer = answer;
        FileLogger.Info(
            $"[RdpCertPrompt] {RemoteEndpoint} thumbprint={Context.Thumbprint} answer={answer}");
        Answered?.Invoke(answer);
    }
}
