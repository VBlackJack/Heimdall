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
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

/// <summary>
/// Integration coverage for the external credential provider wiring in
/// <see cref="ServerListViewModel.TryResolveExternalCredentialsAsync"/>, exercised through a
/// fake <see cref="ICredentialProviderFactory"/> and stub <see cref="ICredentialProvider"/>
/// (no external processes are spawned).
/// </summary>
public sealed class ServerListExternalCredentialWiringTests
{
    [Fact]
    public async Task ResolveCredentials_ProviderDisabled_ReturnsFalseAndFactoryNeverCalled()
    {
        var factory = new FakeCredentialProviderFactory(new StubCredentialProvider());
        await using var fixture = await Fixture.CreateAsync(factory);

        var dto = CreateSshServer(passwordEncrypted: null);
        var settings = new AppSettings { UseExternalCredentialProvider = false };

        var result = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            dto, settings, CancellationToken.None, skipOnFailure: false);

        Assert.False(result);
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task ResolveCredentials_ProviderReturnsCredential_FillsEncryptedPasswordAndUsername()
    {
        var provider = new StubCredentialProvider
        {
            Result = new CredentialResult("vaultuser", "s3cr3t")
        };
        var factory = new FakeCredentialProviderFactory(provider);
        await using var fixture = await Fixture.CreateAsync(factory);

        var dto = CreateSshServer(passwordEncrypted: null);
        dto.SshUsername = "";
        var settings = EnabledSettings();

        var result = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            dto, settings, CancellationToken.None, skipOnFailure: false);

