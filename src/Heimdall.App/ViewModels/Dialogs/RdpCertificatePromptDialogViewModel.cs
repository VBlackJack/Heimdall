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
/// Everything decidable lives here rather than in the window, so it can be tested without
/// constructing one: a WPF <c>Window</c> built in a test seals application-level styles
/// onto the shared dispatcher and takes unrelated tests down with it.
/// </remarks>
public partial class RdpCertificatePromptDialogViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;

    /// <summary>Creates the ViewModel for one certificate question.</summary>
    /// <param name="localizer">Supplies the wording.</param>
    /// <param name="context">What the user needs in order to answer.</param>
    public RdpCertificatePromptDialogViewModel(
        LocalizationManager localizer,
        RdpCertificatePromptContext context)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(context);

        _localizer = localizer;
        Context = context;
    }

    /// <summary>What was observed, and what the profile already trusts.</summary>
    public RdpCertificatePromptContext Context { get; }

    /// <summary>Title of the question.</summary>
    public string Title => _localizer[RdpCertificatePromptLocaleKeys.Title];

    /// <summary>The body: this profile has never approved this certificate.</summary>
    public string Message => _localizer.Format(
        RdpCertificatePromptLocaleKeys.Message,
        Context.ProfileName,
        Context.Host);

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

    /// <summary>What the user answered, or null while the question is still open.</summary>
    /// <remarks>
    /// Null is not an answer. A caller that finds it null after the window closed - the
    /// title bar cross, Escape, a dispatcher shutdown - must read that as a refusal, since
    /// the alternative is opening a session nobody approved.
    /// </remarks>
    public RdpTrustAnswer? Answer { get; private set; }

    /// <summary>Raised when the window should close, carrying its dialog result.</summary>
    public event Action<bool>? CloseRequested;

    [RelayCommand]
    private void Trust() => Answered(RdpTrustAnswer.TrustPermanently, closed: true);

    [RelayCommand]
    private void TrustOnce() => Answered(RdpTrustAnswer.TrustForSession, closed: true);

    [RelayCommand]
    private void Refuse() => Answered(RdpTrustAnswer.Refuse, closed: false);

    private void Answered(RdpTrustAnswer answer, bool closed)
    {
        Answer = answer;
        FileLogger.Info(
            $"[RdpCertPrompt] {Context.Host} thumbprint={Context.Thumbprint} answer={answer}");
        CloseRequested?.Invoke(closed);
    }
}
