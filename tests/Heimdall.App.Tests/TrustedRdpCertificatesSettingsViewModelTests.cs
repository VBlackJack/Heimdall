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
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// The read-and-revoke screen for durable RDP certificate trust.
/// </summary>
/// <remarks>
/// Every test here answers one question the screen exists to answer, and the two that matter
/// most are the ones a screen can pass while being useless: a removal that is not persisted
/// forgets only until the next launch, and a confirmation that throws must not be read as a
/// yes. The first is asserted against the real persistence path, not a recording double.
/// </remarks>
public sealed class TrustedRdpCertificatesSettingsViewModelTests
{
    private static readonly DateTimeOffset Stamp =
        new(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The empty state, asserted with the control that makes it mean something.
    /// </summary>
    /// <remarks>
    /// A screen that lists nothing at all satisfies the first three assertions forever, so the
    /// second half - one certificate, and the empty state is gone - is what makes this a
    /// measurement rather than a description of a stub.
    /// </remarks>
    [Fact]
    public async Task EmptyStore_ShowsEmptyState_AndAStoredCertificateClearsIt()
    {
        var fixture = await VmFixture.CreateAsync();

        await fixture.ViewModel.RefreshAsync();

        Assert.Empty(fixture.ViewModel.Rows);
        Assert.False(fixture.ViewModel.HasRows);
        Assert.True(fixture.ViewModel.IsEmptyStateVisible);

        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        await fixture.ViewModel.RefreshAsync();

        Assert.True(fixture.ViewModel.HasRows);
        Assert.False(fixture.ViewModel.IsEmptyStateVisible);
    }

    [Fact]
    public async Task Rows_AreBuiltFromTheStore_OneRowPerCertificate()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Profiles.Add(Profile("srv-1", "Domain controller A"));
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp)
        {
            Subject = "CN=dc-a.corp.example",
            Issuer = "CN=corp-ca"
        });
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:02", Stamp.AddDays(1)));

        await fixture.ViewModel.RefreshAsync();

        Assert.Equal(2, fixture.ViewModel.Rows.Count);
        Assert.False(fixture.ViewModel.IsEmptyStateVisible);

        var row = fixture.ViewModel.Rows.Single(r => r.Thumbprint == "SHA256:AA:BB:01");
        Assert.Equal("srv-1", row.ProfileId);
        Assert.Equal("Domain controller A", row.ProfileDisplay);
        Assert.Equal("CN=dc-a.corp.example", row.SubjectDisplay);
        Assert.Equal("CN=corp-ca", row.IssuerDisplay);
        Assert.Equal(Stamp, row.FirstTrusted);
        Assert.False(row.IsProfileMissing);
    }

    [Fact]
    public async Task Row_WhoseProfileNoLongerExists_FallsBackToTheProfileIdAndIsMarked()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Profiles.Add(Profile("srv-live", "Still here"));
        fixture.Store.Trust("srv-live", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        fixture.Store.Trust("srv-deleted", new RdpCertificateEntry("SHA256:AA:BB:02", Stamp));

        await fixture.ViewModel.RefreshAsync();

        var orphan = fixture.ViewModel.Rows.Single(r => r.ProfileId == "srv-deleted");

        // The identifier is all that is left of the profile, so it is what the user is shown -
        // a blank cell would hide exactly the decision that most needs cleaning up.
        Assert.Contains("srv-deleted", orphan.ProfileDisplay, StringComparison.Ordinal);
        Assert.True(orphan.IsProfileMissing);

        var live = fixture.ViewModel.Rows.Single(r => r.ProfileId == "srv-live");
        Assert.Equal("Still here", live.ProfileDisplay);
        Assert.False(live.IsProfileMissing);
    }

    [Fact]
    public async Task Row_WithoutSubjectOrIssuer_ShowsTheUnknownLabelRatherThanBlank()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));

        await fixture.ViewModel.RefreshAsync();

        var row = Assert.Single(fixture.ViewModel.Rows);
        Assert.False(string.IsNullOrWhiteSpace(row.SubjectDisplay));
        Assert.False(string.IsNullOrWhiteSpace(row.IssuerDisplay));
    }

    /// <summary>
    /// The question comes first, and the store is untouched while it is on screen.
    /// </summary>
    /// <remarks>
    /// Counting the confirmations proves only that one was shown, which a screen that removed
    /// first and asked afterwards would also satisfy. The ordering is read inside the dialog
    /// call itself, where the entry must still be there.
    /// </remarks>
    [Fact]
    public async Task Forget_AsksForConfirmationBeforeTouchingTheStore()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        await fixture.ViewModel.RefreshAsync();
        fixture.Dialog.ConfirmResult = true;

        int approvedWhileAsking = -1;
        fixture.Dialog.OnConfirm = () => approvedWhileAsking = fixture.Store.GetApproved("srv-1").Count;

        await fixture.ViewModel.ForgetCommand.ExecuteAsync(fixture.ViewModel.Rows[0]);

        Assert.Equal(1, fixture.Dialog.ConfirmCount);
        Assert.Equal(1, approvedWhileAsking);
        Assert.Empty(fixture.Store.GetApproved("srv-1"));
    }

    [Fact]
    public async Task Forget_RefusedConfirmation_LeavesTheEntryAlone()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        await fixture.ViewModel.RefreshAsync();
        fixture.Dialog.ConfirmResult = false;

        await fixture.ViewModel.ForgetCommand.ExecuteAsync(fixture.ViewModel.Rows[0]);

        Assert.Single(fixture.Store.GetApproved("srv-1"));
        Assert.Single(fixture.ViewModel.Rows);
    }

    [Fact]
    public async Task Forget_ConfirmationThatThrows_IsNotReadAsAYes()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        await fixture.ViewModel.RefreshAsync();
        fixture.Dialog.ConfirmThrows = true;

        await fixture.ViewModel.ForgetCommand.ExecuteAsync(fixture.ViewModel.Rows[0]);

        // A dialog that could not be shown is not an approval. Letting the exception stand in
        // for a yes would revoke trust the user never agreed to revoke.
        Assert.Single(fixture.Store.GetApproved("srv-1"));
        Assert.Single(fixture.ViewModel.Rows);
        Assert.True(fixture.ViewModel.HasStatusMessage);
    }

    [Fact]
    public async Task Forget_Confirmed_RemovesTheEntryAndDropsTheRow()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:02", Stamp));
        await fixture.ViewModel.RefreshAsync();
        fixture.Dialog.ConfirmResult = true;

        var target = fixture.ViewModel.Rows.Single(r => r.Thumbprint == "SHA256:AA:BB:01");
        await fixture.ViewModel.ForgetCommand.ExecuteAsync(target);

        Assert.Equal(["SHA256:AA:BB:02"], fixture.Store.GetApproved("srv-1").Select(e => e.Thumbprint));
        Assert.Equal(["SHA256:AA:BB:02"], fixture.ViewModel.Rows.Select(r => r.Thumbprint));
        Assert.True(fixture.ViewModel.HasStatusMessage);
    }

    /// <summary>
    /// The removal must reach settings.json, through the very handler the application wires.
    /// </summary>
    /// <remarks>
    /// A screen that forgets only until the next restart is the same defect wearing a different
    /// hat, and no double can catch it: the assertion is a reload from disk after the real
    /// <c>App.PersistTrustedRdpCertificatesAsync</c> ran off the store's own
    /// <c>TrustChanged</c> event, which is the wiring in <c>App.OnStartup</c>.
    /// </remarks>
    [Fact]
    public async Task Forget_Confirmed_PersistsThroughTheApplicationsOwnTrustChangedHandler()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "Heimdall-TrustedRdpCertificatesSettingsViewModelTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();
            await App.PersistTrustedRdpCertificatesAsync(
                configManager,
                "srv-1",
                [
                    new RdpCertificateEntry("SHA256:AA:BB:01", Stamp),
                    new RdpCertificateEntry("SHA256:AA:BB:02", Stamp),
                ]);

            AppSettings settings = await configManager.LoadSettingsAsync();
            var fixture = await VmFixture.CreateAsync();
            fixture.Store.LoadFromConfig(settings.TrustedRdpCertificates.Select(
                pair => (pair.Key, (IEnumerable<RdpCertificateEntry>)pair.Value)));

            var persisted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Store.TrustChanged += (profileId, entries) =>
            {
                _ = App.PersistTrustedRdpCertificatesAsync(configManager, profileId, entries)
                    .ContinueWith(
                        _ => persisted.TrySetResult(true),
                        TaskScheduler.Default);
            };

            await fixture.ViewModel.RefreshAsync();
            fixture.Dialog.ConfirmResult = true;
            await fixture.ViewModel.ForgetCommand.ExecuteAsync(
                fixture.ViewModel.Rows.Single(r => r.Thumbprint == "SHA256:AA:BB:01"));

            await persisted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var reloadedManager = new ConfigManager(rootPath);
            await reloadedManager.InitializeAsync();
            AppSettings reloaded = await reloadedManager.LoadSettingsAsync();

            Assert.Equal(
                ["SHA256:AA:BB:02"],
                reloaded.TrustedRdpCertificates["srv-1"].Select(e => e.Thumbprint));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Forget_TheLastCertificateOfAProfile_LeavesNoProfileBehind()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        await fixture.ViewModel.RefreshAsync();
        fixture.Dialog.ConfirmResult = true;

        await fixture.ViewModel.ForgetCommand.ExecuteAsync(fixture.ViewModel.Rows[0]);

        Assert.Empty(fixture.Store.GetAllApproved());
        Assert.True(fixture.ViewModel.IsEmptyStateVisible);
    }

    [Fact]
    public async Task SearchText_FiltersOnServerNameAndThumbprint()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Profiles.Add(Profile("srv-1", "Domain controller A"));
        fixture.Profiles.Add(Profile("srv-2", "Build agent"));
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        fixture.Store.Trust("srv-2", new RdpCertificateEntry("SHA256:CC:DD:02", Stamp));
        await fixture.ViewModel.RefreshAsync();

        fixture.ViewModel.SearchText = "build";
        Assert.Equal(["SHA256:CC:DD:02"], fixture.ViewModel.Rows.Select(r => r.Thumbprint));

        fixture.ViewModel.SearchText = "aa:bb";
        Assert.Equal(["SHA256:AA:BB:01"], fixture.ViewModel.Rows.Select(r => r.Thumbprint));

        fixture.ViewModel.SearchText = string.Empty;
        Assert.Equal(2, fixture.ViewModel.Rows.Count);

        // Filtering to nothing is not the same as trusting nothing: the empty state offers to go
        // and connect somewhere, which is wrong advice while rows exist behind a filter.
        fixture.ViewModel.SearchText = "no-such-thing";
        Assert.Empty(fixture.ViewModel.Rows);
        Assert.False(fixture.ViewModel.IsEmptyStateVisible);
    }

    [Fact]
    public async Task Refresh_WhenTheProfileInventoryCannotBeRead_StillListsTheTrustDecisions()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        fixture.ProfilesThrow = true;

        await fixture.ViewModel.RefreshAsync();

        // Losing the names is a degradation. Losing the list would hide trust the user holds.
        var row = Assert.Single(fixture.ViewModel.Rows);
        Assert.Contains("srv-1", row.ProfileDisplay, StringComparison.Ordinal);
        Assert.True(fixture.ViewModel.HasStatusMessage);
    }

    /// <summary>
    /// A profile created since the last refresh is named, not accused of having been deleted.
    /// </summary>
    /// <remarks>
    /// The name map is filled when the settings panel loads, which in a running session means
    /// once at startup. Adding a server does not go back through that path, so a certificate
    /// approved for a brand-new profile used to be drawn from a snapshot taken before the
    /// profile existed: the raw identifier, under a red "profile deleted" badge, for a machine
    /// the user had just connected to. The row that needed no attention was the one shouting.
    /// The suite missed it because the orphan case was only ever exercised through Refresh,
    /// which re-reads the inventory, and never through the store event, which did not.
    /// </remarks>
    [Fact]
    public async Task Trust_ForAProfileCreatedSinceTheLastRefresh_IsNamedRatherThanFlaggedDeleted()
    {
        var fixture = await VmFixture.CreateAsync();

        // The startup snapshot, taken while the profile does not exist yet.
        await fixture.ViewModel.RefreshAsync();

        // Add Server, then a connection that asks about the certificate and is answered.
        fixture.Profiles.Add(Profile("srv-new", "Lab DC"));
        fixture.Store.Trust("srv-new", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));

        var row = Assert.Single(fixture.ViewModel.Rows);
        Assert.Equal("Lab DC", row.ProfileDisplay);
        Assert.False(row.IsProfileMissing);
    }

    /// <summary>
    /// The control for the test above: the badge still appears when the profile really is gone.
    /// </summary>
    /// <remarks>
    /// Without this half, never flagging anything would satisfy the created-since-refresh test
    /// forever and the pair would measure nothing. The deletion happens after the snapshot on
    /// purpose, so the badge has to come from a reading of the inventory taken at the event.
    /// </remarks>
    [Fact]
    public async Task Trust_ForAProfileDeletedSinceTheLastRefresh_IsFlaggedDeleted()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Profiles.Add(Profile("srv-gone", "Retired host"));
        await fixture.ViewModel.RefreshAsync();

        fixture.Profiles.Clear();
        fixture.Store.Trust("srv-gone", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));

        var row = Assert.Single(fixture.ViewModel.Rows);
        Assert.Equal("srv-gone", row.ProfileDisplay);
        Assert.True(row.IsProfileMissing);
    }

    /// <summary>
    /// An inventory that cannot be read at event time keeps the names rather than badging all.
    /// </summary>
    /// <remarks>
    /// Clearing the map on a failed read is right for an explicit refresh, which reports the
    /// failure on the status line. Off a store event there is no line to report it on, so
    /// clearing would put "profile deleted" beside every live server at once - the same false
    /// statement the re-read exists to prevent, only louder.
    /// </remarks>
    [Fact]
    public async Task StoreChange_WhenTheInventoryCannotBeRead_KeepsTheNamesItAlreadyHas()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Profiles.Add(Profile("srv-1", "Domain controller A"));
        await fixture.ViewModel.RefreshAsync();

        fixture.ProfilesThrow = true;
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));

        var row = Assert.Single(fixture.ViewModel.Rows);
        Assert.Equal("Domain controller A", row.ProfileDisplay);
        Assert.False(row.IsProfileMissing);
    }

    /// <summary>
    /// The re-read runs behind the user's confirmation and must not wipe it off the screen.
    /// </summary>
    /// <remarks>
    /// Re-reading the inventory on a store change is a disk read, so it finishes after the
    /// removal that triggered it. Routing that re-read through <c>RefreshAsync</c> would end by
    /// assigning the status line - empty, on success - and would erase the only feedback the
    /// user gets that anything happened, moments after showing it. The inventory read is gated
    /// here so the ordering is a state transition rather than a wall-clock guess, and the row
    /// count taken before the gate opens is the control proving the gate really held.
    /// </remarks>
    [Fact]
    public async Task Forget_KeepsItsConfirmation_WhenTheInventoryReadCompletesAfterTheRemoval()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Profiles.Add(Profile("srv-1", "Domain controller A"));
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:02", Stamp));
        await fixture.ViewModel.RefreshAsync();
        fixture.Dialog.ConfirmResult = true;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuilt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProfileLoadGate = gate.Task;
        fixture.ViewModel.Rows.CollectionChanged += (_, _) => rebuilt.TrySetResult();

        await fixture.ViewModel.ForgetCommand.ExecuteAsync(
            fixture.ViewModel.Rows.Single(r => r.Thumbprint == "SHA256:AA:BB:01"));

        Assert.Equal(2, fixture.ViewModel.Rows.Count);
        Assert.True(fixture.ViewModel.HasStatusMessage);
        string confirmation = fixture.ViewModel.StatusMessage;

        gate.SetResult();
        await rebuilt.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["SHA256:AA:BB:02"], fixture.ViewModel.Rows.Select(r => r.Thumbprint));
        Assert.Equal(confirmation, fixture.ViewModel.StatusMessage);
    }

    /// <summary>
    /// A live screen follows the store; a disposed one lets go of it.
    /// </summary>
    /// <remarks>
    /// The first half is the control: without it, a view model that never subscribed at all
    /// would pass the second half, and the test would be a description of a stub rather than a
    /// measurement of the unsubscribe.
    /// </remarks>
    [Fact]
    public async Task Dispose_StopsListeningToTheStore()
    {
        var fixture = await VmFixture.CreateAsync();
        await fixture.ViewModel.RefreshAsync();

        fixture.Store.Trust("srv-1", new RdpCertificateEntry("SHA256:AA:BB:01", Stamp));
        Assert.Single(fixture.ViewModel.Rows);

        fixture.ViewModel.Dispose();
        fixture.Store.Trust("srv-2", new RdpCertificateEntry("SHA256:AA:BB:02", Stamp));

        Assert.Single(fixture.ViewModel.Rows);
    }

    private static ServerProfileDto Profile(string id, string displayName)
        => new() { Id = id, DisplayName = displayName, RemoteServer = id + ".example" };

    private sealed class VmFixture
    {
        private VmFixture(
            RdpCertificateTrustStore store,
            TrustedRdpCertificatesSettingsViewModel viewModel,
            FakeDialogService dialog)
        {
            Store = store;
            ViewModel = viewModel;
            Dialog = dialog;
        }

        public RdpCertificateTrustStore Store { get; }

        public TrustedRdpCertificatesSettingsViewModel ViewModel { get; }

        public FakeDialogService Dialog { get; }

        public List<ServerProfileDto> Profiles { get; } = [];

        public bool ProfilesThrow { get; set; }

        /// <summary>Held open, an inventory read finishes only when this task does.</summary>
        /// <remarks>
        /// The screen re-reads the inventory whenever the store changes, and on a real machine
        /// that read outlives the change. Left null, every read completes inline, which is what
        /// the rest of these tests want; set, it makes the ordering explicit without a delay.
        /// </remarks>
        public Task? ProfileLoadGate { get; set; }

        public static async Task<VmFixture> CreateAsync()
        {
            var store = new RdpCertificateTrustStore();
            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            var dialog = new FakeDialogService();
            VmFixture? fixture = null;
            var viewModel = new TrustedRdpCertificatesSettingsViewModel(
                store,
                async () =>
                {
                    if (fixture!.ProfilesThrow)
                    {
                        throw new IOException("servers.json is unreadable");
                    }

                    if (fixture.ProfileLoadGate is { } gate)
                    {
                        await gate.ConfigureAwait(false);
                    }

                    return (IReadOnlyList<ServerProfileDto>)fixture.Profiles;
                },
                localizer,
                dialog,
                new FakeUiDispatcher());

            fixture = new VmFixture(store, viewModel, dialog);
            return fixture;
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; }

        public bool ConfirmThrows { get; set; }

        public int ConfirmCount { get; private set; }

        public string LastConfirmMessage { get; private set; } = string.Empty;

        /// <summary>Runs while the confirmation is notionally on screen.</summary>
        public Action? OnConfirm { get; set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            ConfirmCount++;
            LastConfirmMessage = message;
            OnConfirm?.Invoke();
            return ConfirmThrows
                ? throw new InvalidOperationException("no dialog owner")
                : Task.FromResult(ConfirmResult);
        }

        public void ShowWarning(string title, string message)
        {
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
            => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
            => Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
            => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
            => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
            => Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
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
            => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public void ShowError(string title, string message)
        {
        }

        public void ShowInfo(string title, string message)
        {
        }
    }
}
