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

using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Services.CloseGuard;

/// <summary>
/// The synchronous primitive that closes one pane, normally <see cref="ISplitService.ClosePane"/>.
/// </summary>
internal delegate PaneCloseResult ClosePanePrimitive(
    SessionTabViewModel session,
    string paneId,
    CloseRequest request);

/// <summary>
/// Runs the close protocol for one pane: poll, resolve, retry once, report.
/// </summary>
/// <remarks>
/// Lifted out of the shell view model unchanged. The protocol is the same poll / resolve /
/// retry-once cycle the session paths use, so all close paths share one contract, and it lives
/// here beside the arbiter rather than beside the tab strip.
/// <para>
/// Asynchronous because a guard may need to ask the user, and that answer has to be awaited
/// outside the synchronous close primitive. <see cref="ISplitService.ClosePane"/> stays
/// synchronous: it is the primitive, not the conversation.
/// </para>
/// </remarks>
internal sealed class PaneCloseCoordinator
{
    private readonly ClosePanePrimitive _closePane;
    private readonly IPaneCloseArbiter _arbiter;
    private readonly IDialogService _dialogService;
    private readonly LocalizationManager _localizer;

    /// <param name="closePane">
    /// The synchronous close primitive, normally <see cref="ISplitService.ClosePane"/>. A narrow
    /// seam on purpose: what this type orchestrates is one method, so depending on the whole split
    /// service would force every test to stand up a service it never exercises.
    /// </param>
    /// <param name="arbiter">
    /// The arbiter every close path shares. A clearance obtained in one gesture is honoured
    /// wherever that gesture continues, so this must be the same instance, never a fresh one.
    /// </param>
    internal PaneCloseCoordinator(
        ClosePanePrimitive closePane,
        IPaneCloseArbiter arbiter,
        IDialogService dialogService,
        LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(closePane);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(localizer);

        _closePane = closePane;
        _arbiter = arbiter;
        _dialogService = dialogService;
        _localizer = localizer;
    }

    /// <summary>
    /// Closes one pane, honouring its close guard.
    /// </summary>
    internal async Task<PaneCloseResult> ClosePaneAsync(
        SessionTabViewModel session,
        string paneId,
        DisconnectReason reason = DisconnectReason.UserAction,
        CloseIntent intent = CloseIntent.Interactive)
    {
        CloseRequest request = intent == CloseIntent.Silent
            ? CloseRequest.Silent(reason)
            : CloseRequest.Interactive(reason);
        try
        {
            return await CloseCoreAsync(session, paneId, request);
        }
        finally
        {
            // In a finally: a request that threw still holds grants, and leaving them behind would
            // let a later gesture inherit a clearance nobody gave it.
            _arbiter.Release(request);
        }
    }

    private async Task<PaneCloseResult> CloseCoreAsync(
        SessionTabViewModel session,
        string paneId,
        CloseRequest request)
    {
        PaneCloseResult result = _closePane(session, paneId, request);
        if (result.Outcome != PaneCloseOutcome.Deferred)
        {
            ReportIfBlocked(session, result);
            return result;
        }

        object?[] hosts = [SplitTreeHelper.FindPane(session.RootContent, paneId)?.HostControl];
        if (!await _arbiter.ResolveAsync(request, hosts))
        {
            // A refusal is final. Nothing is removed, and the pane stays exactly as it was.
            PaneCloseResult refused = PaneCloseResult.Blocked(
                result.ReasonKey ?? CloseGuardLocaleKeys.BlockedGeneric);
            ReportIfBlocked(session, refused);
            return refused;
        }

        // Exactly one retry. If the guard still defers after its single resolution, the protocol
        // blocks rather than looping: what a second deferral means is not knowable from here - the
        // state or the epoch may have moved while the prompt was open - so no further conclusion is
        // drawn beyond the observable one.
        result = _closePane(session, paneId, request);
        if (result.Outcome == PaneCloseOutcome.Deferred)
        {
            result = PaneCloseResult.Blocked(result.ReasonKey ?? CloseGuardLocaleKeys.BlockedGeneric);
        }

        ReportIfBlocked(session, result);
        return result;
    }

    private void ReportIfBlocked(SessionTabViewModel session, PaneCloseResult result)
    {
        if (result.Outcome != PaneCloseOutcome.Blocked || result.ReasonKey is null)
        {
            return;
        }

        _dialogService.ShowInfo(
            _localizer[CloseGuardLocaleKeys.BlockedTitle],
            _localizer.Format(result.ReasonKey, session.Title));
    }
}
