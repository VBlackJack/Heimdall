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

using Renci.SshNet.Common;

namespace Heimdall.Ssh.Tests;

public sealed class GatewayRouteDiagnosticTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly SshConnectionParams[] Chain =
    [
        new() { Host = "root.invalid", Username = "audit" },
        new() { Host = "child.invalid", Username = "audit" },
        new() { Host = "third.invalid", Username = "audit" }
    ];

    [Fact]
    public async Task Success_UsesOrderedHopsThenDestinationAndDisposes()
    {
        FakeSession session = new();
        IReadOnlyList<GatewayDiagnosticStep> result = await Run(session, target: "destination.invalid");
        Assert.Equal(new[] { "root.invalid", "child.invalid", "third.invalid", "destination.invalid" }, session.Visited);
        Assert.Equal(4, result.Count);
        Assert.All(result, step => Assert.True(step.Success));
        Assert.True(result[^1].IsTarget);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task MissingPin_StopsBeforeConnectingAffectedHop()
    {
        FakeSession session = new();
        IReadOnlyList<GatewayDiagnosticStep> result = await GatewayRouteDiagnostic.RunAsync(
            Chain, hop => hop.Host == "child.invalid" ? null : "pin", TestTimeout, null, 0, null, default, () => session);
        Assert.Equal(new[] { "root.invalid" }, session.Visited);
        Assert.Equal(SshFailureCode.HostKeyUnavailable, result[^1].Failure);
        Assert.Equal(2, result[^1].Hop);
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_StopsBeforeLaterHopsAndDoesNotExposeException(bool authentication)
    {
        FakeSession session = new()
        {
            FailAt = 2,
            Failure = authentication
            ? new SshAuthenticationException("password=secret-do-not-report")
            : new HostKeyRejectedException("child.invalid", 22, "ssh-ed25519", "new", "old")
        };
        IReadOnlyList<GatewayDiagnosticStep> result = await Run(session, "destination.invalid");
        Assert.Equal(2, result.Count);
        Assert.Equal(authentication ? SshFailureCode.AuthRejected : SshFailureCode.HostKeyMismatch, result[^1].Failure);
        Assert.DoesNotContain("secret-do-not-report", result[^1].ToString());
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task CancelledBeforeStart_DoesNotDial()
    {
        FakeSession session = new();
        using CancellationTokenSource ct = new();
        ct.Cancel();
        IReadOnlyList<GatewayDiagnosticStep> result = await GatewayRouteDiagnostic.RunAsync(
            Chain, _ => "pin", TestTimeout, null, 0, null, ct.Token, () => session);
        Assert.Empty(session.Visited);
        Assert.Equal(SshFailureCode.Cancelled, Assert.Single(result).Failure);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task StepDeadline_StopsBlockedTransport()
    {
        FakeSession session = new() { Block = true };
        IReadOnlyList<GatewayDiagnosticStep> result = await GatewayRouteDiagnostic.RunAsync(
            Chain, _ => "pin", TimeSpan.FromMilliseconds(30), null, 0, null, default, () => session);
        Assert.Equal(SshFailureCode.NetworkTimedOut, Assert.Single(result).Failure);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task TargetRefusal_IsNotReportedAsSuccessfulTunnel()
    {
        FakeSession session = new() { FailTarget = true };
        IReadOnlyList<GatewayDiagnosticStep> result = await Run(session, "destination.invalid");
        Assert.Equal(4, result.Count);
        Assert.All(result.Take(3), step => Assert.True(step.Success));
        Assert.Equal(SshFailureCode.ForwardingFailed, result[^1].Failure);
        Assert.True(result[^1].IsTarget);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task SocksReply_RequiresRemoteChannelConfirmation(byte status, bool success)
    {
        using ScriptStream stream = new([5, 0, 5, status, 0, 1, 0, 0, 0, 0, 0, 0]);
        Task probe = SshGatewayDiagnosticSession.ConfirmSocksTargetAsync(stream, "internal.invalid", 443, default);
        if (success) await probe;
        else await Assert.ThrowsAsync<ProxyException>(() => probe);
        byte[] written = stream.Written.ToArray();
        Assert.Equal(new byte[] { 5, 1, 0 }, written.Take(3));
        Assert.Equal((byte)3, written[6]); // DNS remains remote: SOCKS domain address.
    }

    [Fact]
    public async Task TruncatedSocksReply_DoesNotPass()
    {
        using ScriptStream stream = new([5, 0, 5, 0, 0, 1]);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            SshGatewayDiagnosticSession.ConfirmSocksTargetAsync(stream, "internal.invalid", 443, default));
    }

    [Fact]
    public async Task DestinationBannerBeforeProxyReply_ProvesRemoteTcpAccess()
    {
        // SSH.NET can forward server-first protocol data before its own SOCKS reply.
        using ScriptStream stream = new([5, 0, (byte)'S', (byte)'S', (byte)'H', (byte)'-']);
        await SshGatewayDiagnosticSession.ConfirmSocksTargetAsync(stream, "internal.invalid", 22, default);
    }

    private static Task<IReadOnlyList<GatewayDiagnosticStep>> Run(FakeSession session, string? target = null) =>
        GatewayRouteDiagnostic.RunAsync(Chain, _ => "pin", TestTimeout, target, 443, null, default, () => session);

    private sealed class FakeSession : IGatewayDiagnosticSession
    {
        public List<string> Visited { get; } = [];
        public bool Disposed { get; private set; }
        public int FailAt { get; init; }
        public Exception? Failure { get; init; }
        public bool Block { get; init; }
        public bool FailTarget { get; init; }
        public async Task ConnectHopAsync(SshConnectionParams hop, string fingerprint, CancellationToken ct)
        {
            Visited.Add(hop.Host);
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (Visited.Count == FailAt) throw Failure!;
        }
        public Task ProbeTargetAsync(string host, int port, CancellationToken ct)
        {
            Visited.Add(host);
            return FailTarget ? Task.FromException(new ProxyException("synthetic refusal")) : Task.CompletedTask;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class ScriptStream(byte[] response) : MemoryStream(response)
    {
        public MemoryStream Written { get; } = new();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => Written.WriteAsync(buffer, ct);
        protected override void Dispose(bool disposing)
        {
            if (disposing) Written.Dispose();
            base.Dispose(disposing);
        }
    }
}
