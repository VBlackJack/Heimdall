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
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

/// <summary>
/// Covers the optional Windows Hello connect gate
/// (<see cref="ServerListViewModel.EnsureWindowsHelloAsync"/>): opt-in behaviour,
/// fail-closed when unavailable, failure handling, and the grace window.
/// </summary>
public sealed class ServerListWindowsHelloGateTests
{
    [Fact]
    public async Task EnsureWindowsHello_SettingOff_ReturnsTrueAndNeverCallsService()
    {
        var hello = new FakeWindowsHelloService();
        await using var fixture = await Fixture.CreateAsync(hello);
        var settings = new AppSettings { RequireWindowsHelloOnConnect = false };

        bool allowed = await fixture.ViewModel.EnsureWindowsHelloAsync(settings, CancellationToken.None);

        Assert.True(allowed);
        Assert.Equal(0, hello.IsAvailableCalls);
        Assert.Equal(0, hello.VerifyCalls);
    }

    [Fact]
    public async Task EnsureWindowsHello_AvailableAndVerified_ReturnsTrue()
    {
        var hello = new FakeWindowsHelloService { Available = true, VerifyResult = true };
        await using var fixture = await Fixture.CreateAsync(hello);
        var settings = EnabledSettings(graceMinutes: 0);

        bool allowed = await fixture.ViewModel.EnsureWindowsHelloAsync(settings, CancellationToken.None);

        Assert.True(allowed);
        Assert.Equal(1, hello.IsAvailableCalls);
        Assert.Equal(1, hello.VerifyCalls);
    }

    [Fact]
    public async Task EnsureWindowsHello_Unavailable_FailsClosedWithoutVerifying()
    {
        var hello = new FakeWindowsHelloService { Available = false };
        await using var fixture = await Fixture.CreateAsync(hello);
        var settings = EnabledSettings(graceMinutes: 5);

        bool allowed = await fixture.ViewModel.EnsureWindowsHelloAsync(settings, CancellationToken.None);

        Assert.False(allowed);
        Assert.Equal(1, hello.IsAvailableCalls);
        Assert.Equal(0, hello.VerifyCalls);
    }

    [Fact]
    public async Task EnsureWindowsHello_VerificationFails_ReturnsFalse()
    {
        var hello = new FakeWindowsHelloService { Available = true, VerifyResult = false };
        await using var fixture = await Fixture.CreateAsync(hello);
        var settings = EnabledSettings(graceMinutes: 5);

        bool allowed = await fixture.ViewModel.EnsureWindowsHelloAsync(settings, CancellationToken.None);

        Assert.False(allowed);
        Assert.Equal(1, hello.VerifyCalls);
    }

    [Fact]
    public async Task EnsureWindowsHello_WithinGraceWindow_DoesNotReverify()
    {
        var hello = new FakeWindowsHelloService { Available = true, VerifyResult = true };
        await using var fixture = await Fixture.CreateAsync(hello);
        var settings = EnabledSettings(graceMinutes: 60);

        bool first = await fixture.ViewModel.EnsureWindowsHelloAsync(settings, CancellationToken.None);
        bool second = await fixture.ViewModel.EnsureWindowsHelloAsync(settings, CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        // Second call is served from the in-memory grace window: no extra prompts.
        Assert.Equal(1, hello.VerifyCalls);
        Assert.Equal(1, hello.IsAvailableCalls);
    }

    [Fact]
    public async Task EnsureWindowsHello_GraceExpired_ReverifiesEachConnect()
    {
        var hello = new FakeWindowsHelloService { Available = true, VerifyResult = true };
        await using var fixture = await Fixture.CreateAsync(hello);
        // Zero-minute grace = always re-verify (the boundary case for an expired window).
        var settings = EnabledSettings(graceMinutes: 0);

        await fixture.ViewModel.EnsureWindowsHelloAsync(settings, CancellationToken.None);
        await fixture.ViewModel.EnsureWindowsHelloAsync(settings, CancellationToken.None);

        Assert.Equal(2, hello.VerifyCalls);
    }

    private static AppSettings EnabledSettings(int graceMinutes) => new()
    {
        RequireWindowsHelloOnConnect = true,
        WindowsHelloGraceMinutes = graceMinutes
    };

    private sealed class FakeWindowsHelloService : IWindowsHelloService
    {
        public bool Available { get; init; } = true;

        public bool VerifyResult { get; init; } = true;

        public int IsAvailableCalls { get; private set; }

        public int VerifyCalls { get; private set; }

        public Task<bool> IsAvailableAsync()
        {
            IsAvailableCalls++;
            return Task.FromResult(Available);
        }

        public Task<bool> VerifyAsync(string message, CancellationToken ct)
        {
            VerifyCalls++;
            return Task.FromResult(VerifyResult);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _rootPath;

        private Fixture(string rootPath, ServerListViewModel viewModel)
        {
            _rootPath = rootPath;
            ViewModel = viewModel;
        }

        public ServerListViewModel ViewModel { get; }

        public static async Task<Fixture> CreateAsync(IWindowsHelloService helloService)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(), "heimdall-hello-gate", Guid.NewGuid().ToString("N"));
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();

            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            var stateMachine = new ConnectionStateMachine();
            var connectionService = new ConnectionService(
                configManager, localizer, new NullTunnelService(), Array.Empty<IProtocolHandler>());
            var dialog = new NullDialogService();
            var puttyImporter = new PuttySessionImporter(new FakePuttySessionRegistrySource([]), configManager);
            var knownHostsImporter = new KnownHostsImporter(configManager, new HostKeyStore());
            var uiDispatcher = new FakeUiDispatcher();

            var viewModel = new ServerListViewModel(
                configManager,
                localizer,
                uiDispatcher,
                stateMachine,
                connectionService,
                dialog,
                new NullRdpImportService(),
                puttyImporter,
                knownHostsImporter,
                windowsHelloService: helloService);

            return new Fixture(rootPath, viewModel);
        }

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();

            try
            {
                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullTunnelService : ITunnelService
    {
        public Task<(bool Success, bool UsesTunnel, string Host, int Port, string? ErrorMessage)> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
            => Task.FromResult((true, false, server.RemoteServer, remotePort, (string?)null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class NullRdpImportService : IRdpImportService
    {
        public Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct) =>
            Task.FromResult(new RdpImportPreview
            {
                Entries = [],
                FilesNotFound = [],
                FilesUnreadable = []
            });

        public Task<RdpImportResult> ApplyAsync(RdpImportPreview preview, RdpImportSelection selection, CancellationToken ct) =>
            Task.FromResult(new RdpImportResult());
    }

    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info") => Task.FromResult(false);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null) => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null) => Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null) => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) => Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel) => Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel) => Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview) => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel) => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);

        public void ShowError(string title, string message)
        {
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }
    }
}
