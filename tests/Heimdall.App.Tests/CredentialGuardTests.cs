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
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Tests;

public sealed class CredentialGuardTests
{
    [Fact]
    public void MapSecurityServicesRunning_CredentialGuardServiceIdIsActive()
    {
        CredentialGuardStatus result =
            CredentialGuardService.MapSecurityServicesRunning(new uint[] { 2, 1 });

        Assert.Equal(CredentialGuardState.Active, result.State);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void MapSecurityServicesRunning_OtherServiceIdsAreInactive()
    {
        CredentialGuardStatus result =
            CredentialGuardService.MapSecurityServicesRunning(new uint[] { 2, 3 });

        Assert.Equal(CredentialGuardState.Inactive, result.State);
        Assert.Null(result.FailureReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public void MapSecurityServicesRunning_UnusableValueIsIndeterminate(object? rawValue)
    {
        CredentialGuardStatus result =
            CredentialGuardService.MapSecurityServicesRunning(rawValue);

        Assert.Equal(CredentialGuardState.Indeterminate, result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public async Task GetStatusAsync_DefinitiveResultIsCached()
    {
        var queryCount = 0;
        var service = new CredentialGuardService(() =>
        {
            Interlocked.Increment(ref queryCount);
            return new uint[] { 1 };
        });

        CredentialGuardStatus first = await service.GetStatusAsync();
        CredentialGuardStatus second = await service.GetStatusAsync();

        Assert.Equal(CredentialGuardState.Active, first.State);
        Assert.Equal(CredentialGuardState.Active, second.State);
        Assert.Equal(1, queryCount);
    }

    [Fact]
    public async Task GetStatusAsync_IndeterminateResultIsNotCached()
    {
        var queryCount = 0;
        var service = new CredentialGuardService(() =>
        {
            Interlocked.Increment(ref queryCount);
            throw new InvalidOperationException("temporary WMI failure");
        });

        CredentialGuardStatus first = await service.GetStatusAsync();
        CredentialGuardStatus second = await service.GetStatusAsync();

        Assert.Equal(CredentialGuardState.Indeterminate, first.State);
        Assert.Equal(CredentialGuardState.Indeterminate, second.State);
        Assert.Equal(2, queryCount);
    }

    [Fact]
    public async Task ConnectAsync_RequiredAndActiveEmbeddedRdpReachesTunnel()
    {
        var tunnel = new TrackingTunnelService();
        var detector = new StubCredentialGuardService(CredentialGuardState.Active);
        RdpHandler handler = CreateHandler(tunnel, detector);

        ConnectionResult result = await handler.ConnectAsync(
            CreateServer("Embedded"),
            new AppSettings { RequireCredentialGuard = true },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.IsType<RdpSessionResult>(result.Session);
        Assert.Equal(1, detector.CallCount);
        Assert.Equal(1, tunnel.SetupCallCount);
    }

    [Theory]
    [InlineData(CredentialGuardState.Inactive)]
    [InlineData(CredentialGuardState.Indeterminate)]
    public async Task ConnectAsync_RequiredWithoutActiveCredentialGuardBlocksBeforeTunnel(
        CredentialGuardState state)
    {
        var tunnel = new TrackingTunnelService();
        var detector = new StubCredentialGuardService(state);
        RdpHandler handler = CreateHandler(tunnel, detector);

        ConnectionResult result = await handler.ConnectAsync(
            CreateServer("Embedded"),
            new AppSettings { RequireCredentialGuard = true },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Session);
        Assert.Equal(1, detector.CallCount);
        Assert.Equal(0, tunnel.SetupCallCount);
    }

    [Fact]
    public async Task ConnectAsync_UncheckedSettingSkipsDetection()
    {
        var tunnel = new TrackingTunnelService();
        var detector = new StubCredentialGuardService(CredentialGuardState.Inactive);
        RdpHandler handler = CreateHandler(tunnel, detector);

        ConnectionResult result = await handler.ConnectAsync(
            CreateServer("Embedded"),
            new AppSettings { RequireCredentialGuard = false },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, detector.CallCount);
        Assert.Equal(1, tunnel.SetupCallCount);
    }

    [Fact]
    public async Task ConnectAsync_RequiredExternalRdpNeverChecksCredentialGuard()
    {
        var tunnel = new TrackingTunnelService();
        var detector = new StubCredentialGuardService(CredentialGuardState.Inactive);
        var launcher = new StubRdpExternalClientLauncher();
        RdpHandler handler = CreateHandler(tunnel, detector, launcher);

        ConnectionResult result = await handler.ConnectAsync(
            CreateServer("Embedded"),
            new AppSettings
            {
                RequireCredentialGuard = true,
                RdpArtifactCleanupDelayMs = 1,
                RdpCredentialAutofillTimeoutMs = 1
            },
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.True(result.Success);
        Assert.Equal(0, detector.CallCount);
        Assert.Equal(1, tunnel.SetupCallCount);
        Assert.Equal(1, launcher.LaunchCallCount);
    }

    [Fact]
    public void ServerListPreGuardsRunBeforeCredentialPreparation()
    {
        string singleSource = ReadRepoFile(
            "src", "Heimdall.App", "ViewModels", "ServerListViewModel.cs");
        string singleConnect = SliceFrom(singleSource, "private async Task<bool> ConnectCoreAsync");
        int singleGuard = singleConnect.IndexOf("EnforceCredentialGuardAsync(", StringComparison.Ordinal);
        int windowsHello = singleConnect.IndexOf("EnsureWindowsHelloAsync(", StringComparison.Ordinal);
        int credentialProvider = singleConnect.IndexOf(
            "TryResolveExternalCredentialsAsync(",
            StringComparison.Ordinal);

        Assert.True(singleGuard >= 0);
        Assert.True(singleGuard < windowsHello);
        Assert.True(singleGuard < credentialProvider);

        string bulkSource = ReadRepoFile(
            "src", "Heimdall.App", "ViewModels", "ServerListViewModel.Bulk.cs");
        string bulkPreparation = SliceFrom(
            bulkSource,
            "internal async Task<BulkConnectPlan> PrepareBulkConnectPlanAsync");
        int bulkGuard = bulkPreparation.IndexOf("EnforceCredentialGuardAsync(", StringComparison.Ordinal);
        int bulkCredentialProvider = bulkPreparation.IndexOf(
            "TryResolveExternalCredentialsAsync(",
            StringComparison.Ordinal);

        Assert.True(bulkGuard >= 0);
        Assert.True(bulkGuard < bulkCredentialProvider);
        Assert.Contains("showMessage: !credentialGuardMessageShown", bulkPreparation, StringComparison.Ordinal);
    }

    private static RdpHandler CreateHandler(
        ITunnelService tunnel,
        ICredentialGuardService credentialGuardService,
        IRdpExternalClientLauncher? launcher = null) =>
        new(
            tunnel,
            new ConnectionStateMachine(),
            new LocalizationManager(),
            launcher ?? new StubRdpExternalClientLauncher(),
            credentialGuardService);

    private static ServerProfileDto CreateServer(string rdpMode) =>
        new()
        {
            Id = $"credential-guard-{Guid.NewGuid():N}",
            DisplayName = "Credential Guard test",
            RemoteServer = "127.0.0.1",
            RemotePort = 3389,
            ConnectionType = "RDP",
            RdpMode = rdpMode,
            UseDirectConnection = true
        };

    private static string ReadRepoFile(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine([FindRepoRoot(), .. relativeParts]));

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Heimdall repository root.");
    }

    private static string SliceFrom(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method signature: {signature}");
        return source[start..];
    }

    private sealed class StubCredentialGuardService(CredentialGuardState state)
        : ICredentialGuardService
    {
        public int CallCount { get; private set; }

        public Task<CredentialGuardStatus> GetStatusAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            string? failureReason = state is CredentialGuardState.Indeterminate
                ? "temporary WMI failure"
                : null;
            return Task.FromResult(new CredentialGuardStatus(state, failureReason));
        }
    }

    private sealed class TrackingTunnelService : ITunnelService
    {
        public int SetupCallCount { get; private set; }

        public Task<(bool Success, bool UsesTunnel, string Host, int Port, string? ErrorMessage)>
            SetupTunnelIfNeededAsync(
                ServerProfileDto server,
                int remotePort,
                AppSettings settings,
                CancellationToken ct,
                bool preferDistinctLoopback = false)
        {
            SetupCallCount++;
            return Task.FromResult((
                true,
                false,
                server.RemoteServer,
                remotePort,
                (string?)null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(
            int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class StubRdpExternalClientLauncher : IRdpExternalClientLauncher
    {
        public int LaunchCallCount { get; private set; }

        public ILaunchedRdpClientProcess? Launch(string rdpFilePath)
        {
            LaunchCallCount++;
            return new StubLaunchedRdpClientProcess();
        }
    }

    private sealed class StubLaunchedRdpClientProcess : ILaunchedRdpClientProcess
    {
        public int Id => 4242;

        public int ExitCode => 0;

        public bool EnableRaisingEvents { get; set; }

        public event EventHandler Exited
        {
            add
            {
            }
            remove
            {
            }
        }

        public void Dispose()
        {
        }
    }
}
