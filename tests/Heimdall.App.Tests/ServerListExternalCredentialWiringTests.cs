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
// Touches the static CredentialProtector; serialized with the vault tests, and pinned to the
// no-vault baseline so a writer scheduled before this class cannot leave it locked.
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerListExternalCredentialWiringTests : IDisposable
{
    private readonly CredentialProtectorStateScope _scope = new();

    public void Dispose()
    {
        _scope.Dispose();
    }

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

    [Fact]
    public async Task ResolveCredentials_SharedProfile_LooksTheVaultUpByHostNotByName()
    {
        var provider = new StubCredentialProvider { Result = new CredentialResult("", "s3cr3t") };
        await using var fixture = await Fixture.CreateAsync(new FakeCredentialProviderFactory(provider));

        // The shared file names the session after a machine and points it at another.
        var dto = CreateSshServer(passwordEncrypted: null);
        dto.Id = SharedSessionIds.PrefixFor("teamsource") + "0123abcd";
        dto.DisplayName = "prod-db";
        dto.RemoteServer = "elsewhere.example.com";
        dto.VaultEntryName = "prod-db";

        await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            dto, EnabledSettings(), CancellationToken.None, skipOnFailure: false);

        Assert.Equal("elsewhere.example.com", provider.LastTitle);
    }

    [Fact]
    public async Task ResolveCredentials_LocalProfile_LooksTheVaultUpByEntryNameThenDisplayName()
    {
        var provider = new StubCredentialProvider { Result = new CredentialResult("", "s3cr3t") };
        await using var fixture = await Fixture.CreateAsync(new FakeCredentialProviderFactory(provider));

        var named = CreateSshServer(passwordEncrypted: null);
        await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            named, EnabledSettings(), CancellationToken.None, skipOnFailure: false);
        Assert.Equal("SSH Box", provider.LastTitle);

        var referenced = CreateSshServer(passwordEncrypted: null);
        referenced.VaultEntryName = "vault-entry";
        await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            referenced, EnabledSettings(), CancellationToken.None, skipOnFailure: false);
        Assert.Equal("vault-entry", provider.LastTitle);
    }

    [Fact]
    public async Task ResolveCredentials_ProviderTimesOut_NoSkip_ShowsTheTimeoutNotACancellation()
    {
        // A vault timeout used to arrive as an OperationCanceledException: the connect ended
        // without a word, as if the user had cancelled it.
        var provider = new StubCredentialProvider
        {
            Exception = new CredentialProviderTimeoutException(TimeSpan.FromMilliseconds(4321))
        };
        await using var fixture = await Fixture.CreateAsync(new FakeCredentialProviderFactory(provider));
        var settings = EnabledSettings();
        settings.CredentialProviderTimeoutMs = 4321;

        var result = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            CreateSshServer(passwordEncrypted: null), settings, CancellationToken.None, skipOnFailure: false);

        Assert.False(result);
        Assert.Equal(1, fixture.Dialog.ErrorCount);
        Assert.Equal(
            fixture.Localizer.Format("ErrorCredentialProviderTimeout", "SSH Box", 4321),
            fixture.Dialog.LastErrorMessage);
    }

    [Fact]
    public async Task ResolveCredentials_ProviderTimesOut_SkipOnFailure_SkipsTheServer()
    {
        // The bulk path: one slow entry is skipped, it does not end the batch.
        var provider = new StubCredentialProvider
        {
            Exception = new CredentialProviderTimeoutException(TimeSpan.FromSeconds(1))
        };
        await using var fixture = await Fixture.CreateAsync(new FakeCredentialProviderFactory(provider));

        var result = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            CreateSshServer(passwordEncrypted: null), EnabledSettings(), CancellationToken.None, skipOnFailure: true);

        Assert.True(result);
        Assert.Equal(0, fixture.Dialog.ErrorCount);
    }

    [Fact]
    public async Task ResolveCredentials_ToolMissing_NamesTheExecutable()
    {
        var provider = new StubCredentialProvider
        {
            Exception = new CredentialProviderLaunchException(
                "keepassxc-cli", new System.ComponentModel.Win32Exception(2))
        };
        await using var fixture = await Fixture.CreateAsync(new FakeCredentialProviderFactory(provider));

        await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            CreateSshServer(passwordEncrypted: null), EnabledSettings(), CancellationToken.None, skipOnFailure: false);

        Assert.Equal(
            fixture.Localizer.Format("ErrorCredentialProviderNotFound", "keepassxc-cli"),
            fixture.Dialog.LastErrorMessage);
    }

    [Fact]
    public async Task ResolveCredentials_SshProfileWithAKey_NeverRunsTheVaultCommand()
    {
        var provider = new StubCredentialProvider { Result = null };
        await using var fixture = await Fixture.CreateAsync(new FakeCredentialProviderFactory(provider));
        var dto = CreateSshServer(passwordEncrypted: null);
        dto.SshKeyPath = @"C:\keys\id_ed25519";

        var result = await fixture.ViewModel.TryResolveExternalCredentialsAsync(
            dto, EnabledSettings(), CancellationToken.None, skipOnFailure: false);

        Assert.False(result);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, fixture.Dialog.WarningCount);
    }

    [Fact]
    public async Task Restore_VaultHasNoEntry_RaisesNoModal()
    {
        // A restore reconnects on its own, several sessions at launch: the vault's "no password"
        // warning arrived as one modal per restored session.
        var provider = new StubCredentialProvider { Result = null };
        await using var fixture = await Fixture.CreateAsync(new FakeCredentialProviderFactory(provider));
        await fixture.SeedAsync(CreateSshServer(passwordEncrypted: null));

        await fixture.ViewModel.RestoreServerAsync(
            "srv-ssh", CancellationToken.None, failureReport: ConnectFailureReport.FailedTabOnly);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, fixture.Dialog.WarningCount);
    }

    [Fact]
    public async Task Connect_VaultHasNoEntry_StillWarnsTheUserWhoAsked()
    {
        // Control for the test above: the same profile, connected by hand, keeps its warning.
        var provider = new StubCredentialProvider { Result = null };
        await using var fixture = await Fixture.CreateAsync(new FakeCredentialProviderFactory(provider));
        await fixture.SeedAsync(CreateSshServer(passwordEncrypted: null));

        await fixture.ViewModel.ConnectCommand.ExecuteAsync(fixture.ViewModel.Servers[0]);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, fixture.Dialog.WarningCount);
    }

    [Fact]
    public async Task BulkPlan_VaultTimesOutOnOneServer_SkipsItAndKeepsTheOthers()
    {
        var provider = new StubCredentialProvider
        {
            ExceptionForTitle = ("SSH Box", new CredentialProviderTimeoutException(TimeSpan.FromSeconds(1))),
            Result = new CredentialResult("", "s3cr3t")
        };
        await using var fixture = await Fixture.CreateAsync(new FakeCredentialProviderFactory(provider));
        var slow = CreateSshServer(passwordEncrypted: null);
        var fast = CreateSshServer(passwordEncrypted: null);
        fast.Id = "srv-ssh-2";
        fast.DisplayName = "SSH Box 2";
        await fixture.SeedAsync(slow, fast);

        var plan = await fixture.ViewModel.PrepareBulkConnectPlanAsync(
            fixture.ViewModel.Servers.ToList(), CancellationToken.None);

        Assert.Equal(1, plan.ConnectableCount);
        var skip = Assert.Single(plan.Skips.Occurred);
        Assert.Equal(BulkConnectSkipReason.ExternalCredentialsUnresolved, skip.Key);
        Assert.Equal(1, skip.Value);
    }

    [Fact]
    public async Task BulkPlan_LooksTheVaultUpSideBySide_AtMostTheCapAtOnce()
    {
        // One after the other, a batch waited for one database unlock per server in a row.
        var provider = new ConcurrencyProbeProvider(ServerListViewModel.MaxParallelVaultLookups);
        await using var fixture = await Fixture.CreateAsync(new FakeCredentialProviderFactory(provider));
        var servers = Enumerable.Range(1, ServerListViewModel.MaxParallelVaultLookups + 2)
            .Select(index =>
            {
                var dto = CreateSshServer(passwordEncrypted: null);
                dto.Id = $"srv-ssh-{index}";
                dto.DisplayName = $"SSH Box {index}";
                return dto;
            })
            .ToArray();
        await fixture.SeedAsync(servers);

        var plan = await fixture.ViewModel.PrepareBulkConnectPlanAsync(
            fixture.ViewModel.Servers.ToList(), CancellationToken.None);

        Assert.Equal(ServerListViewModel.MaxParallelVaultLookups, provider.MaxConcurrent);
        Assert.Equal(servers.Length, plan.ConnectableCount);
        Assert.Equal(
            servers.Select(server => server.Id),
            plan.Candidates.Select(candidate => candidate.ServerDto.Id));
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

        private Fixture(
            string rootPath,
            ServerListViewModel viewModel,
            RecordingDialogService dialog,
            ConfigManager configManager,
            LocalizationManager localizer)
        {
            _rootPath = rootPath;
            ViewModel = viewModel;
            Dialog = dialog;
            ConfigManager = configManager;
            Localizer = localizer;
        }

        public ServerListViewModel ViewModel { get; }

        public RecordingDialogService Dialog { get; }

        public ConfigManager ConfigManager { get; }

        public LocalizationManager Localizer { get; }

        /// <summary>Saves the profiles, enables the provider and loads the list.</summary>
        public async Task SeedAsync(params ServerProfileDto[] servers)
        {
            await ConfigManager.SaveServersAsync([.. servers]);
            await ConfigManager.MergeSettingAsync(settings =>
            {
                settings.UseExternalCredentialProvider = true;
                settings.CredentialProviderCommand = "ignored-by-fake-factory";
            });

            ViewModel.LoadServers(
                await ConfigManager.LoadServersAsync(),
                await ConfigManager.LoadSettingsAsync());
        }

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

            return new Fixture(rootPath, viewModel, dialog, configManager, localizer);
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

        /// <summary>An exception thrown only for the entry of that title.</summary>
        public (string Title, Exception Exception)? ExceptionForTitle { get; init; }

        public string? LastTitle { get; private set; }

        public int CallCount { get; private set; }

        public Task<CredentialResult?> GetCredentialAsync(
            string serverHost,
            int port,
            string? username,
            string? title,
            CancellationToken ct = default)
        {
            LastTitle = title;
            CallCount++;
            if (ExceptionForTitle is { } scoped && scoped.Title == title)
            {
                return Task.FromException<CredentialResult?>(scoped.Exception);
            }

            if (Exception is not null)
            {
                return Task.FromException<CredentialResult?>(Exception);
            }

            return Task.FromResult(Result);
        }
    }

    /// <summary>
    /// Holds every lookup until <c>expected</c> run at once (or a fallback delay passes), and
    /// records the highest number seen running together.
    /// </summary>
    private sealed class ConcurrencyProbeProvider(int expected) : ICredentialProvider
    {
        private static readonly TimeSpan Fallback = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(300);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private int _maxConcurrent;

        public string Name => "Probe";

        public bool IsAvailable => true;

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public async Task<CredentialResult?> GetCredentialAsync(
            string serverHost,
            int port,
            string? username,
            string? title,
            CancellationToken ct = default)
        {
            int running = Interlocked.Increment(ref _running);
            int seen;
            while ((seen = Volatile.Read(ref _maxConcurrent)) < running
                   && Interlocked.CompareExchange(ref _maxConcurrent, running, seen) != seen)
            {
            }

            if (running >= expected)
            {
                _reached.TrySetResult();
            }

            try
            {
                await Task.WhenAny(_reached.Task, Task.Delay(Fallback, ct));

                // Held a moment past the release, so a lookup started beyond the cap would be
                // seen running together with the others rather than after them.
                await Task.Delay(Hold, ct);
                return new CredentialResult("", "s3cr3t");
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private sealed class NullTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
            => Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));

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

        public string? LastErrorMessage { get; private set; }

        public void ShowWarning(string title, string message) => WarningCount++;

        public void ShowError(string title, string message)
        {
            ErrorCount++;
            LastErrorMessage = message;
        }

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
