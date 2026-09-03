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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.Plink;
using Microsoft.Extensions.Time.Testing;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// The global <see cref="FileLogger"/> has one instance, so a class that
/// re-points it must not run beside another that is reading its own log.
/// </summary>
[CollectionDefinition(TunnelFailureLogFileCollection.Name, DisableParallelization = true)]
public sealed class TunnelFailureLogFileCollection
{
    public const string Name = "Tunnel failure log file";
}

/// <summary>
/// What the coalescer actually costs and buys, measured on the log file rather
/// than on the injected writer every other test in this area uses.
/// </summary>
/// <remarks>
/// The class documentation used to say ten identical failures produce one line.
/// They do not, and no test could see it: the writer is a test seam, and both of
/// its branches reach one <c>FileLogger</c> queue and one file, Debug included.
/// What coalescing buys is a severity - one Error to find, the repeats demoted
/// to Debug with their text intact - and that is what is pinned here, from the
/// bytes on disk.
/// </remarks>
[Collection(TunnelFailureLogFileCollection.Name)]
public sealed class TunnelFailureLogFileTests : IDisposable
{
    private const string RelayedServerRefusal = "Permission denied (password).";
    private const string FailureReportPrefix = "Tunnel failed for ";

    private readonly string _logDirectory;
    private readonly string _keyFilePath;
    private readonly string _gatewayId = Guid.NewGuid().ToString("N");

    public TunnelFailureLogFileTests()
    {
        _logDirectory = Path.Combine(Path.GetTempPath(), $"heimdall-log-{Guid.NewGuid():N}");
        _keyFilePath = Path.Combine(Path.GetTempPath(), $"heimdall-gateway-{Guid.NewGuid():N}.pem");
        File.WriteAllText(_keyFilePath, "not parsed: the SSH client factory is replaced in these tests");

        FileLogger.SetEnabled(true);
        FileLogger.Initialize(_logDirectory, flushIntervalMs: 60000);
    }

