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
using Heimdall.App.ViewModels;
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
/// Which connections are allowed to ask the user for a credential, and which are not.
/// </summary>
/// <remarks>
/// This file exists because a mutation found the junction unguarded. Deleting the single
/// line that grants the intent left every handler test green: the handlers set the flag
/// themselves, so they proved the feature worked while proving nothing about whether
/// anything ever turned it on. That is the shape where a guard ships complete, tested,
/// and attached to nothing.
/// </remarks>
public sealed class ServerListCredentialPromptIntentTests
{
    [Fact]
    public async Task ConnectCommand_GrantsThePromptIntent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SeedServerAsync();

        await fixture.ViewModel.ConnectCommand.ExecuteAsync(fixture.ViewModel.Servers[0]);

        ServerProfileDto dispatched = Assert.Single(fixture.Handler.Dispatched);
        Assert.True(
            dispatched.AllowCredentialPrompt,
            "a connection the user asked for may ask something back");
    }

    [Fact]
    public async Task RestoreServerAsync_WithholdsThePromptIntent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SeedServerAsync();

        await fixture.ViewModel.RestoreServerAsync(Fixture.ServerId, CancellationToken.None);

        ServerProfileDto dispatched = Assert.Single(fixture.Handler.Dispatched);

        // A restore reconnects on its own initiative, and it reconnects several sessions.
        // Granting the intent here would raise a modal nobody asked for, once per session,
        // at launch.
        Assert.False(
            dispatched.AllowCredentialPrompt,
            "a session reconnecting on its own must fail quietly");
    }

    /// <summary>Records the profile each connection is dispatched with.</summary>
    private sealed class RecordingSftpHandler : IProtocolHandler
    {
        public List<ServerProfileDto> Dispatched { get; } = [];

        public string Protocol => "SFTP";

        public Task<ConnectionResult> ConnectAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
        {
            Dispatched.Add(server);
            return Task.FromResult(new ConnectionResult(false, "recorded", null));
        }
    }

    /// <summary>No tunnel: the handler is a double, so nothing is ever forwarded.</summary>
    private sealed class NullTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
            => Task.FromResult(
                new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    /// <summary>Runs everything inline; these tests have no dispatcher.</summary>
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();

        public T Invoke<T>(Func<T> function) => function();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> function) => function();

        public bool CheckAccess() => true;
    }

    /// <summary>Never used: these tests import nothing.</summary>
    private sealed class UnusedRdpImportService : IRdpImportService
    {
        public Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<RdpImportResult> ApplyAsync(
            RdpImportPreview preview,
            RdpImportSelection selection,
            CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Tolerates the error notice a failed connection is expected to show, and refuses
    /// everything else.
    /// </summary>
    /// <remarks>
    /// A credential question raised from this level would be a modal nobody asked for -
    /// the prompt belongs inside the handler, where the intent flag gates it - so any
    /// such call has to fail the test rather than pass silently through it.
    /// </remarks>
    private class RefusingDialogProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowError))
            {
                return null;
            }

            throw new NotSupportedException($"unexpected dialog call: {targetMethod?.Name}");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string ServerId = "sftp-intent-test";

        private readonly string _rootPath;

        private Fixture(string rootPath, ServerListViewModel viewModel, RecordingSftpHandler handler, ConfigManager configManager)
        {
            _rootPath = rootPath;
            ViewModel = viewModel;
            Handler = handler;
            ConfigManager = configManager;
        }

        public ServerListViewModel ViewModel { get; }

        public RecordingSftpHandler Handler { get; }

        public ConfigManager ConfigManager { get; }

        public static async Task<Fixture> CreateAsync()
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(), "heimdall-prompt-intent", Guid.NewGuid().ToString("N"));
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();

            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            var handler = new RecordingSftpHandler();
            var connectionService = new ConnectionService(
                configManager, localizer, new NullTunnelService(), [handler]);

            var viewModel = new ServerListViewModel(
                configManager,
                localizer,
                new InlineUiDispatcher(),
                new ConnectionStateMachine(),
                connectionService,
                System.Reflection.DispatchProxy.Create<IDialogService, RefusingDialogProxy>(),
                new UnusedRdpImportService(),
                new PuttySessionImporter(new FakePuttySessionRegistrySource([]), configManager),
                new KnownHostsImporter(configManager, new HostKeyStore()));

            return new Fixture(rootPath, viewModel, handler, configManager);
        }

        public async Task SeedServerAsync()
        {
            await ConfigManager.SaveServersAsync(
            [
                new ServerProfileDto
                {
                    Id = ServerId,
                    DisplayName = "SFTP intent",
                    ConnectionType = "SFTP",
                    RemoteServer = "server01.contoso.local",
                    SshPort = DefaultPorts.Ssh,
                    SshUsername = "operator",
                    UseDirectConnection = true
                }
            ]);

            ViewModel.LoadServers(
                await ConfigManager.LoadServersAsync(),
                await ConfigManager.LoadSettingsAsync());
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
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
