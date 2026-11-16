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
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The pane's side of the root question: it puts the question to the user, and every escalation
/// site asks <see cref="DecideSudoAsync"/> or <see cref="RequireSudoAsync"/> before it calls sudo.
/// </summary>
public sealed partial class EmbeddedSftpViewModel
{
    private const string SudoQuestionSeverity = "danger";

    private readonly SftpSudoFallbackGate _sudoGate;

    /// <summary>The effective mode, read live. Automatic while the pane has no wired source.</summary>
    internal SftpSudoFallbackMode SudoFallbackMode => _sudoGate.Mode;

    /// <summary>Whether the pane's sudo button may be used: not while the setting is Never.</summary>
    public bool IsSudoModeAvailable => _sudoGate.Mode != SftpSudoFallbackMode.Never;

    /// <summary>The tooltip of the pane's sudo button, which says why it is off while the setting is Never.</summary>
    public string SudoModeTooltip => L10n(
        IsSudoModeAvailable ? SftpSudoConsentLocaleKeys.ModeTooltip : SftpSudoConsentLocaleKeys.ModeNeverTooltip);

    /// <summary>
    /// Whether the pane lists folders as root: the button is on and the setting still allows root.
    /// A button left on while the setting moved to Never does nothing.
    /// </summary>
    internal bool IsListingAsRoot
        => SudoMode && _sshParams is not null && _sudoGate.Mode != SftpSudoFallbackMode.Never;

    /// <summary>Replaces the live source of the mode, as <c>Initialize</c> does; null leaves the pane Automatic.</summary>
    internal void SetSudoFallbackModeProvider(Func<SftpSudoFallbackMode>? provider)
    {
        _sudoGate.SetModeProvider(provider);
        RefreshSudoModeAvailability();
    }

    /// <summary>Tells the toolbar to read the setting again; the pane reads it live at every act.</summary>
    internal void RefreshSudoModeAvailability()
    {
        if (_disposed)
        {
            return;
        }

        _ = RunOnUiAsync(() =>
        {
            // A button left on while the setting is Never is switched off here, so the pane
            // cannot resume listing as root on its own if the setting later moves back.
            if (SudoMode && !IsSudoModeAvailable)
            {
                SudoMode = false;
                UpdateStatus(L10n("SftpSudoModeDisabled"));
            }

            OnPropertyChanged(nameof(IsSudoModeAvailable));
            OnPropertyChanged(nameof(SudoModeTooltip));
        });
    }

    /// <summary>
    /// Decides whether a refused act may call sudo, asking the user when the setting says so, and
    /// re-checks the caller's token and the pane's life after the answer: a question can stay open
    /// while a job is cancelled or the pane is closed.
    /// </summary>
    internal async Task<SftpSudoDecision> DecideSudoAsync(
        SftpRootActKind act,
        string path,
        SftpSudoConsentScope scope,
        CancellationToken ct = default,
        SftpSudoInitiator initiator = SftpSudoInitiator.User,
        bool paneArmed = false)
    {
        ArgumentNullException.ThrowIfNull(scope);

        SftpSudoDecision decision = await _sudoGate
            .DecideAsync(new SftpSudoRequest(act, path, initiator, paneArmed), scope, ct)
            .ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        if (_disposed)
        {
            throw new OperationCanceledException();
        }

        return decision;
    }

    /// <summary>
    /// Returns when the refused act may call sudo, and throws when it may not: the refusal comes
    /// back wrapped with the reason root was not run, or as the stop of a batch that proved sudo
    /// cannot succeed.
    /// </summary>
    internal async Task RequireSudoAsync(
        SftpRootActKind act,
        string path,
        SftpSudoConsentScope scope,
        Exception refusal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        SftpSudoDecision decision = await DecideSudoAsync(act, path, scope, ct).ConfigureAwait(false);
        if (decision.IsAllowed())
        {
            return;
        }

        if (decision == SftpSudoDecision.BatchStopped)
        {
            throw new SudoBatchStoppedException(scope.Guard.StoppingFailure!);
        }

        Core.Logging.FileLogger.Info($"EmbeddedSFTP root not run for a refused act ({act}): {decision}.");
        throw new SftpSudoNotRunException(decision, refusal);
    }

    /// <summary>The refusal and the line that says why root was not run, in one sentence.</summary>
    private string DescribeSudoNotRun(SftpSudoNotRunException notRun, string refusalText)
    {
        string? noteKey = SftpSudoConsentLocaleKeys.NoteFor(notRun.Decision);
        return noteKey is null
            ? refusalText
            : LF(SftpSudoConsentLocaleKeys.RefusalWithNote, refusalText, L10n(noteKey));
    }

    // Puts the question on the UI thread, like the delete question always was: the loop that asks
    // runs off it, and a dialog service that answers synchronously must not be awaited where the
    // refusal was caught. With nothing to ask through, the answer is no.
    private async Task<bool> AskSudoQuestionAsync(SftpSudoQuestion question)
    {
        IDialogService? dialogService = _dialogService;
        if (dialogService is null)
        {
            return false;
        }

        SftpSudoQuestionKeys keys = SftpSudoConsentLocaleKeys.For(question.Scope.QuestionAct);
        string title = L10n(keys.Title);
        string accept = L10n(keys.Accept);
        string decline = L10n(keys.Decline);
        string visiblePath = ExecutionTrustDescriber.Visible(question.Path);
        string message = question.Scope.Kind == SftpSudoScopeKind.Job
            ? LF(keys.Message, visiblePath, ExecutionTrustDescriber.Visible(question.Scope.JobName ?? string.Empty))
            : LF(keys.Message, visiblePath);

        bool granted = false;
        await _uiDispatcher.InvokeAsync(async () =>
        {
            granted = await dialogService.ShowConfirmAsync(
                title,
                message,
                SudoQuestionSeverity,
                accept,
                decline).ConfigureAwait(true);
        }).ConfigureAwait(false);

        return granted;
    }
}
