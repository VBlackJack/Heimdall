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
using Heimdall.App.Services.CloseGuard;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// The pane close protocol: poll, resolve, retry exactly once, report.
/// </summary>
/// <remarks>
/// Every clause here was previously reachable only by constructing the whole shell, so the
/// protocol that decides whether a user's work is torn down had no oracle of its own. The seam is
/// the close primitive alone - one delegate - because that is all the protocol orchestrates.
/// </remarks>
public sealed class PaneCloseCoordinatorTests
{
    private const string PaneId = "secondary";
    private const string OtherPaneId = "primary";

    [Fact]
    public async Task OneRequestIdentityIsThreadedThroughEveryStep()
    {
        Fixture fixture = await Fixture.CreateAsync(
            PaneCloseResult.Deferred(CloseGuardLocaleKeys.BlockedTool),
            PaneCloseResult.Closed);

        await fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId);

        // One object, carried through. The arbiter keys its grants by request, so a freshly minted
        // CloseRequest for the retry would silently discard the consent the user just gave. Object
        // identity is the property; RequestId is only secondary evidence of it.
        CloseRequest initial = fixture.Primitive.Calls[0].Request;
        Assert.Same(initial, Assert.Single(fixture.Arbiter.Resolved));
        Assert.Same(initial, fixture.Primitive.Calls[1].Request);
        Assert.Same(initial, Assert.Single(fixture.Arbiter.Released));

