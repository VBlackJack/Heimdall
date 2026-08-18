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
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels.Shell;

/// <summary>
/// Default <see cref="ISessionRestoreCoordinator"/>: dialog, replay, then one attempt to delete the
/// snapshot.
/// </summary>
/// <remarks>
/// Stateless between calls, and the inventory arrives as an argument, so a single instance serves
/// every shell without capturing one.
/// </remarks>
public sealed class SessionRestoreCoordinator : ISessionRestoreCoordinator
{
    private readonly ISessionSnapshotService _snapshotService;
    private readonly LocalizationManager _localizer;
    private readonly IDialogService _dialogService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionRestoreCoordinator"/> class.
    /// </summary>
    public SessionRestoreCoordinator(
        ISessionSnapshotService snapshotService,
        LocalizationManager localizer,
        IDialogService dialogService)
    {
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    /// <inheritdoc />
    public async Task RestoreAsync(ISessionRestoreHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        SessionSnapshotFile? snapshot = await _snapshotService.LoadAsync(cancellationToken);
        if (snapshot?.Sessions.Count is not > 0)
        {
            return;
        }

        SnapshotRestoreDialogResult? dialogResult;
        try
        {
            SnapshotRestoreDialogViewModel dialogVm = new(
                _localizer,
                snapshot,
                host.RestorableServers);
            dialogResult = await _dialogService.ShowSnapshotRestoreDialogAsync(dialogVm);
        }
        catch (Exception ex)
        {
            // The snapshot is deliberately left in place: the user never saw the choice, so
            // deleting it here would silently discard sessions nobody declined.
            FileLogger.Error("Snapshot restore dialog failed.", ex);
            _dialogService.ShowError(
                _localizer["DialogSnapshotRestoreTitle"],
                _localizer.Format("ErrorSnapshotRestoreFailed", ex.Message));
            return;
        }

        if (dialogResult is null)
        {
            return;
        }

        if (dialogResult.Action == SnapshotRestoreDialogAction.DontRestore)
        {
            await _snapshotService.ClearAsync(cancellationToken);
            return;
        }

        int restoredCount = 0;
        List<SessionSnapshotEntry> selectedSessions = dialogResult.Sessions
            .OrderBy(session => session.Order)
            .ToList();

        foreach (SessionSnapshotEntry session in selectedSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await host.RestoreServerAsync(session.ServerId, cancellationToken))
                {
                    restoredCount++;
                }
            }
            catch (OperationCanceledException)
            {
                // Not a session failing. The sessions queued behind this one are still owed, so the
                // replay stops here rather than continuing, no deletion is attempted, and nothing is
                // reported as a partial restore. Caught ahead of the general handler below, which
                // would otherwise absorb it - and a cancellation raised inside the reopen is not
                // visible to the loop-top check at all.
                throw;
            }
            catch (Exception ex)
            {
                // Swallowed on purpose: the sessions queued behind this one are independent, and
                // the shortfall is reported once below rather than per failure.
                FileLogger.Error($"Session snapshot restore failed for {session.ServerId}.", ex);
            }
        }

        await _snapshotService.ClearAsync(cancellationToken);

        if (restoredCount < selectedSessions.Count)
        {
            _dialogService.ShowWarning(
                _localizer["DialogSnapshotRestoreTitle"],
                _localizer.Format(
                    "WarningSnapshotRestorePartial",
                    restoredCount,
                    selectedSessions.Count));
        }
    }
}
