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
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Shell;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// Launch-time replay of the previous run's session snapshot.
/// </summary>
/// <remarks>
/// <para>This orchestration had no coverage while it lived inline in the shell view model. Two rules
/// carry the weight and neither was expressed anywhere: once the user has answered, deletion of the
/// snapshot is attempted exactly once, while a snapshot the user never got to answer for survives;
/// and one session that fails to reopen must not cancel the sessions queued behind it.</para>
/// <para>What these tests do not prove, because the code does not do it: that the snapshot actually
/// disappears. Every assertion below counts calls to <c>ClearAsync</c>. The real service reports a
/// delete it could not perform as an ordinary completion, and a cancellation or a process exit
/// between the replay and the delete leaves the file behind, so the same sessions can be offered
/// again - and reopened again - at the next launch. There is no exactly-once replay guarantee here
/// and nothing below should be read as one.</para>
/// </remarks>
public sealed class SessionRestoreCoordinatorTests
{
    [Fact]
    public async Task NoSnapshotFile_AsksNothingAndAttemptsNoDeletion()
    {
        RecordingDialogService dialogs = new();
        RecordingSnapshotService snapshots = new(snapshot: null);
        RecordingHost host = new();

        await CreateCoordinator(snapshots, dialogs).RestoreAsync(host, CancellationToken.None);

        Assert.Equal(0, dialogs.RestoreDialogsShown);
        Assert.Equal(0, snapshots.ClearCalls);
        Assert.Empty(host.RestoreAttempts);
    }

    [Fact]
    public async Task SnapshotWithNoSession_AsksNothingAndAttemptsNoDeletion()
    {
        RecordingDialogService dialogs = new();
        RecordingSnapshotService snapshots = new(Snapshot());
        RecordingHost host = new();

        await CreateCoordinator(snapshots, dialogs).RestoreAsync(host, CancellationToken.None);

        Assert.Equal(0, dialogs.RestoreDialogsShown);
        Assert.Equal(0, snapshots.ClearCalls);
        Assert.Empty(host.RestoreAttempts);
    }

    [Fact]
    public async Task DontRestore_AttemptsTheDeletionOnceWithoutReopeningAnything()
    {
        RecordingSnapshotService snapshots = new(Snapshot(Entry("srv-1", 0)));
        RecordingDialogService dialogs = new()
        {
            Result = new SnapshotRestoreDialogResult(SnapshotRestoreDialogAction.DontRestore, []),
        };
        RecordingHost host = new();

        await CreateCoordinator(snapshots, dialogs).RestoreAsync(host, CancellationToken.None);

        // Declining is an answer, so the deletion is attempted. Whether the file actually goes away
        // is the snapshot service's business, and it reports a failed delete as an ordinary return.
        Assert.Equal(1, snapshots.ClearCalls);
        Assert.Empty(host.RestoreAttempts);
    }

    [Fact]
    public async Task RestoreSelected_ReopensInSnapshotOrderThenAttemptsTheDeletionOnce()
    {
        RecordingSnapshotService snapshots = new(
            Snapshot(Entry("srv-a", 0), Entry("srv-b", 1), Entry("srv-c", 2)));

        // Deliberately handed back out of order: the tab sequence comes from Order, not from the
        // order the dialog happened to enumerate.
        RecordingDialogService dialogs = new()
        {
            Result = new SnapshotRestoreDialogResult(
                SnapshotRestoreDialogAction.RestoreSelected,
                [Entry("srv-c", 2), Entry("srv-a", 0), Entry("srv-b", 1)]),
        };
        RecordingHost host = new();

        await CreateCoordinator(snapshots, dialogs).RestoreAsync(host, CancellationToken.None);

        Assert.Equal(["srv-a", "srv-b", "srv-c"], host.RestoreAttempts);
        Assert.Equal(1, snapshots.ClearCalls);
    }

