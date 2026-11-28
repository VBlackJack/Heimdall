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

using System.Text;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh.Tests;

public sealed class OpenSshBackendTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "heimdall-openssh-tests-" + Guid.NewGuid().ToString("N"));

    public OpenSshBackendTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ClientLocator_PrefersConfiguredLocalFile_RejectsUnc_AndFallsBackToSystem32ThenPath()
    {
        string explicitExe = Path.Combine(_tempDir, "custom-ssh.exe");
        string sysRoot = Path.Combine(_tempDir, "Win");
        string sysExe = Path.Combine(sysRoot, "System32", "OpenSSH", "ssh.exe");
        string pathDir = Path.Combine(_tempDir, "bin");
        string pathExe = Path.Combine(pathDir, "ssh.exe");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { explicitExe, sysExe, pathExe };

        Assert.Equal(explicitExe, OpenSshClientLocator.ResolveExecutablePath(
            explicitExe,
            existing.Contains,
            sysRoot,
            pathDir));

        Assert.Null(OpenSshClientLocator.ResolveExecutablePath(
            @"\\attacker.example\share\ssh.exe",
            existing.Contains,
            sysRoot,
            pathDir));

        Assert.Null(OpenSshClientLocator.ResolveExecutablePath(
            Path.Combine(_tempDir, "missing-ssh.exe"),
            existing.Contains,
            sysRoot,
            pathDir));

        Assert.Equal(sysExe, OpenSshClientLocator.ResolveExecutablePath(
            string.Empty,
            existing.Contains,
            sysRoot,
            pathDir));

        existing.Remove(sysExe);
        Assert.Equal(pathExe, OpenSshClientLocator.ResolveExecutablePath(
            null,
            existing.Contains,
            sysRoot,
            pathDir));
    }

    [Fact]
    public void EphemeralKnownHosts_WritesNoBomMapsTunnelEndpointExcludesRevokedAndDisposesCleanly()
    {
        var store = new HostKeyStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        store.TrustEntry("app.internal", 2222, new HostKeyEntry("SHA256:app", now, now, "ssh-ed25519", HostKeySource.UserConfirmed) { PublicKeyBase64 = "AAAAC3NzaC1lZDI1NTE5AAAAIAppKey" });
        store.TrustEntry("revoked.internal", 22, new HostKeyEntry("SHA256:rev", now, now, "ssh-ed25519", HostKeySource.UserConfirmed) { PublicKeyBase64 = "AAAAC3NzaC1lZDI1NTE5AAAAIRevKey" });
        store.AddRevokedKey("*", "SHA256:rev");

        using var ephemeral = OpenSshEphemeralKnownHosts.Create(
            store,
            transportHost: "127.0.0.1",
            transportPort: 49200,
            logicalHost: "app.internal",
            logicalPort: 2222,
            baseDirectory: _tempDir);

        Assert.True(ephemeral.TargetHostPinned);
        Assert.True(File.Exists(ephemeral.FilePath));

        byte[] raw = File.ReadAllBytes(ephemeral.FilePath);
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF);

        string text = Encoding.UTF8.GetString(raw);
        Assert.Contains("[127.0.0.1]:49200 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAppKey", text, StringComparison.Ordinal);
        Assert.Contains("[app.internal]:2222 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAppKey", text, StringComparison.Ordinal);
        Assert.DoesNotContain("revoked.internal", text, StringComparison.Ordinal);

        string createdPath = ephemeral.FilePath;
        ephemeral.Dispose();
        Assert.False(File.Exists(createdPath));
    }

    [Fact]
    public void KnownHostsJanitor_SweepsDeadPidAndStaleFilesWhileKeepingLiveOwnPid()
    {
        string liveFile = Path.Combine(_tempDir, $"kh-{Environment.ProcessId}-live.txt");
        string deadPidFile = Path.Combine(_tempDir, "kh-999999-dead.txt");
        string oldFile = Path.Combine(_tempDir, "kh-notapid-old.txt");
        File.WriteAllText(liveFile, "a");
        File.WriteAllText(deadPidFile, "b");
        File.WriteAllText(oldFile, "c");

        DateTime now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(liveFile, now.AddMinutes(-5));
        File.SetLastWriteTimeUtc(deadPidFile, now.AddMinutes(-5));
        File.SetLastWriteTimeUtc(oldFile, now.AddHours(-24));

        var janitor = new OpenSshKnownHostsJanitor(
            directory: _tempDir,
            isProcessAlive: pid => pid == Environment.ProcessId,
            isOwnedByCurrentUser: _ => true,
            utcNow: () => now);

        int deleted = janitor.SweepStaleFiles();

        Assert.Equal(2, deleted);
        Assert.True(File.Exists(liveFile));
        Assert.False(File.Exists(deadPidFile));
        Assert.False(File.Exists(oldFile));
    }

    [Fact]
    public void LaunchBuilder_BuildsHardenedArguments_AndGatesStartupExecution()
    {
        IReadOnlyList<string> argsUntrusted = OpenSshLaunchBuilder.BuildArguments(new OpenSshLaunchSpec
        {
            Host = "prod.example.com",
            Port = 2222,
            Username = "alice",
            EphemeralKnownHostsPath = @"C:\temp\kh.txt",
            StrictKnownHostOnly = true,
            KeyPath = @"C:\keys\id ed25519",
            CertificatePath = @"C:\keys\id ed25519-cert.pub",
            AgentForwarding = true,
            X11Forwarding = true,
            Compression = true,
            CryptoPolicy = SshCryptoPolicy.StrictModern,
            LocalForwards = ["5432:db.internal:5432"],
            SocksProxyPort = 1080,
            RemoteBindPort = 9090,
            RemoteLocalPort = 3000,
            AllowStartupExecution = false,
            StartupCommand = "tmux attach",
            EnvironmentVariables = ["ENV=prod"],
            ServerAliveIntervalSeconds = 30
        });

        Assert.Contains("-F", argsUntrusted);
        Assert.Contains("none", argsUntrusted);
        Assert.Contains("GlobalKnownHostsFile=none", argsUntrusted);
        Assert.Contains("UserKnownHostsFile=C:\\temp\\kh.txt", argsUntrusted);
        Assert.Contains("StrictHostKeyChecking=yes", argsUntrusted);
        Assert.Contains("-X", argsUntrusted);
        Assert.Contains("-C", argsUntrusted);
        Assert.Contains("-A", argsUntrusted);
        Assert.Contains($"KexAlgorithms={OpenSshLaunchBuilder.StrictModernKexAlgorithms}", argsUntrusted);
        Assert.Contains($"Ciphers={OpenSshLaunchBuilder.StrictModernCiphers}", argsUntrusted);
        Assert.Contains("127.0.0.1:5432:db.internal:5432", argsUntrusted);
        Assert.Contains("127.0.0.1:1080", argsUntrusted);
        Assert.Contains("9090:127.0.0.1:3000", argsUntrusted);
        Assert.DoesNotContain("SetEnv=ENV=prod", argsUntrusted);
        Assert.DoesNotContain("tmux attach", argsUntrusted);
        Assert.Equal("--", argsUntrusted[^2]);
        Assert.Equal("alice@prod.example.com", argsUntrusted[^1]);

        IReadOnlyList<string> argsTrusted = OpenSshLaunchBuilder.BuildArguments(new OpenSshLaunchSpec
        {
            Host = "prod.example.com",
            Port = 22,
            Username = "alice",
            EphemeralKnownHostsPath = @"C:\temp\kh.txt",
            StrictKnownHostOnly = false,
            AllowStartupExecution = true,
            StartupCommand = "tmux attach",
            EnvironmentVariables = ["ENV=prod"]
        });

        Assert.Contains("StrictHostKeyChecking=ask", argsTrusted);
        Assert.Contains("SetEnv=ENV=prod", argsTrusted);
        Assert.Contains("-t", argsTrusted);
        Assert.Equal("--", argsTrusted[^3]);
        Assert.Equal("alice@prod.example.com", argsTrusted[^2]);
        Assert.Equal("tmux attach", argsTrusted[^1]);

        string joined = OpenSshLaunchBuilder.JoinWindowsCommandLine(argsUntrusted);
        Assert.Contains("\"C:\\keys\\id ed25519\"", joined, StringComparison.Ordinal);
    }
}
