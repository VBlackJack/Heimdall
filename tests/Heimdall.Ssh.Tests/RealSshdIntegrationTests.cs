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

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Opt-in integration tests exercised against a real OpenSSH server (for example, a throwaway
/// GNU/Linux or Alpine Docker container). Set <c>HEIMDALL_SSHD_TEST_HOST</c>,
/// <c>HEIMDALL_SSHD_TEST_USER</c>, <c>HEIMDALL_SSHD_TEST_PASSWORD</c>, and optionally
/// <c>HEIMDALL_SSHD_TEST_PORT</c> (default 22) to run these tests.
/// </summary>
public sealed class RealSshdIntegrationTests
{
    internal const string HostVariable = "HEIMDALL_SSHD_TEST_HOST";
    internal const string PortVariable = "HEIMDALL_SSHD_TEST_PORT";
    internal const string UserVariable = "HEIMDALL_SSHD_TEST_USER";
    internal const string PasswordVariable = "HEIMDALL_SSHD_TEST_PASSWORD";

    private const int DefaultSshPort = 22;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static string Host => Environment.GetEnvironmentVariable(HostVariable)!;
    private static string Username => Environment.GetEnvironmentVariable(UserVariable)!;
    private static string Password => Environment.GetEnvironmentVariable(PasswordVariable)!;

    private static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable(PortVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 && port <= 65535
            ? port
            : DefaultSshPort;

    [Fact]
    public void SshdLabEnvironment_SkipReason_NamesRequiredVariables()
    {
        var reason = SshdLabEnvironment.FormatSkipReason(hasHost: false, hasUser: true, hasPassword: true);
        Assert.NotNull(reason);
        Assert.Contains(HostVariable, reason, StringComparison.Ordinal);
        Assert.Contains(UserVariable, reason, StringComparison.Ordinal);
        Assert.Contains(PasswordVariable, reason, StringComparison.Ordinal);

        Assert.Null(SshdLabEnvironment.FormatSkipReason(hasHost: true, hasUser: true, hasPassword: true));
    }

    [SshdLabFact]
    public async Task Ed25519EncryptedOpenSshKey_DeploysIdempotently_AndAuthenticates()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var tempDir = CreateTempDirectory();
        try
        {
            var hostKeyStore = new HostKeyStore();
            var verifier = new TrustOnceHostKeyVerifier();
            var passphrase = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var comment = $"heimdall-lab-{Guid.NewGuid():N}";

            var keyPair = SshKeyPairGenerator.Generate(
                SshKeyAlgorithm.Ed25519,
                comment: comment,
                passphrase: passphrase,
                format: SshPrivateKeyFormat.OpenSsh);

            var passwordParams = new SshConnectionParams
            {
                Host = Host,
                Port = Port,
                Username = Username,
                Password = Password
            };

            var firstDeploy = await SshPublicKeyDeployer.DeployAsync(
                passwordParams,
                keyPair.PublicKeyOpenSsh,
                hostKeyStore,
                verifier,
                cts.Token);

            Assert.True(firstDeploy.Success, firstDeploy.ErrorMessage);
            Assert.False(firstDeploy.AlreadyInstalled);

            var secondDeploy = await SshPublicKeyDeployer.DeployAsync(
                passwordParams,
                keyPair.PublicKeyOpenSsh,
                hostKeyStore,
                verifier,
                cts.Token);

            Assert.True(secondDeploy.Success, secondDeploy.ErrorMessage);
            Assert.True(secondDeploy.AlreadyInstalled);

            var keyPath = Path.Combine(tempDir, "id_ed25519_lab");
            await File.WriteAllTextAsync(keyPath, keyPair.PrivateKey, new UTF8Encoding(false), cts.Token);

            var keyParams = new SshConnectionParams
            {
                Host = Host,
                Port = Port,
                Username = Username,
                KeyPath = keyPath,
                KeyPassphrase = passphrase
            };

            var pinned = await SshConnectionFactory.ResolveHostKeyAsync(keyParams, hostKeyStore, verifier, cts.Token);
            using var client = SshConnectionFactory.CreateSshClient(keyParams);
            SshConnectionFactory.AttachPinnedHostKeyVerification(client, keyParams, pinned);
            await SshConnectionFactory.ConnectWithCancellationAsync(client, cts.Token);

            Assert.True(client.IsConnected);
            using var cmd = client.RunCommand("printf '%s' HEIMDALL_KEY_AUTH_OK");
            Assert.Equal(0, cmd.ExitStatus);
            Assert.Equal("HEIMDALL_KEY_AUTH_OK", cmd.Result);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [SshdLabFact]
    public async Task ServerHealthMonitor_CollectsRealHostMetrics_InSingleCombinedCommand()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var hostKeyStore = new HostKeyStore();
        var verifier = new TrustOnceHostKeyVerifier();
        var parameters = new SshConnectionParams
        {
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password
        };

        var pinned = await SshConnectionFactory.ResolveHostKeyAsync(parameters, hostKeyStore, verifier, cts.Token);
        using var client = SshConnectionFactory.CreateSshClient(parameters);
        SshConnectionFactory.AttachPinnedHostKeyVerification(client, parameters, pinned);
        await SshConnectionFactory.ConnectWithCancellationAsync(client, cts.Token);

        var firstSample = new TaskCompletionSource<ServerHealthData>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new ServerHealthMonitor();
        monitor.HealthUpdated += data => firstSample.TrySetResult(data);

        await monitor.StartAsync(client, cts.Token);
        var sample = await firstSample.Task.WaitAsync(cts.Token);
        await monitor.StopAsync();

        Assert.True(sample.IsSupported);
        Assert.True(sample.MemTotalMb > 0);
        Assert.InRange(sample.DiskPercent, 0, 100);
        Assert.False(string.IsNullOrWhiteSpace(sample.DiskTotal));
    }

    [SshdLabFact]
    public async Task SshShellSession_StartsAndTearsDown_DynamicForwardAndEcho()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var hostKeyStore = new HostKeyStore();
        var verifier = new TrustOnceHostKeyVerifier();
        var freeSocksPort = FindUnusedLoopbackPort();
        var parameters = new SshConnectionParams
        {
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            SocksProxyPort = freeSocksPort
        };

        using var session = new SshShellSession();
        var outputBuffer = new StringBuilder();
        var markerSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string Marker = "HEIMDALL_SSHD_ECHO_OK";

        session.DataReceived += chunk =>
        {
            lock (outputBuffer)
            {
                outputBuffer.Append(Encoding.UTF8.GetString(chunk));
                if (outputBuffer.ToString().Contains(Marker, StringComparison.Ordinal))
                {
                    markerSeen.TrySetResult(true);
                }
            }
        };

        await session.ConnectAsync(parameters, hostKeyStore, verifier, 100, 30, cts.Token);

        Assert.True(session.IsConnected);
        Assert.Equal(1, session.ActiveForwardedPortCount);

        session.Write($"printf '{Marker}\\n'\n");
        Assert.True(await markerSeen.Task.WaitAsync(cts.Token));

        session.Disconnect();
        Assert.Equal(0, session.ActiveForwardedPortCount);
    }

    [SshdLabFact]
    public async Task HostKeyResolution_EnforcesRevokedMarker_AgainstLiveSshd()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var hostKeyStore = new HostKeyStore();
        var parameters = new SshConnectionParams
        {
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password
        };

        // First probe discovers the live host key and trusts it.
        var initialVerifier = new TrustOnceHostKeyVerifier();
        var pinned = await SshConnectionFactory.ResolveHostKeyAsync(parameters, hostKeyStore, initialVerifier, cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(pinned.Fingerprint));

        // Mark the presented host key fingerprint as @revoked in a fresh policy store.
        var revokedStore = new HostKeyStore();
        revokedStore.AddRevokedKey("*", pinned.Fingerprint);

        var promptCount = 0;
        var countingVerifier = new CountingVerifier(() => promptCount++);

        await Assert.ThrowsAsync<HostKeyRejectedException>(() =>
            SshConnectionFactory.ResolveHostKeyAsync(parameters, revokedStore, countingVerifier, cts.Token));

        Assert.Equal(0, promptCount);
    }

    private static int FindUnusedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "heimdall-sshd-lab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp lab files.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of temp lab files.
        }
    }

    private sealed class TrustOnceHostKeyVerifier : IHostKeyVerifier
    {
        public Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string fingerprint,
            string? previousFingerprint,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                previousFingerprint is null ? HostKeyDecision.Accept : HostKeyDecision.Reject);
        }
    }

    private sealed class CountingVerifier(Action onPrompt) : IHostKeyVerifier
    {
        public Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string fingerprint,
            string? previousFingerprint,
            CancellationToken cancellationToken = default)
        {
            onPrompt();
            return Task.FromResult(
                previousFingerprint is null ? HostKeyDecision.Accept : HostKeyDecision.Reject);
        }
    }
}

/// <summary>A fact that runs only when the real-sshd lab environment variables are configured.</summary>
public sealed class SshdLabFactAttribute : FactAttribute
{
    public SshdLabFactAttribute()
    {
        Skip = SshdLabEnvironment.SkipReason();
    }
}

internal static class SshdLabEnvironment
{
    internal static string? SkipReason()
    {
        var hasHost = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RealSshdIntegrationTests.HostVariable));
        var hasUser = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RealSshdIntegrationTests.UserVariable));
        var hasPassword = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RealSshdIntegrationTests.PasswordVariable));
        return FormatSkipReason(hasHost, hasUser, hasPassword);
    }

    internal static string? FormatSkipReason(bool hasHost, bool hasUser, bool hasPassword)
    {
        return hasHost && hasUser && hasPassword
            ? null
            : $"Set {RealSshdIntegrationTests.HostVariable}, {RealSshdIntegrationTests.UserVariable}, and {RealSshdIntegrationTests.PasswordVariable} to run against a real sshd server.";
    }
}
