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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class ConnectionServiceDispatchTests
{
    [Fact]
    public async Task RunPreflight_MissingGateway_ReturnsActionableFailure()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall-preflight-missing-gateway",
            Guid.NewGuid().ToString("N"));
        ConfigManager configManager = new ConfigManager(rootPath);

        try
        {
            LocalizationManager localizer = await CreateLocalizerAsync();
            using ConnectionService service = new ConnectionService(
                configManager,
                localizer,
                new StubTunnelService(),
                []);
            ServerProfileDto server = new()
            {
                Id = "missing-gateway-test",
                DisplayName = "Missing Gateway Test",
                ConnectionType = "SSH",
                RemoteServer = "ssh.example.com",
                SshGatewayId = "missing-gateway"
            };

            Heimdall.Ssh.PreflightResult result = service.RunPreflight(server, new AppSettings());

            Assert.False(result.Success);
            Assert.Equal(Heimdall.Ssh.SshFailureCode.Unknown, result.FailureCode);
            Assert.Contains("missing-gateway", result.Message, StringComparison.Ordinal);
            Assert.Contains("Missing Gateway Test", result.Message, StringComparison.Ordinal);
            Assert.Contains("Settings", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectWinRmAsync_DispatchesToWinRmHandler()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall-winrm-dispatch",
            Guid.NewGuid().ToString("N"));
        ConfigManager configManager = new ConfigManager(rootPath);

        try
        {
            await configManager.InitializeAsync();

            LocalizationManager localizer = new LocalizationManager();
            CapturingProtocolHandler handler = new CapturingProtocolHandler("WINRM");
            using ConnectionService service = new ConnectionService(
                configManager,
                localizer,
                new StubTunnelService(),
                new IProtocolHandler[] { handler });
            ServerProfileDto server = new ServerProfileDto
            {
                Id = "winrm-test",
                ConnectionType = "WINRM",
                RemoteServer = "server01.contoso.local"
            };
            AppSettings settings = new AppSettings();

            ConnectionResult result = await service.ConnectWinRmAsync(server, settings);

            Assert.True(result.Success);
            Assert.Equal(1, handler.CallCount);
            Assert.Same(server, handler.Server);
            Assert.Same(settings, handler.Settings);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectLocalShellAsync_UnconfirmedPayloadApproved_DispatchesToHandler()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall-local-dispatch-approved",
            Guid.NewGuid().ToString("N"));
        ConfigManager configManager = new ConfigManager(rootPath);

        try
        {
            await configManager.InitializeAsync();

            LocalizationManager localizer = await CreateLocalizerAsync();
            CapturingProtocolHandler handler = new CapturingProtocolHandler("LOCAL");
            using ConnectionService service = new ConnectionService(
                configManager,
                localizer,
                new StubTunnelService(),
                new IProtocolHandler[] { handler });
            int confirmCalls = 0;
            service.ConfirmExecution = profile =>
            {
                confirmCalls++;
                return Task.FromResult(true);
            };
            ServerProfileDto server = CreateUnconfirmedLocalShellProfile();
            AppSettings settings = new AppSettings();

            ConnectionResult result = await service.ConnectLocalShellAsync(server, settings);

            Assert.True(result.Success);
            Assert.Equal(1, confirmCalls);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectLocalShellAsync_UnconfirmedPayloadDeclined_BlocksHandler()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall-local-dispatch-declined",
            Guid.NewGuid().ToString("N"));
        ConfigManager configManager = new ConfigManager(rootPath);

        try
        {
            await configManager.InitializeAsync();

            LocalizationManager localizer = await CreateLocalizerAsync();
            CapturingProtocolHandler handler = new CapturingProtocolHandler("LOCAL");
            using ConnectionService service = new ConnectionService(
                configManager,
                localizer,
                new StubTunnelService(),
                new IProtocolHandler[] { handler });
            service.ConfirmExecution = profile => Task.FromResult(false);
            ServerProfileDto server = CreateUnconfirmedLocalShellProfile();
            AppSettings settings = new AppSettings();

            ConnectionResult result = await service.ConnectLocalShellAsync(server, settings);

            Assert.False(result.Success);
            Assert.Equal(0, handler.CallCount);
            Assert.Equal(localizer["StatusExecutionBlockedUnconfirmed"], result.ErrorMessage);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectLocalShellAsync_UnconfirmedPayloadWithoutDelegate_FailsClosed()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall-local-dispatch-failclosed",
            Guid.NewGuid().ToString("N"));
        ConfigManager configManager = new ConfigManager(rootPath);

        try
        {
            await configManager.InitializeAsync();

            LocalizationManager localizer = await CreateLocalizerAsync();
            CapturingProtocolHandler handler = new CapturingProtocolHandler("LOCAL");
            using ConnectionService service = new ConnectionService(
                configManager,
                localizer,
                new StubTunnelService(),
                new IProtocolHandler[] { handler });
            ServerProfileDto server = CreateUnconfirmedLocalShellProfile();
            AppSettings settings = new AppSettings();

            ConnectionResult result = await service.ConnectLocalShellAsync(server, settings);

            Assert.False(result.Success);
            Assert.Equal(0, handler.CallCount);
            Assert.Equal(localizer["StatusExecutionBlockedUnconfirmed"], result.ErrorMessage);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectLocalShellAsync_ConfirmedPayload_SkipsConfirmation()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall-local-dispatch-confirmed",
            Guid.NewGuid().ToString("N"));
        ConfigManager configManager = new ConfigManager(rootPath);

        try
        {
            await configManager.InitializeAsync();

            LocalizationManager localizer = await CreateLocalizerAsync();
            CapturingProtocolHandler handler = new CapturingProtocolHandler("LOCAL");
            using ConnectionService service = new ConnectionService(
                configManager,
                localizer,
                new StubTunnelService(),
                new IProtocolHandler[] { handler });
            int confirmCalls = 0;
            service.ConfirmExecution = profile =>
            {
                confirmCalls++;
                return Task.FromResult(false);
            };
            ServerProfileDto server = CreateUnconfirmedLocalShellProfile();
            server.ExecutionConfirmed = true;
            AppSettings settings = new AppSettings();

            ConnectionResult result = await service.ConnectLocalShellAsync(server, settings);

            Assert.True(result.Success);
            Assert.Equal(0, confirmCalls);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectWinRmAsync_NonLocalPayload_SkipsConfirmation()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall-winrm-dispatch-nonlocal",
            Guid.NewGuid().ToString("N"));
        ConfigManager configManager = new ConfigManager(rootPath);

        try
        {
            await configManager.InitializeAsync();

            LocalizationManager localizer = await CreateLocalizerAsync();
            CapturingProtocolHandler handler = new CapturingProtocolHandler("WINRM");
            using ConnectionService service = new ConnectionService(
                configManager,
                localizer,
                new StubTunnelService(),
                new IProtocolHandler[] { handler });
            int confirmCalls = 0;
            service.ConfirmExecution = profile =>
            {
                confirmCalls++;
                return Task.FromResult(false);
            };
            ServerProfileDto server = new ServerProfileDto
            {
                Id = "winrm-with-local-fields",
                ConnectionType = "WINRM",
                RemoteServer = "server01.contoso.local",
                LocalShellExecutable = "evil.exe"
            };
            AppSettings settings = new AppSettings();

            ConnectionResult result = await service.ConnectWinRmAsync(server, settings);

            Assert.True(result.Success);
            Assert.Equal(0, confirmCalls);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private static ServerProfileDto CreateUnconfirmedLocalShellProfile()
    {
        return new ServerProfileDto
        {
            Id = "local-test",
            DisplayName = "Imported Local",
            ConnectionType = "LOCAL",
            RemoteServer = "localhost",
            LocalShellExecutable = "evil.exe"
        };
    }

    private sealed class CapturingProtocolHandler(string protocol) : IProtocolHandler
    {
        public string Protocol { get; } = protocol;

        public int CallCount { get; private set; }

        public ServerProfileDto? Server { get; private set; }

        public AppSettings? Settings { get; private set; }

        public Task<ConnectionResult> ConnectAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
        {
            CallCount++;
            Server = server;
            Settings = settings;
            return Task.FromResult(new ConnectionResult(true, null, null));
        }
    }

    private sealed class StubTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
            => Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer ?? string.Empty, remotePort, (string?)null, null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort)
            => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }
}