    [Fact]
    public async Task EveryRestoreSucceeding_ReportsNoShortfall()
    {
        RecordingSnapshotService snapshots = new(Snapshot(Entry("srv-a", 0), Entry("srv-b", 1)));
        RecordingDialogService dialogs = new()
        {
            Result = new SnapshotRestoreDialogResult(
                SnapshotRestoreDialogAction.RestoreSelected,
                [Entry("srv-a", 0), Entry("srv-b", 1)]),
        };
        RecordingHost host = new();

        await CreateCoordinator(snapshots, dialogs).RestoreAsync(host, CancellationToken.None);

        Assert.Equal(2, host.RestoreAttempts.Count);
        Assert.Empty(dialogs.Warnings);
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task AThrowingRestore_DoesNotCancelTheSessionsBehindIt()
    {
        RecordingSnapshotService snapshots = new(
            Snapshot(Entry("srv-a", 0), Entry("srv-boom", 1), Entry("srv-c", 2)));
        RecordingDialogService dialogs = new()
        {
            Result = new SnapshotRestoreDialogResult(
                SnapshotRestoreDialogAction.RestoreSelected,
                [Entry("srv-a", 0), Entry("srv-boom", 1), Entry("srv-c", 2)]),
        };
        RecordingHost host = new() { ThrowFor = "srv-boom" };

        await CreateCoordinator(snapshots, dialogs).RestoreAsync(host, CancellationToken.None);

        // The session after the failure is what matters: an unreachable server in the middle of the
        // snapshot must not silently drop the rest of the workspace.
        Assert.Equal(["srv-a", "srv-boom", "srv-c"], host.RestoreAttempts);
        Assert.Equal(1, snapshots.ClearCalls);
        Assert.Single(dialogs.Warnings);
    }

    [Fact]
    public async Task AServerThatNoLongerExists_IsReportedAsAShortfallExactlyOnce()
    {
        RecordingSnapshotService snapshots = new(Snapshot(Entry("srv-a", 0), Entry("srv-gone", 1)));
        RecordingDialogService dialogs = new()
        {
            Result = new SnapshotRestoreDialogResult(
                SnapshotRestoreDialogAction.RestoreSelected,
                [Entry("srv-a", 0), Entry("srv-gone", 1)]),
        };
        RecordingHost host = new() { UnknownServer = "srv-gone" };

        await CreateCoordinator(snapshots, dialogs).RestoreAsync(host, CancellationToken.None);

        Assert.Single(dialogs.Warnings);
        Assert.Equal(1, snapshots.ClearCalls);
    }

    [Fact]
    public async Task ADialogThatFails_LeavesTheSnapshotForTheNextLaunch()
    {
        RecordingSnapshotService snapshots = new(Snapshot(Entry("srv-1", 0)));
        RecordingDialogService dialogs = new() { ThrowOnRestoreDialog = true };
        RecordingHost host = new();

        await CreateCoordinator(snapshots, dialogs).RestoreAsync(host, CancellationToken.None);

        // Nobody declined these sessions, so deleting the snapshot here would discard them silently.
        Assert.Equal(0, snapshots.ClearCalls);
        Assert.Single(dialogs.Errors);
        Assert.Empty(host.RestoreAttempts);
    }

    [Fact]
    public async Task ADismissedDialog_LeavesTheSnapshotForTheNextLaunch()
    {
        RecordingSnapshotService snapshots = new(Snapshot(Entry("srv-1", 0)));
        RecordingDialogService dialogs = new() { Result = null };
        RecordingHost host = new();

        await CreateCoordinator(snapshots, dialogs).RestoreAsync(host, CancellationToken.None);

        Assert.Equal(0, snapshots.ClearCalls);
        Assert.Empty(host.RestoreAttempts);
    }

    [Fact]
    public async Task ARestoreThatObservesCancellation_PropagatesAndStopsTheReplay()
    {
        RecordingSnapshotService snapshots = new(
            Snapshot(Entry("srv-a", 0), Entry("srv-b", 1), Entry("srv-c", 2)));
        RecordingDialogService dialogs = new()
        {
            Result = new SnapshotRestoreDialogResult(
                SnapshotRestoreDialogAction.RestoreSelected,
                [Entry("srv-a", 0), Entry("srv-b", 1), Entry("srv-c", 2)]),
        };

        // Cancellation raised by the reopen itself, with no cancellation on the token this call was
        // given: the loop-top check cannot see it, so only the catch around the reopen decides
        // whether the replay stops or treats the cancellation as one server failing.
        RecordingHost host = new() { CancelFor = "srv-a" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateCoordinator(snapshots, dialogs).RestoreAsync(host, CancellationToken.None));

        Assert.Equal(["srv-a"], host.RestoreAttempts);
        Assert.Equal(0, snapshots.ClearCalls);
        Assert.Empty(dialogs.Warnings);
    }

    [Fact]
    public async Task CancellationDuringReplay_StopsBetweenSessionsAndLeavesTheSnapshot()
    {
        RecordingSnapshotService snapshots = new(Snapshot(Entry("srv-a", 0), Entry("srv-b", 1)));
        RecordingDialogService dialogs = new()
        {
            Result = new SnapshotRestoreDialogResult(
                SnapshotRestoreDialogAction.RestoreSelected,
                [Entry("srv-a", 0), Entry("srv-b", 1)]),
        };

        using CancellationTokenSource cts = new();
        RecordingHost host = new() { OnRestore = () => cts.Cancel() };
        ISessionRestoreCoordinator coordinator = CreateCoordinator(snapshots, dialogs);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.RestoreAsync(host, cts.Token));

        // A partial replay must not even attempt the deletion: the sessions never reopened are
        // still owed.
        Assert.Equal(["srv-a"], host.RestoreAttempts);
        Assert.Equal(0, snapshots.ClearCalls);
    }

