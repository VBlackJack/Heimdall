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


using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Renci.SshNet;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshGatewayHardeningTests : IDisposable
{
    private readonly CredentialProtectorStateScope _credentialState = new();

    public void Dispose() => _credentialState.Dispose();

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("user")]
    [InlineData("key")]
    [InlineData("password")]
    [InlineData("passphrase")]
    [InlineData("legacy")]
    public void ConnectionEditsAtAnyHopChangeReuseIdentity(string field)
    {
        SshGatewayDto original = Gateway();
        SshGatewayDto edited = original.CloneFaithfully();
        switch (field)
        {
            case "host": edited.Host = "other.example.test"; break;
            case "port": edited.Port = 2222; break;
            case "user": edited.User = "other-account"; break;
            case "key": edited.KeyPath = "other-key"; break;
            case "password": edited.SshPasswordEncrypted = "synthetic-ciphertext"; break;
            case "passphrase": edited.SshKeyPassphraseEncrypted = "synthetic-passphrase"; break;
            case "legacy": edited.SshKeyPassphraseEncrypted = ""; break;
        }
        SshGatewayDto child = new() { Id = "child", Host = "child.example.test", User = "user" };
        Assert.NotEqual(TunnelService.BuildGatewayChainKey([original, child]), TunnelService.BuildGatewayChainKey([edited, child]));
        Assert.NotEqual(TunnelService.BuildGatewayChainKey([child, original]), TunnelService.BuildGatewayChainKey([child, edited]));
    }

    [Fact]
    public async Task EditedGatewayStartsANewDialInsteadOfAcquiringOldTunnel()
    {
        string? dialHost = null;
        using TunnelManager manager = new(
            (parameters, host, port, store, verifier, token) =>
            {
                dialHost = host;
                throw new InvalidOperationException("Stop before network dial");
            }, SshConnectionFactory.CreateSshClient,
            (_, _, _, _, _, _) => throw new InvalidOperationException("Unexpected client connection"));
        SshGatewayDto original = Gateway();
        original.SshPasswordEncrypted = Heimdall.Core.Security.CredentialProtector.Protect("synthetic");
        TunnelInfo info = TunnelManager.BuildTunnelInfo(original.Host, 51321, "target.example.test", 3389,
            0, 0, 0, "Original", gatewayChainKey: TunnelService.BuildGatewayChainKey([original]));
        manager.TryRegisterExternalTunnel(info, new Handle(), () => true);
        SshGatewayDto edited = original.CloneFaithfully();
        edited.Host = "new.example.test";
        TunnelService service = new(manager, new HostKeyStore(), Trust(), new ConnectionStateMachine(),
            new LocalizationManager(), RejectingHostKeyVerifier.Instance);
        ServerProfileDto server = new() { Id = "server", SshGatewayId = original.Id, RemoteServer = info.RemoteHost, UseDirectConnection = false };
        TunnelSetupOutcome result = await service.SetupTunnelIfNeededAsync(server, 3389,
            new AppSettings { SshGateways = [edited] }, CancellationToken.None);
        Assert.False(result.ReusedExistingTunnel);
        Assert.Equal(edited.Host, dialHost);
        Assert.True(manager.ReleaseReference(info.LocalPort));
    }

    [Fact]
    public void RenameKeepsReuseButAgentPreferenceDoesNot()
    {
        SshGatewayDto original = Gateway();
        SshGatewayDto renamed = original.CloneFaithfully();
        renamed.Name = "New display name";
        Assert.Equal(TunnelService.BuildGatewayChainKey([original]), TunnelService.BuildGatewayChainKey([renamed]));
        SshAgentPreference alternative = Enum.GetValues<SshAgentPreference>().First(value => value != SshAgentPreference.AutoOpenSshFirst);
        Assert.NotEqual(TunnelService.BuildGatewayChainKey([original]), TunnelService.BuildGatewayChainKey([original], alternative));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledReuseDoesNotRetainAnotherReference(bool cancelDuringAcquisition)
    {
        using TunnelManager manager = new();
        using CancellationTokenSource cancellation = new();
        SshGatewayDto gateway = Gateway();
        TunnelInfo info = TunnelManager.BuildTunnelInfo(gateway.Host, 51321, "target.example.test", 3389,
            0, 0, 0, "Original", gatewayChainKey: TunnelService.BuildGatewayChainKey([gateway]));
        Handle handle = new();
        Assert.True(manager.TryRegisterExternalTunnel(info, handle, () =>
        {
            if (cancelDuringAcquisition) cancellation.Cancel();
            return true;
        }));
        if (!cancelDuringAcquisition) cancellation.Cancel();
        TunnelService service = new(manager, new HostKeyStore(), Trust(), new ConnectionStateMachine(),
            new LocalizationManager(), RejectingHostKeyVerifier.Instance);
        ServerProfileDto server = new() { Id = "server", SshGatewayId = gateway.Id, RemoteServer = info.RemoteHost, UseDirectConnection = false };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetupTunnelIfNeededAsync(
            server, 3389, new AppSettings { SshGateways = [gateway] }, cancellation.Token));
        Assert.True(manager.ReleaseReference(info.LocalPort));
        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public async Task ZeroDelayCancellationReleasesRegisteredTunnel()
    {
        using TunnelManager manager = new();
        Handle handle = new();
        TunnelInfo info = TunnelManager.BuildTunnelInfo("gateway", 51321, "target", 3389, 0, 0, 0, null);
        manager.TryRegisterExternalTunnel(info, handle, () => true);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TunnelService.WaitForTunnelEstablishmentOrReleaseAsync(
            manager, info.LocalPort, 0, TimeProvider.System, cancellation.Token));
        Assert.False(manager.HasTunnel(info.LocalPort));
        Assert.Equal(1, handle.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChainedEntryPointAppliesKeepaliveBeforeDial(bool multipleHops)
    {
        double interval = 0;
        using TunnelManager manager = new(
            (parameters, host, port, store, verifier, token) => Task.FromResult(new PinnedFingerprintVerifier(host, port, "synthetic")),
            parameters => new SshClient(new ConnectionInfo(parameters.Host, parameters.Port, parameters.Username, new NoneAuthenticationMethod(parameters.Username))),
            (client, host, port, verifier, token, message) =>
            {
                interval = client.KeepAliveInterval.TotalSeconds;
                throw new InvalidOperationException("Stop before network dial");
            });
        SshConnectionParams hop = Hop("root");
        await manager.OpenChainedTunnelAsync(multipleHops ? [hop, Hop("child")] : [hop],
            "target", 3389, 0, new HostKeyStore(), RejectingHostKeyVerifier.Instance, keepAliveIntervalSeconds: 41);
        Assert.Equal(41, interval);
    }

    [Fact]
    public async Task ToolConnectionOwnsParentRouteAndPinsTheLogicalTarget()
    {
        Handle route = new();
        TrackingClient? client = null;
        SshConnectionParams root = Hop("root");
        SshConnectionParams child = Hop("child");
        SshConnectionParams? dial = null;
        SshClient connected = await ToolGatewayConnector.ConnectCoreAsync([root, child], Trust(root, child),
            new LocalizationManager(), 41, CancellationToken.None,
            openParents: async (parents, target, verifier, interval, token) =>
            {
                Assert.Single(parents);
                Assert.Same(root, parents[0]);
                Assert.Same(child, target);
                Assert.Equal(41, interval);
                Assert.Equal(HostKeyDecision.Reject, await verifier.VerifyAsync(root.Host, root.Port, "ssh-ed25519", "wrong", null, token));
                return new ToolGatewayConnector.ParentRoute("127.0.0.1", 51322, route);
            },
            createClient: (parameters, lifetime) =>
            {
                dial = parameters;
                client = new TrackingClient(lifetime);
                return client;
            }, connectClient: (_, _) => Task.CompletedTask);
        Assert.Equal("127.0.0.1", dial!.Host);
        Assert.Equal(child.Host, dial.LogicalHost);
        Assert.Equal(child.Port, dial.LogicalPort);
        Assert.Equal(41, connected.KeepAliveInterval.TotalSeconds);
        Assert.Equal(0, route.DisposeCount);
        connected.Dispose();
        Assert.Equal(1, client!.DisposeCount);
        Assert.Equal(1, route.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolFailureDisposesEitherUntransferredRouteOrOwnedClient(bool factoryFails)
    {
        Handle route = new();
        TrackingClient? client = null;
        SshConnectionParams root = Hop("root");
        SshConnectionParams child = Hop("child");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ToolGatewayConnector.ConnectCoreAsync([root, child], Trust(root, child),
            new LocalizationManager(), 41, CancellationToken.None,
            openParents: (_, _, _, _, _) => Task.FromResult(new ToolGatewayConnector.ParentRoute("127.0.0.1", 51322, route)),
            createClient: (_, lifetime) =>
            {
                if (factoryFails) throw new InvalidOperationException("Factory failure");
                client = new TrackingClient(lifetime);
                return client;
            }, connectClient: (_, _) => throw new InvalidOperationException("Handshake failure")));
        Assert.Equal(1, route.DisposeCount);
        if (!factoryFails) Assert.Equal(1, client!.DisposeCount);
    }

    [Fact]
    public async Task MissingTrustRefusesBeforeCreatingAnyConnection()
    {
        bool created = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ToolGatewayConnector.ConnectCoreAsync([Hop("root")], Trust(),
            new LocalizationManager(), 41, CancellationToken.None,
            createClient: (_, _) => { created = true; throw new InvalidOperationException(); }));
        Assert.False(created);
    }

    [Fact]
    public async Task ToolHandshakeObservesCancellationAndDisposesClient()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SshConnectionParams hop = Hop("root");
        TrackingClient client = new(null);
        Task<SshClient> pending = ToolGatewayConnector.ConnectCoreAsync([hop], Trust(hop), new LocalizationManager(), 41,
            cancellation.Token, createClient: (_, _) => client,
            connectClient: async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        await entered.Task;
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public void ProductionSshClientReleasesParentRouteOnce()
    {
        Handle route = new();
        SshClient client = SshConnectionFactory.CreateSshClient(Hop("root"), route);
        client.Dispose();
        client.Dispose();
        Assert.Equal(1, route.DisposeCount);
    }

    private static SshGatewayDto Gateway() => new() { Id = "gateway", Host = "gateway.example.test", User = "user" };
    private static SshConnectionParams Hop(string name) => new() { Host = name + ".example.test", Username = "audit", Password = "synthetic" };
    private static HostKeyTrustService Trust(params SshConnectionParams[] hops)
    {
        HostKeyTrustService trust = new(new HostKeyStore());
        foreach (SshConnectionParams hop in hops) trust.TrustForSession(hop.Host, hop.Port, "synthetic", "ssh-ed25519");
        return trust;
    }
    private sealed class Handle : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
    private sealed class TrackingClient(IDisposable? lifetime) : SshClient(new ConnectionInfo("unused", "audit", new NoneAuthenticationMethod("audit")))
    {
        public int DisposeCount { get; private set; }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && DisposeCount++ == 0) lifetime?.Dispose();
        }
    }
}
