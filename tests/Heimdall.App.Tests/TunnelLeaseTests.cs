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
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class TunnelLeaseTests
{
    private const int Port = 49171;
    private const long LeaseId = 7;

    [Fact]
    public void Dispose_ReleasesOnce_EvenWhenCalledTwice()
    {
        RecordingTunnelService tunnel = new();
        TunnelLease lease = new(tunnel, usesTunnel: true, Port, LeaseId);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal((Port, LeaseId), Assert.Single(tunnel.Releases));
    }

    [Fact]
    public void Dispose_AfterTransfer_ReleasesNothing()
    {
        RecordingTunnelService tunnel = new();
        TunnelLease lease = new(tunnel, usesTunnel: true, Port, LeaseId);

        lease.Transfer();
        lease.Dispose();

        Assert.Empty(tunnel.Releases);
    }

    [Fact]
    public void Release_AfterTransfer_ReleasesOnce()
    {
        RecordingTunnelService tunnel = new();
        TunnelLease lease = new(tunnel, usesTunnel: true, Port, LeaseId);

        lease.Transfer();
        lease.Release();
        lease.Release();
        lease.Dispose();

        Assert.Equal((Port, LeaseId), Assert.Single(tunnel.Releases));
    }

    [Theory]
    [InlineData(false, Port)]
    [InlineData(true, 0)]
    public void Dispose_WithoutATunnelOrAPort_ReleasesNothing(bool usesTunnel, int port)
    {
        RecordingTunnelService tunnel = new();
        TunnelLease lease = new(tunnel, usesTunnel, port, LeaseId);

        lease.Dispose();

        Assert.Empty(tunnel.Releases);
    }

    private sealed class RecordingTunnelService : ITunnelService
    {
        public List<(int Port, long LeaseId)> Releases { get; } = [];

        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false) => throw new NotSupportedException();

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort) => throw new NotSupportedException();

        public void ReleaseTunnelReference(int localPort, long leaseId) => Releases.Add((localPort, leaseId));
    }
}