        Assert.Equal(initial.RequestId, fixture.Primitive.Calls[1].Request.RequestId);
    }

    [Fact]
    public async Task TheGrantIsReleasedOnceWhenTheCloseSucceeds()
    {
        Fixture fixture = await Fixture.CreateAsync(PaneCloseResult.Closed);

        await fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId);

        Assert.Single(fixture.Arbiter.Released);
    }

    [Fact]
    public async Task TheGrantIsReleasedOnceWhenTheGuardRefuses()
    {
        Fixture fixture = await Fixture.CreateAsync(
            grant: false,
            PaneCloseResult.Deferred(CloseGuardLocaleKeys.BlockedTool));

        await fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId);

        Assert.Single(fixture.Arbiter.Released);
    }

    [Fact]
    public async Task TheGrantIsReleasedEvenWhenThePrimitiveThrows()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Primitive.Throw = new InvalidOperationException("primitive failed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId));

        // The finally is the whole point: a request that threw still holds grants, and leaving them
        // behind lets a later gesture inherit a clearance nobody gave it.
        Assert.Single(fixture.Arbiter.Released);
    }

    [Fact]
    public async Task AnImmediateCloseNeitherResolvesNorRetries()
    {
        Fixture fixture = await Fixture.CreateAsync(PaneCloseResult.Closed);

        PaneCloseResult result = await fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId);

        Assert.Equal(PaneCloseOutcome.Closed, result.Outcome);
        Assert.Single(fixture.Primitive.Calls);
        Assert.Empty(fixture.Arbiter.Resolved);
        Assert.Empty(fixture.Dialog.Messages);
    }

    [Fact]
    public async Task AnImmediateBlockReportsOnceAndNeverResolves()
    {
        Fixture fixture = await Fixture.CreateAsync(
            PaneCloseResult.Blocked(CloseGuardLocaleKeys.BlockedTool));

        PaneCloseResult result = await fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId);

        Assert.Equal(PaneCloseOutcome.Blocked, result.Outcome);
        Assert.Empty(fixture.Arbiter.Resolved);
        Assert.Single(fixture.Primitive.Calls);

        (string title, string message) = Assert.Single(fixture.Dialog.Messages);
        Assert.Equal(fixture.Localizer[CloseGuardLocaleKeys.BlockedTitle], title);
        Assert.Equal(
            fixture.Localizer.Format(CloseGuardLocaleKeys.BlockedTool, fixture.Session.Title),
            message);
    }

    [Fact]
    public async Task ARefusalBlocksWithoutRetryingAndWithoutRemovingAnything()
    {
        Fixture fixture = await Fixture.CreateAsync(
            grant: false,
            PaneCloseResult.Deferred(CloseGuardLocaleKeys.BlockedTool));

        PaneCloseResult result = await fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId);

        // A refusal is final. One primitive call - the initial poll - and nothing torn down after.
        Assert.Equal(PaneCloseOutcome.Blocked, result.Outcome);
        Assert.Equal(CloseGuardLocaleKeys.BlockedTool, result.ReasonKey);
        Assert.Single(fixture.Primitive.Calls);
        Assert.Single(fixture.Arbiter.Resolved);
        Assert.Single(fixture.Dialog.Messages);
    }

    [Fact]
    public async Task AGrantRetriesExactlyOnce()
    {
        Fixture fixture = await Fixture.CreateAsync(
            PaneCloseResult.Deferred(CloseGuardLocaleKeys.BlockedTool),
            PaneCloseResult.Closed);

        PaneCloseResult result = await fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId);

        // Two primitive calls, and the second IS the retry. In production that retry re-enters the
        // primitive's own Poll, which is exactly what detects work started while the prompt was
        // open - see PaneCloseArbiterTests.ResolveAsync_NewWorkStartedDURINGThePrompt_RefusesOnTheRetry,
        // which owns that proof. What this file owns is that one CloseRequest survives the round
        // trip, so the grant stamped against its epoch is still the one being honoured.
        Assert.Equal(PaneCloseOutcome.Closed, result.Outcome);
        Assert.Equal(2, fixture.Primitive.Calls.Count);
        Assert.Same(fixture.Primitive.Calls[0].Request, fixture.Primitive.Calls[1].Request);
        Assert.Single(fixture.Arbiter.Resolved);
        Assert.Empty(fixture.Dialog.Messages);
    }

    [Fact]
    public async Task ASecondDeferralBecomesBlockedWithoutASecondResolution()
    {
        Fixture fixture = await Fixture.CreateAsync(
            PaneCloseResult.Deferred(CloseGuardLocaleKeys.BlockedTool),
            PaneCloseResult.Deferred(CloseGuardLocaleKeys.BlockedTool));

        PaneCloseResult result = await fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId);

        // Blocks rather than looping. What a second deferral means is not decided here: the state
        // or the epoch may have moved while the prompt was open.
        Assert.Equal(PaneCloseOutcome.Blocked, result.Outcome);
        Assert.Equal(2, fixture.Primitive.Calls.Count);
        Assert.Single(fixture.Arbiter.Resolved);
        Assert.Single(fixture.Dialog.Messages);
    }

    [Fact]
    public async Task TheHostHandedToTheArbiterBelongsToTheRequestedPane()
    {
        Fixture fixture = await Fixture.CreateAsync(
            PaneCloseResult.Deferred(CloseGuardLocaleKeys.BlockedTool),
            PaneCloseResult.Closed);

        await fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId);

        // Resolving against the wrong pane's host asks the wrong guard, and a consent given for one
        // pane would tear down another.
        object? host = Assert.Single(Assert.Single(fixture.Arbiter.ResolvedHosts));
        Assert.Same(Fixture.SecondaryHost, host);
        Assert.NotSame(Fixture.PrimaryHost, host);
    }

    [Fact]
    public async Task ASilentRequestCarriesTheSilentIntent()
    {
        Fixture fixture = await Fixture.CreateAsync(PaneCloseResult.Closed);

        await fixture.Coordinator.ClosePaneAsync(
            fixture.Session,
            PaneId,
            DisconnectReason.UserAction,
            CloseIntent.Silent);

        // Which guards see the request is the arbiter's business; what this owns is passing the
        // intent through unaltered.
        Assert.Equal(CloseIntent.Silent, Assert.Single(fixture.Primitive.Calls).Request.Intent);
    }

    [Fact]
    public async Task AnInteractiveRequestIsTheDefault()
    {
        Fixture fixture = await Fixture.CreateAsync(PaneCloseResult.Closed);

        await fixture.Coordinator.ClosePaneAsync(fixture.Session, PaneId);

        Assert.Equal(CloseIntent.Interactive, Assert.Single(fixture.Primitive.Calls).Request.Intent);
    }

    private sealed class Fixture
    {
        internal static readonly object PrimaryHost = new();
        internal static readonly object SecondaryHost = new();

        private Fixture(
            PaneCloseCoordinator coordinator,
            SessionTabViewModel session,
            RecordingPrimitive primitive,
            RecordingArbiter arbiter,
            RecordingDialogService dialog,
            LocalizationManager localizer)
        {
            Coordinator = coordinator;
            Session = session;
            Primitive = primitive;
            Arbiter = arbiter;
            Dialog = dialog;
            Localizer = localizer;
        }

        internal PaneCloseCoordinator Coordinator { get; }

        internal SessionTabViewModel Session { get; }

        internal RecordingPrimitive Primitive { get; }

        internal RecordingArbiter Arbiter { get; }

        internal RecordingDialogService Dialog { get; }

        internal LocalizationManager Localizer { get; }

        internal static Task<Fixture> CreateAsync(params PaneCloseResult[] results)
            => CreateAsync(grant: true, results);

        internal static async Task<Fixture> CreateAsync(bool grant, params PaneCloseResult[] results)
        {
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            RecordingPrimitive primitive = new(results);
            RecordingArbiter arbiter = new() { Grant = grant };
            RecordingDialogService dialog = new();

            SessionTabViewModel session = new()
            {
                Title = "session",
                RootContent = new SplitContainerModel
                {
                    First = new SessionPaneModel { PaneId = OtherPaneId, HostControl = PrimaryHost },
                    Second = new SessionPaneModel { PaneId = PaneId, HostControl = SecondaryHost },
                },
            };

            PaneCloseCoordinator coordinator = new(primitive.Close, arbiter, dialog, localizer);
            return new Fixture(coordinator, session, primitive, arbiter, dialog, localizer);
        }
    }

    private sealed class RecordingPrimitive(PaneCloseResult[] results)
    {
        private readonly Queue<PaneCloseResult> _results = new(results);

        internal List<(SessionTabViewModel Session, string PaneId, CloseRequest Request)> Calls { get; } = [];

        internal Exception? Throw { get; set; }

        internal PaneCloseResult Close(SessionTabViewModel session, string paneId, CloseRequest request)
        {
            Calls.Add((session, paneId, request));
            if (Throw is not null)
            {
                throw Throw;
            }

            return _results.Count > 0 ? _results.Dequeue() : PaneCloseResult.Closed;
        }
    }

    private sealed class RecordingArbiter : IPaneCloseArbiter
    {
        internal bool Grant { get; init; } = true;

        internal List<CloseRequest> Resolved { get; } = [];

        internal List<IReadOnlyList<object?>> ResolvedHosts { get; } = [];

        internal List<CloseRequest> Released { get; } = [];

        public CloseDecision Poll(CloseRequest request, IReadOnlyList<object?> hosts)
        {
            return CloseDecision.Allow(0);
        }

        public Task<bool> ResolveAsync(CloseRequest request, IReadOnlyList<object?> hosts)
        {
            Resolved.Add(request);
            ResolvedHosts.Add(hosts);
            return Task.FromResult(Grant);
        }

        public void Release(CloseRequest request) => Released.Add(request);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        internal List<(string Title, string Message)> Messages { get; } = [];

        public void ShowInfo(string title, string message) => Messages.Add((title, message));

        public void ShowError(string title, string message) => Messages.Add((title, message));

        public void ShowWarning(string title, string message) => Messages.Add((title, message));

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
            => Task.FromResult(true);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => Task.FromResult(defaultValue);

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default)
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

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(
            SnapshotRestoreDialogViewModel viewModel)
            => Task.FromResult<SnapshotRestoreDialogResult?>(null);

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

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
            => Task.FromResult(initialPort);

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken)
            => Task.FromResult(initialUsername);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }
}
