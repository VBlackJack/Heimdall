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
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class TrustedHostKeysSettingsViewModelTests
{
    [Fact]
    public async Task EmptyStore_ShowsEmptyState()
    {
        var fixture = await VmFixture.CreateAsync();

        Assert.Empty(fixture.ViewModel.Rows);
        Assert.True(fixture.ViewModel.IsEmptyStateVisible);
    }

    [Fact]
    public async Task Rows_DefaultSortsByLastSeenDescending_AndFiltersByHostPort()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.TrustEntry("old.example.com", 22, Entry("SHA256:old", -3));
        fixture.Store.TrustEntry("new.example.com", 22, Entry("SHA256:new", -1));
        fixture.Store.TrustEntry("other.example.com", 2200, Entry("SHA256:other", -2));

        fixture.ViewModel.Refresh();

        Assert.Equal(["new.example.com:22", "other.example.com:2200", "old.example.com:22"],
            fixture.ViewModel.Rows.Select(static row => row.HostPort));

        fixture.ViewModel.SearchText = "OLD";

        var row = Assert.Single(fixture.ViewModel.Rows);
        Assert.Equal("old.example.com:22", row.HostPort);
    }

    [Fact]
    public async Task SortByCommand_TogglesRequestedColumn()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.TrustEntry("b.example.com", 22, Entry("SHA256:b", -1));
        fixture.Store.TrustEntry("a.example.com", 22, Entry("SHA256:a", -2));
        fixture.ViewModel.Refresh();

        fixture.ViewModel.SortByCommand.Execute("HostPort");

        Assert.True(fixture.ViewModel.SortAscending);
        Assert.Equal(["a.example.com:22", "b.example.com:22"],
            fixture.ViewModel.Rows.Select(static row => row.HostPort));

        fixture.ViewModel.SortByCommand.Execute("HostPort");

        Assert.False(fixture.ViewModel.SortAscending);
        Assert.Equal(["b.example.com:22", "a.example.com:22"],
            fixture.ViewModel.Rows.Select(static row => row.HostPort));
    }

    [Fact]
    public async Task TrustRemoveAndReplaceEvents_UpdateRows()
    {
        var fixture = await VmFixture.CreateAsync();

        fixture.Trust.Trust("server.example.com", 22, "SHA256:first", "ssh-ed25519", HostKeySource.UserConfirmed);

        var row = Assert.Single(fixture.ViewModel.Rows);
        Assert.Equal("SHA256:first", row.Fingerprint);

        fixture.Trust.Trust("server.example.com", 22, "SHA256:second", "ssh-rsa", HostKeySource.UserConfirmed);

        row = Assert.Single(fixture.ViewModel.Rows);
        Assert.Equal("SHA256:second", row.Fingerprint);
        Assert.Equal("ssh-rsa", row.Algorithm);

        Assert.True(fixture.Trust.Remove("server.example.com", 22));
        Assert.Empty(fixture.ViewModel.Rows);
    }

    [Fact]
    public async Task RemoveCommand_RequiresConfirmation()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Trust.Trust("server.example.com", 22, "SHA256:fingerprint", "ssh-ed25519", HostKeySource.UserConfirmed);
        var row = Assert.Single(fixture.ViewModel.Rows);

        fixture.Dialog.ConfirmResult = false;
        await fixture.ViewModel.RemoveCommand.ExecuteAsync(row);

        Assert.NotNull(fixture.Trust.GetEntry("server.example.com", 22));
        Assert.Contains("server.example.com:22", fixture.Dialog.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Contains("SHA256:fingerprint", fixture.Dialog.LastConfirmMessage, StringComparison.Ordinal);

        fixture.Dialog.ConfirmResult = true;
        await fixture.ViewModel.RemoveCommand.ExecuteAsync(row);

        Assert.Null(fixture.Trust.GetEntry("server.example.com", 22));
    }

    [Fact]
    public async Task CopyFingerprint_CopiesFullFingerprint()
    {
        var fixture = await VmFixture.CreateAsync();
        var fullFingerprint = "SHA256:abcdefghijklmnopqrstuvwxyz0123456789";
        fixture.Trust.Trust("server.example.com", 22, fullFingerprint, "ssh-ed25519", HostKeySource.UserConfirmed);
        var row = Assert.Single(fixture.ViewModel.Rows);

        fixture.ViewModel.CopyFingerprintCommand.Execute(row);

        Assert.Equal(fullFingerprint, fixture.Clipboard.Text);
        Assert.Contains("server.example.com:22", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailsCommand_ProvidesAllFieldsAndPublicKeyFallback()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.TrustEntry(
            "with-key.example.com",
            22,
            Entry("SHA256:with-key", -1, "ssh-ed25519", HostKeySource.ImportedKnownHosts, "AAAAB3NzaC1yc2EAAAADAQABAAABAQ"));
        fixture.Store.TrustEntry(
            "without-key.example.com",
            22,
            Entry("SHA256:without-key", -2, "ssh-ed25519", HostKeySource.Unknown));
        fixture.ViewModel.Refresh();

        await fixture.ViewModel.ShowDetailsCommand.ExecuteAsync(
            fixture.ViewModel.Rows.Single(row => row.HostPort == "with-key.example.com:22"));

        Assert.NotNull(fixture.Dialog.LastDetails);
        Assert.Equal("AAAAB3NzaC1yc2EAAAADAQABAAABAQ", fixture.Dialog.LastDetails.PublicKeyBase64);
        Assert.Equal("Imported known_hosts", fixture.Dialog.LastDetails.Source);

        await fixture.ViewModel.ShowDetailsCommand.ExecuteAsync(
            fixture.ViewModel.Rows.Single(row => row.HostPort == "without-key.example.com:22"));

        Assert.NotNull(fixture.Dialog.LastDetails);
        Assert.Contains("not available", fixture.Dialog.LastDetails.PublicKeyBase64, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportWithoutConflicts_MergesAndShowsSummary()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.ImportHandler = () =>
        {
            fixture.Trust.Import(
                "imported.example.com",
                22,
                "SHA256:imported",
                "ssh-ed25519",
                DateTimeOffset.UtcNow,
                "AAAAC3NzaC1lZDI1NTE5AAAA");
            return new KnownHostsImportReport(1, 0, []);
        };

        await fixture.ViewModel.ImportKnownHostsCommand.ExecuteAsync(null);

        Assert.Equal("SHA256:imported", fixture.Trust.GetEntry("imported.example.com", 22)?.Fingerprint);
        Assert.Contains("1 keys imported", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportConflict_DefaultKeepDoesNotReplace_ReplaceSelectionDoes()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Trust.Trust("conflict.example.com", 22, "SHA256:existing", "ssh-ed25519", HostKeySource.UserConfirmed);
        fixture.ImportHandler = () => new KnownHostsImportReport(
            0,
            0,
            [new KnownHostsImportConflict("conflict.example.com", 22, "SHA256:existing", "SHA256:imported", "ssh-rsa", 4, "AAAAimported")]);

        fixture.Dialog.ConflictHandler = vm =>
        {
            var row = Assert.Single(vm.Items);
            Assert.True(row.KeepExisting);
            return new ImportKnownHostsConflictResolution(
            [
                new ImportKnownHostsConflictSelection(
                    row.Host,
                    row.Port,
                    row.ImportedFingerprint,
                    row.Algorithm,
                    ReplaceWithImported: false)
            ]);
        };

        await fixture.ViewModel.ImportKnownHostsCommand.ExecuteAsync(null);

        Assert.Equal("SHA256:existing", fixture.Trust.GetEntry("conflict.example.com", 22)?.Fingerprint);

        fixture.Dialog.ConflictHandler = vm =>
        {
            var row = Assert.Single(vm.Items);
            row.ReplaceWithImported = true;
            return new ImportKnownHostsConflictResolution(
            [
                new ImportKnownHostsConflictSelection(
                    row.Host,
                    row.Port,
                    row.ImportedFingerprint,
                    row.Algorithm,
                    row.ReplaceWithImported,
                    row.ImportedPublicKeyBase64)
            ]);
        };

        await fixture.ViewModel.ImportKnownHostsCommand.ExecuteAsync(null);

        var entry = fixture.Trust.GetEntry("conflict.example.com", 22);
        Assert.NotNull(entry);
        Assert.Equal("SHA256:imported", entry.Fingerprint);
        Assert.Equal(HostKeySource.ImportedKnownHosts, entry.Source);
        Assert.Equal("AAAAimported", entry.PublicKeyBase64);
    }

    [Fact]
    public async Task ExportReportsSkippedEntries()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.ExportHandler = () => new KnownHostsExportReport(1, 2, 3);

        await fixture.ViewModel.ExportKnownHostsCommand.ExecuteAsync(null);

        Assert.Contains("Exported 1 keys", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("3 entries skipped", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.NotNull(fixture.Dialog.LastWarningMessage);
    }

    [Fact]
    public async Task SourceDisplay_IsLocalizedNotRawEnum()
    {
        var fixture = await VmFixture.CreateAsync();
        fixture.Store.TrustEntry(
            "imported.example.com",
            22,
            Entry("SHA256:imported", -1, "ssh-ed25519", HostKeySource.ImportedKnownHosts));

        fixture.ViewModel.Refresh();

        var row = Assert.Single(fixture.ViewModel.Rows);
        Assert.Equal("Imported known_hosts", row.SourceDisplay);
        Assert.NotEqual(HostKeySource.ImportedKnownHosts.ToString(), row.SourceDisplay);
    }

    private static HostKeyEntry Entry(
        string fingerprint,
        int daysAgo,
        string algorithm = "ssh-ed25519",
        HostKeySource source = HostKeySource.UserConfirmed,
        string? publicKeyBase64 = null)
    {
        var seen = DateTimeOffset.UtcNow.AddDays(daysAgo);
        return new HostKeyEntry(fingerprint, seen, seen, algorithm, source)
        {
            PublicKeyBase64 = publicKeyBase64
        };
    }

    private sealed class VmFixture
    {
        private VmFixture(
            HostKeyStore store,
            IHostKeyTrustService trust,
            TrustedHostKeysSettingsViewModel viewModel,
            FakeDialogService dialog,
            FakeClipboardService clipboard)
        {
            Store = store;
            Trust = trust;
            ViewModel = viewModel;
            Dialog = dialog;
            Clipboard = clipboard;
        }

        public HostKeyStore Store { get; }

        public IHostKeyTrustService Trust { get; }

        public TrustedHostKeysSettingsViewModel ViewModel { get; }

        public FakeDialogService Dialog { get; }

        public FakeClipboardService Clipboard { get; }

        public Func<KnownHostsImportReport> ImportHandler { get; set; } = () => new KnownHostsImportReport(0, 0, []);

        public Func<KnownHostsExportReport> ExportHandler { get; set; } = () => new KnownHostsExportReport(0, 0, 0);

        public static async Task<VmFixture> CreateAsync()
        {
            var store = new HostKeyStore();
            var trust = new HostKeyTrustService(store);
            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            var dialog = new FakeDialogService();
            var clipboard = new FakeClipboardService();
            var dispatcher = new FakeUiDispatcher();
            VmFixture? fixture = null;
            var viewModel = new TrustedHostKeysSettingsViewModel(
                trust,
                () => fixture!.ImportHandler(),
                () => fixture!.ExportHandler(),
                localizer,
                dialog,
                clipboard,
                dispatcher);

            fixture = new VmFixture(store, trust, viewModel, dialog, clipboard);
            return fixture;
        }
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public string? Text { get; private set; }

        public void SetText(string text) => Text = text;
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; }

        public string LastConfirmMessage { get; private set; } = string.Empty;

        public TrustedHostKeyDetailsDialogViewModel? LastDetails { get; private set; }

        public string? LastWarningMessage { get; private set; }

        public Func<ImportKnownHostsConflictDialogViewModel, ImportKnownHostsConflictResolution?>? ConflictHandler { get; set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            LastConfirmMessage = message;
            return Task.FromResult(ConfirmResult);
        }

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
        {
            LastDetails = viewModel;
            return Task.CompletedTask;
        }

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult(ConflictHandler?.Invoke(viewModel));

        public void ShowWarning(string title, string message) => LastWarningMessage = message;

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