    [Fact]
    public async Task ANullHost_IsRejected()
    {
        ISessionRestoreCoordinator coordinator = CreateCoordinator(
            new RecordingSnapshotService(snapshot: null),
            new RecordingDialogService());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => coordinator.RestoreAsync(null!, CancellationToken.None));
    }

    private static ISessionRestoreCoordinator CreateCoordinator(
        RecordingSnapshotService snapshots,
        RecordingDialogService dialogs)
    {
        LocalizationManager localizer = new();
        localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en")
            .GetAwaiter()
            .GetResult();
        return new SessionRestoreCoordinator(snapshots, localizer, dialogs);
    }

    private static SessionSnapshotFile Snapshot(params SessionSnapshotEntry[] entries)
    {
        return new SessionSnapshotFile
        {
            SavedAtUtc = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc),
            Sessions = [.. entries],
        };
    }

    private static SessionSnapshotEntry Entry(string serverId, int order)
    {
        return new SessionSnapshotEntry
        {
            ServerId = serverId,
            ConnectionType = "SSH",
            Order = order,
        };
    }

    private sealed class RecordingHost : ISessionRestoreHost
    {
        public List<string> RestoreAttempts { get; } = [];

        public string? ThrowFor { get; init; }

        public string? UnknownServer { get; init; }

        public string? CancelFor { get; init; }

        public Action? OnRestore { get; init; }

        public IEnumerable<ServerItemViewModel> RestorableServers => [];

        public Task<bool> RestoreServerAsync(string originalServerId, CancellationToken cancellationToken)
        {
            RestoreAttempts.Add(originalServerId);
            OnRestore?.Invoke();

            if (string.Equals(originalServerId, ThrowFor, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Restore refused for {originalServerId}.");
            }

            if (string.Equals(originalServerId, CancelFor, StringComparison.Ordinal))
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult(
                !string.Equals(originalServerId, UnknownServer, StringComparison.Ordinal));
        }
    }

    private sealed class RecordingSnapshotService(SessionSnapshotFile? snapshot) : ISessionSnapshotService
    {
        public int ClearCalls { get; private set; }

        public string SnapshotPath => "unused";

        public Task SaveAsync(
            IReadOnlyList<SessionSnapshotEntry> sessions,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SessionSnapshotFile?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            ClearCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public SnapshotRestoreDialogResult? Result { get; init; }

        public bool ThrowOnRestoreDialog { get; init; }

        public int RestoreDialogsShown { get; private set; }

        public List<string> Errors { get; } = [];

        public List<string> Warnings { get; } = [];

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(
            SnapshotRestoreDialogViewModel viewModel)
        {
            RestoreDialogsShown++;
            if (ThrowOnRestoreDialog)
            {
                throw new InvalidOperationException("Restore dialog could not be shown.");
            }

            return Task.FromResult(Result);
        }

        public void ShowError(string title, string message) => Errors.Add(message);

        public void ShowWarning(string title, string message) => Warnings.Add(message);

        public void ShowInfo(string title, string message)
        {
        }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
            => Task.FromResult(false);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<int?> ShowBulkEditPortAsync(
            int count,
            int? initialPort,
            CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
            => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
            => Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(
            ScheduledTaskDialogViewModel? editVm = null)
            => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
            => Task.FromResult<PinSetupResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
            => Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
            => Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
            => Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
            => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
            => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);
    }
}