        Assert.False(result); // false == continue connecting (no skip)
        Assert.Equal(1, factory.CreateCallCount);
        Assert.False(string.IsNullOrEmpty(dto.SshPasswordEncrypted));
        Assert.Equal("s3cr3t", CredentialProtector.Unprotect(dto.SshPasswordEncrypted));
        Assert.Equal("vaultuser", dto.SshUsername);
    }

    [Fact]
    public async Task ResolveCredentials_ProviderReturnsNull_SkipOnFailure_ReturnsTrueNoDialog()
    {
        var factory = new FakeCredentialProviderFactory(new StubCredentialProvider { Result = null });
        await using var fixture = await Fixture.CreateAsync(factory);

        var dto = CreateSshServer(passwordEncrypted: null);
        var settings = EnabledSettings();

        var result = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            dto, settings, CancellationToken.None, skipOnFailure: true);

        Assert.True(result); // true == skip this server
        Assert.Equal(0, fixture.Dialog.WarningCount);
        Assert.Equal(0, fixture.Dialog.ErrorCount);
        Assert.True(string.IsNullOrEmpty(dto.SshPasswordEncrypted));
    }

    [Fact]
    public async Task ResolveCredentials_ProviderReturnsNull_NoSkip_ShowsWarningReturnsFalse()
    {
        var factory = new FakeCredentialProviderFactory(new StubCredentialProvider { Result = null });
        await using var fixture = await Fixture.CreateAsync(factory);

        var dto = CreateSshServer(passwordEncrypted: null);
        var settings = EnabledSettings();

        var result = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            dto, settings, CancellationToken.None, skipOnFailure: false);

        Assert.False(result);
        Assert.Equal(1, fixture.Dialog.WarningCount);
        Assert.Equal(0, fixture.Dialog.ErrorCount);
    }

    [Fact]
    public async Task ResolveCredentials_ProviderThrows_NoSkip_ShowsErrorReturnsFalse()
    {
        var provider = new StubCredentialProvider
        {
            Exception = new InvalidOperationException("boom")
        };
        var factory = new FakeCredentialProviderFactory(provider);
        await using var fixture = await Fixture.CreateAsync(factory);

        var dto = CreateSshServer(passwordEncrypted: null);
        var settings = EnabledSettings();

        var result = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            dto, settings, CancellationToken.None, skipOnFailure: false);

        Assert.False(result);
        Assert.Equal(1, fixture.Dialog.ErrorCount);
        Assert.Equal(0, fixture.Dialog.WarningCount);
    }

    [Fact]
    public async Task ResolveCredentials_ProviderThrows_SkipOnFailure_ReturnsTrueNoDialog()
    {
        var provider = new StubCredentialProvider
        {
            Exception = new InvalidOperationException("boom")
        };
        var factory = new FakeCredentialProviderFactory(provider);
        await using var fixture = await Fixture.CreateAsync(factory);

        var dto = CreateSshServer(passwordEncrypted: null);
        var settings = EnabledSettings();

        var result = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            dto, settings, CancellationToken.None, skipOnFailure: true);

        Assert.True(result);
        Assert.Equal(0, fixture.Dialog.ErrorCount);
        Assert.Equal(0, fixture.Dialog.WarningCount);
    }

    [Fact]
    public async Task ResolveCredentials_ProviderCancelled_Propagates()
    {
        var provider = new StubCredentialProvider
        {
            Exception = new OperationCanceledException()
        };
        var factory = new FakeCredentialProviderFactory(provider);
        await using var fixture = await Fixture.CreateAsync(factory);

        var dto = CreateSshServer(passwordEncrypted: null);
        var settings = EnabledSettings();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.ViewModel.TryResolveExternalCredentialsAsync(
                dto, settings, CancellationToken.None, skipOnFailure: false));

        Assert.Equal(0, fixture.Dialog.ErrorCount);
        Assert.Equal(0, fixture.Dialog.WarningCount);
    }

    private static AppSettings EnabledSettings() => new()
    {
        UseExternalCredentialProvider = true,
        CredentialProviderCommand = "ignored-by-fake-factory"
    };

    private static ServerProfileDto CreateSshServer(string? passwordEncrypted) => new()
    {
        Id = "srv-ssh",
        DisplayName = "SSH Box",
        RemoteServer = "ssh.example.com",
        ConnectionType = "SSH",
        SshPort = 22,
        SshUsername = "",
        SshPasswordEncrypted = passwordEncrypted,
        Origin = Core.Models.ProfileOrigin.Manual
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _rootPath;

        private Fixture(string rootPath, ServerListViewModel viewModel, RecordingDialogService dialog)
        {
            _rootPath = rootPath;
            ViewModel = viewModel;
            Dialog = dialog;
        }

        public ServerListViewModel ViewModel { get; }

        public RecordingDialogService Dialog { get; }

        public static async Task<Fixture> CreateAsync(ICredentialProviderFactory factory)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(), "heimdall-cred-wiring", Guid.NewGuid().ToString("N"));
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();

            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            var stateMachine = new ConnectionStateMachine();
            var connectionService = new ConnectionService(
                configManager, localizer, new NullTunnelService(), Array.Empty<IProtocolHandler>());
            var dialog = new RecordingDialogService();
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
                credentialProviderFactory: factory);

            return new Fixture(rootPath, viewModel, dialog);
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

    private sealed class FakeCredentialProviderFactory(ICredentialProvider provider) : ICredentialProviderFactory
    {
        public int CreateCallCount { get; private set; }

        public ICredentialProvider Create(AppSettings settings)
        {
            CreateCallCount++;
            return provider;
        }
    }

    private sealed class StubCredentialProvider : ICredentialProvider
    {
        public string Name => "Stub";

        public bool IsAvailable { get; init; } = true;

        public CredentialResult? Result { get; init; }

        public Exception? Exception { get; init; }

        public Task<CredentialResult?> GetCredentialAsync(
            string serverHost,
            int port,
            string? username,
            string? title,
            CancellationToken ct = default)
        {
            if (Exception is not null)
            {
                return Task.FromException<CredentialResult?>(Exception);
            }

            return Task.FromResult(Result);
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

    private sealed class RecordingDialogService : IDialogService
    {
        public int WarningCount { get; private set; }

        public int ErrorCount { get; private set; }

        public void ShowWarning(string title, string message) => WarningCount++;

        public void ShowError(string title, string message) => ErrorCount++;

        public void ShowInfo(string title, string message)
        {
        }

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
    }
}
