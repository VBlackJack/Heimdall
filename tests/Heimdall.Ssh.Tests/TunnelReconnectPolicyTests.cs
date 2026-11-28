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

using System.Net.Sockets;
using Heimdall.Ssh.Agents;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.Ssh.Tests;

public sealed class TunnelReconnectPolicyTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    [InlineData(9, 32)]
    public void GetBackoffDelay_UsesBoundedExponentialSchedule(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TunnelReconnectPolicy.GetBackoffDelay(attempt));
    }

    [Fact]
    public void ShouldRetry_FailsClosedOnHostKeyAuthRevokedAndCancellation_RetriesTransientDrops()
    {
        Assert.False(TunnelReconnectPolicy.ShouldRetry(null, SshFailureCode.HostKeyMismatch));
        Assert.False(TunnelReconnectPolicy.ShouldRetry(null, SshFailureCode.HostKeyRevoked));
        Assert.False(TunnelReconnectPolicy.ShouldRetry(null, SshFailureCode.AuthRejected));
        Assert.False(TunnelReconnectPolicy.ShouldRetry(null, SshFailureCode.AgentSigningDeclined));
        Assert.False(TunnelReconnectPolicy.ShouldRetry(null, SshFailureCode.Cancelled));

        Assert.False(TunnelReconnectPolicy.ShouldRetry(new HostKeyRejectedException("gw", 22, "ssh-ed25519", "a", "b")));
        Assert.False(TunnelReconnectPolicy.ShouldRetry(new SshAuthenticationException("bad auth")));
        Assert.False(TunnelReconnectPolicy.ShouldRetry(new SshAgentKeyNotLoadedException("SHA256:abc")));
        Assert.False(TunnelReconnectPolicy.ShouldRetry(new OperationCanceledException()));

        Assert.True(TunnelReconnectPolicy.ShouldRetry(null, SshFailureCode.TunnelBroken));
        Assert.True(TunnelReconnectPolicy.ShouldRetry(null, SshFailureCode.SessionDisconnected));
        Assert.True(TunnelReconnectPolicy.ShouldRetry(new SocketException((int)SocketError.ConnectionReset)));
    }

    [Fact]
    public void TunnelSession_TracksLiveConnectionCountAndActivityTimestamp()
    {
        using var client = new SshClient("example.invalid", 22, "ops", "pw");
        var info = TunnelManager.BuildTunnelInfo("bastion", 15432, "db.internal", 5432, 0, 0, 0, "Bastion");
        using var session = new TunnelSession(client, null, info);

        Assert.Equal(0, session.TotalConnections);
        Assert.Null(session.LastActivityUtc);

        session.RecordConnectionActivity();
        session.RecordConnectionActivity();

        Assert.Equal(2, session.TotalConnections);
        Assert.NotNull(session.LastActivityUtc);
    }
}