    public void Dispose()
    {
        FileLogger.SetEnabled(true);
        try
        {
            File.Delete(_keyFilePath);
            Directory.Delete(_logDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test over.
        }
    }

    // Ten profiles reconnecting through one refusing gateway. Every one of them
    // is written: coalescing demotes the repeats, it does not withhold them, and
    // the log file is where that is visible.
    [Fact]
    public async Task TenIdenticalFailures_WriteTenLines_OfWhichExactlyOneIsAnError()
    {
        Harness harness = new Harness(_keyFilePath, _gatewayId);

        for (int profile = 0; profile < 10; profile++)
        {
            await harness.ConnectAsync($"server-{profile}");
            harness.Clock.Advance(TimeSpan.FromMilliseconds(200));
        }

        IReadOnlyList<string> lines = ReadFailureLines();

        Assert.Equal(10, lines.Count);
        Assert.Single(lines, line => line.Contains("[ERROR]", StringComparison.Ordinal));
        Assert.Equal(9, lines.Count(line => line.Contains("[DEBUG]", StringComparison.Ordinal)));
    }

    // The reason the repeats are written at all: a reader holding nothing but
    // the file can still say why attempt seven failed.
    [Fact]
    public async Task EveryDemotedRepeat_CarriesTheRefusalTextInFull()
    {
        Harness harness = new Harness(_keyFilePath, _gatewayId);

        for (int profile = 0; profile < 10; profile++)
        {
            await harness.ConnectAsync($"server-{profile}");
            harness.Clock.Advance(TimeSpan.FromMilliseconds(200));
        }

        IReadOnlyList<string> demoted = ReadFailureLines()
            .Where(line => line.Contains("[DEBUG]", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(9, demoted.Count);
        Assert.All(demoted, line => Assert.Contains(RelayedServerRefusal, line, StringComparison.Ordinal));
    }

    // The counterpart, and the positive control for the count above: ten
    // failures that are not identical are ten Error lines, so the single Error
    // is a consequence of sameness and not of anything swallowing output.
    [Fact]
    public async Task TenFailuresOnDistinctGateways_AreTenErrorLines()
    {
        Harness harness = new Harness(_keyFilePath, _gatewayId);

        for (int profile = 0; profile < 10; profile++)
        {
            await harness.ConnectAsync($"server-{profile}", gatewaySuffix: profile);
            harness.Clock.Advance(TimeSpan.FromMilliseconds(200));
        }

        IReadOnlyList<string> lines = ReadFailureLines();

        Assert.Equal(10, lines.Count);
        Assert.Equal(10, lines.Count(line => line.Contains("[ERROR]", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The failure-report lines this test's own gateway produced. Two filters,
    /// both needed: the logger is global, so the file can hold lines written by
    /// anything else running at the same time, and this gateway's own id also
    /// appears in the "Establish tunnel" line written before every attempt.
    /// Both report shapes - the full one and the demoted repeat - open with
    /// <c>Tunnel failed for</c>.
    /// </summary>
    private IReadOnlyList<string> ReadFailureLines()
    {
        FileLogger.Flush();
        string path = Directory.EnumerateFiles(_logDirectory, "heimdall_*.log").Single();
        return File.ReadAllLines(path)
            .Where(line => line.Contains(_gatewayId, StringComparison.Ordinal)
                && line.Contains(FailureReportPrefix, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// A tunnel service whose dial always refuses, writing through the shipped
    /// log writer rather than a test seam. That is the point: the seam is what
    /// hid the level from every existing test.
    /// </summary>
    private sealed class Harness
    {
        private readonly string _keyFilePath;
        private readonly string _gatewayId;

        public Harness(string keyFilePath, string gatewayId)
        {
            _keyFilePath = keyFilePath;
            _gatewayId = gatewayId;
            Clock = new FakeTimeProvider();

            Service = new TunnelService(
                new TunnelManager(ResolveVerifierAsync, CreateClient, ConnectAndFailAsync),
                new HostKeyStore(),
                new HostKeyTrustService(new HostKeyStore()),
                new ConnectionStateMachine(),
                new LocalizationManager(),
                RejectingHostKeyVerifier.Instance,
                new NeverProbedPlinkHostKeyProbe(),
                Clock,
                _ => new SshAgentRegistry([]),
                new TunnelFailureLogCoalescer(Clock, TunnelFailureLogCoalescer.DefaultWindow));
        }

        public FakeTimeProvider Clock { get; }

        public TunnelService Service { get; }

        public Task<TunnelSetupOutcome> ConnectAsync(string serverId, int? gatewaySuffix = null)
        {
            SshGatewayDto gateway = new SshGatewayDto
            {
                Id = gatewaySuffix is null ? _gatewayId : $"{_gatewayId}-{gatewaySuffix}",
                Name = "bastion",
                Host = "gw.example.test",
                Port = 22,
                User = "ssh-user",
                KeyPath = _keyFilePath
            };
            ServerProfileDto server = new ServerProfileDto
            {
                Id = serverId,
                RemoteServer = "target.example.test",
                RemotePort = 3389,
                ConnectionType = "RDP",
                SshGatewayId = gateway.Id,
                UseDirectConnection = false
            };

            return Service.SetupTunnelIfNeededAsync(
                server,
                3389,
                new AppSettings { SshGateways = [gateway] },
                CancellationToken.None);
        }

        private static Task<PinnedFingerprintVerifier> ResolveVerifierAsync(
            SshConnectionParams connectionParams,
            string verificationHost,
            int verificationPort,
            HostKeyStore hostKeyStore,
            IHostKeyVerifier verifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PinnedFingerprintVerifier(verificationHost, verificationPort, "SHA256:pinned"));

        private static SshClient CreateClient(SshConnectionParams connectionParams) =>
            new SshClient(new ConnectionInfo(
                connectionParams.Host,
                connectionParams.Port,
                connectionParams.Username,
                new NoneAuthenticationMethod(connectionParams.Username)));

        private static Task ConnectAndFailAsync(
            SshClient client,
            string verificationHost,
            int verificationPort,
            PinnedFingerprintVerifier pinnedVerifier,
            CancellationToken cancellationToken,
            string cancelLogMessage) =>
            throw new SshAuthenticationException(RelayedServerRefusal);
    }

    private sealed class NeverProbedPlinkHostKeyProbe : IPlinkHostKeyProbe
    {
        public Task<PlinkHostKeyPresentation?> ProbeAsync(
            string plinkPath,
            string host,
            int port,
            string? username,
            int timeoutMs,
            CancellationToken ct) =>
            throw new InvalidOperationException("These tests never reach the Plink fallback.");
    }
}
