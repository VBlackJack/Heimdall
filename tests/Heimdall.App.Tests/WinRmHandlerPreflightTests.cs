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
using System.Net.Sockets;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.WinRm;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Tests;

public sealed class WinRmHandlerPreflightTests
{
    [Fact]
    public async Task ConnectAsync_WhenPreflightFails_ReturnsFailure()
    {
        WinRmPreflight preflight = new WinRmPreflight(
            tcpProbe: DnsFailureProbeAsync);
        WinRmHandler handler = new WinRmHandler(
            new PassThroughTunnelService(),
            new ConnectionStateMachine(),
            new LocalizationManager(),
            preflight,
            bootstrapJanitor: CreateNoOpJanitor());

        ConnectionResult result = await handler.ConnectAsync(
            CreateServer(),
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task ConnectAsync_WithInvalidHost_ReturnsLocalizedFrenchConfigurationFailure()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        WinRmHandler handler = new WinRmHandler(
            new PassThroughTunnelService(),
            new ConnectionStateMachine(),
            localizer,
            bootstrapJanitor: CreateNoOpJanitor());
        ServerProfileDto server = CreateServer();
        server.RemoteServer = "bad host; Remove-Item";

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("L'h\u00f4te WinRM est invalide.", result.ErrorMessage);
        Assert.DoesNotContain("Invalid WinRM host", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(result.Session);
    }

    private static Task DnsFailureProbeAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken token)
    {
        throw new SocketException((int)SocketError.HostNotFound);
    }

    private static ServerProfileDto CreateServer()
        => new ServerProfileDto
        {
            Id = "winrm-preflight-test",
            DisplayName = "WinRM Preflight Test",
            ConnectionType = "WINRM",
            RemoteServer = "server01.contoso.local",
            WinRmPort = DefaultPorts.WinRmHttp,
            WinRmUseSsl = false,
            WinRmIdentityMode = WinRmIdentityMode.CurrentUser
        };

    private static WinRmBootstrapJanitor CreateNoOpJanitor()
    {
        return new WinRmBootstrapJanitor(enumerateScripts: _ => Array.Empty<string>());
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private sealed class PassThroughTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            return Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }
}
