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
    public void EphemeralKnownHosts_WritesExactlyTheGivenLinesUnderTheAlias_NoBom_AndDisposesCleanly()
    {
        using var ephemeral = OpenSshEphemeralKnownHosts.Create(
            [
                new OpenSshKnownHostsKey("ssh-ed25519", "AAAAC3NzaC1lZDI1NTE5AAAAIAppKey"),
                new OpenSshKnownHostsKey("ssh-rsa", "AAAAB3NzaC1yc2EAAAADAQABAAABgQC+/="),
                new OpenSshKnownHostsKey("ssh-ed25519", "AAAAC3NzaC1lZDI1NTE5AAAAIAppKey"),
            ],
            baseDirectory: _tempDir);

        Assert.True(File.Exists(ephemeral.FilePath));

        byte[] raw = File.ReadAllBytes(ephemeral.FilePath);
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF);

        string text = Encoding.UTF8.GetString(raw);
        Assert.Equal(
            $"{OpenSshEphemeralKnownHosts.HostKeyAlias} ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAppKey\n"
            + $"{OpenSshEphemeralKnownHosts.HostKeyAlias} ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQC+/=\n",
            text);

        string createdPath = ephemeral.FilePath;
        ephemeral.Dispose();
        Assert.False(File.Exists(createdPath));
    }

    [Fact]
    public void EphemeralKnownHosts_NeverCopiesTheHostKeyStore()
    {
        var store = new HostKeyStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        store.TrustEntry("other.internal", 22, new HostKeyEntry("SHA256:other", now, now, "ssh-ed25519", HostKeySource.UserConfirmed) { PublicKeyBase64 = "AAAAC3NzaC1lZDI1NTE5AAAAIOtherKey" });

        using var ephemeral = OpenSshEphemeralKnownHosts.Create(
            [new OpenSshKnownHostsKey("ssh-ed25519", "AAAAC3NzaC1lZDI1NTE5AAAAIDecided")],
            baseDirectory: _tempDir);

        string text = File.ReadAllText(ephemeral.FilePath);
        Assert.DoesNotContain("other.internal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OtherKey", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("ssh-dss", "AAAAB3NzaC1kc3M=")]
    [InlineData("sk-ssh-ed25519@openssh.com", "AAAAGnNrLXNzaC1lZDI1NTE5")]
    [InlineData("ssh-ed25519", "AAAA BBBB")]
    [InlineData("ssh-ed25519", "AAAA\nssh-rsa BBBB")]
    [InlineData("ssh-ed25519", "")]
    public void EphemeralKnownHosts_RefusesAnUnsupportedTypeOrAMalformedBlob(string keyType, string blob)
    {
        Assert.Throws<ArgumentException>(() => OpenSshEphemeralKnownHosts.BuildKnownHostsContent(
            [new OpenSshKnownHostsKey(keyType, blob)]));
    }

    [Fact]
    public void EphemeralKnownHosts_RefusesAnEmptyKeyList()
    {
        Assert.Throws<ArgumentException>(() => OpenSshEphemeralKnownHosts.BuildKnownHostsContent([]));
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
        Assert.Contains("UserKnownHostsFile=\"C:\\temp\\kh.txt\"", argsUntrusted);
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
            AllowStartupExecution = true,
            StartupCommand = "tmux attach",
            EnvironmentVariables = ["ENV=prod"]
        });

        Assert.Contains("StrictHostKeyChecking=yes", argsTrusted);
        Assert.Contains("SetEnv=ENV=\"prod\"", argsTrusted);
        Assert.Contains("-t", argsTrusted);
        Assert.Equal("--", argsTrusted[^3]);
        Assert.Equal("alice@prod.example.com", argsTrusted[^2]);
        Assert.Equal("tmux attach", argsTrusted[^1]);

        string joined = OpenSshLaunchBuilder.JoinWindowsCommandLine(argsUntrusted);
        Assert.Contains("\"C:\\keys\\id ed25519\"", joined, StringComparison.Ordinal);
    }

    private static OpenSshLaunchSpec PinnedSpec(
        IReadOnlyList<string> keyTypes,
        SshCryptoPolicy policy = SshCryptoPolicy.Standard,
        bool certificate = false)
        => new()
        {
            Host = "prod.example.com",
            EphemeralKnownHostsPath = @"C:\temp\kh.txt",
            PinnedKeyTypes = keyTypes,
            HostCertificatePresented = certificate,
            CryptoPolicy = policy,
        };

    private static string[] HostKeyAlgorithmValues(IReadOnlyList<string> args)
        => args.Where(a => a.StartsWith("HostKeyAlgorithms=", StringComparison.Ordinal)).ToArray();

    [Theory]
    [InlineData(SshCryptoPolicy.Standard)]
    [InlineData(SshCryptoPolicy.StrictModern)]
    public void LaunchBuilder_AlwaysStrict_NeverAsksOrAcceptsNew_WhateverTheSpec(SshCryptoPolicy policy)
    {
        IReadOnlyList<string> args = OpenSshLaunchBuilder.BuildArguments(PinnedSpec(["ssh-ed25519"], policy));

        Assert.Contains("StrictHostKeyChecking=yes", args);
        Assert.Contains("CheckHostIP=no", args);
        Assert.Contains("UpdateHostKeys=no", args);
        Assert.Contains($"HostKeyAlias={OpenSshEphemeralKnownHosts.HostKeyAlias}", args);
        Assert.DoesNotContain(args, a => a.Contains("=ask", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains("accept-new", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains("StrictHostKeyChecking=no", StringComparison.Ordinal));
        Assert.Single(args, a => a.StartsWith("StrictHostKeyChecking=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ssh-ed25519", "ssh-ed25519")]
    [InlineData("ecdsa-sha2-nistp256", "ecdsa-sha2-nistp256")]
    [InlineData("ecdsa-sha2-nistp384", "ecdsa-sha2-nistp384")]
    [InlineData("ecdsa-sha2-nistp521", "ecdsa-sha2-nistp521")]
    [InlineData("ssh-rsa", "rsa-sha2-512,rsa-sha2-256,ssh-rsa")]
    public void LaunchBuilder_PinsHostKeyAlgorithmsToThePinnedKeyType(string keyType, string expected)
    {
        IReadOnlyList<string> args = OpenSshLaunchBuilder.BuildArguments(PinnedSpec([keyType]));

        Assert.Equal([$"HostKeyAlgorithms={expected}"], HostKeyAlgorithmValues(args));
    }

    [Fact]
    public void LaunchBuilder_AppendsTheMatchingCertificateAlgorithmsAfterThePlainOnes()
    {
        IReadOnlyList<string> ed = OpenSshLaunchBuilder.BuildArguments(PinnedSpec(["ssh-ed25519"], certificate: true));
        IReadOnlyList<string> rsa = OpenSshLaunchBuilder.BuildArguments(PinnedSpec(["ssh-rsa"], certificate: true));

        Assert.Equal(
            ["HostKeyAlgorithms=ssh-ed25519,ssh-ed25519-cert-v01@openssh.com"],
            HostKeyAlgorithmValues(ed));
        Assert.Equal(
            ["HostKeyAlgorithms=rsa-sha2-512,rsa-sha2-256,ssh-rsa,rsa-sha2-512-cert-v01@openssh.com,rsa-sha2-256-cert-v01@openssh.com,ssh-rsa-cert-v01@openssh.com"],
            HostKeyAlgorithmValues(rsa));
    }

    [Fact]
    public void LaunchBuilder_StrictModern_ReplacesItsHostKeyListWithTheFilteredPinnedOne_EmittedOnce()
    {
        IReadOnlyList<string> rsa = OpenSshLaunchBuilder.BuildArguments(
            PinnedSpec(["ssh-rsa"], SshCryptoPolicy.StrictModern, certificate: true));
        IReadOnlyList<string> ed = OpenSshLaunchBuilder.BuildArguments(
            PinnedSpec(["ssh-ed25519"], SshCryptoPolicy.StrictModern));

        Assert.Equal(
            ["HostKeyAlgorithms=rsa-sha2-512,rsa-sha2-256,rsa-sha2-512-cert-v01@openssh.com,rsa-sha2-256-cert-v01@openssh.com"],
            HostKeyAlgorithmValues(rsa));
        Assert.Equal(["HostKeyAlgorithms=ssh-ed25519"], HostKeyAlgorithmValues(ed));
        Assert.DoesNotContain(
            rsa,
            a => a.Contains("ssh-rsa", StringComparison.Ordinal));
    }

    [Fact]
    public void LaunchBuilder_StrictModernWithoutAPinnedType_KeepsItsOwnHostKeyList_EmittedOnce()
    {
        IReadOnlyList<string> args = OpenSshLaunchBuilder.BuildArguments(PinnedSpec([], SshCryptoPolicy.StrictModern));

        Assert.Equal(
            [$"HostKeyAlgorithms={OpenSshLaunchBuilder.StrictModernHostKeyAlgorithms}"],
            HostKeyAlgorithmValues(args));
    }

    [Fact]
    public void LaunchBuilder_StandardWithoutAPinnedType_LeavesTheAlgorithmChoiceToSsh()
    {
        IReadOnlyList<string> args = OpenSshLaunchBuilder.BuildArguments(PinnedSpec([]));

        Assert.Empty(HostKeyAlgorithmValues(args));
    }

    [Theory]
    [InlineData("ssh-dss")]
    [InlineData("sk-ssh-ed25519@openssh.com")]
    [InlineData("nonsense")]
    public void LaunchBuilder_RefusesAnUnsupportedPinnedKeyType(string keyType)
    {
        OpenSshLaunchException ex = Assert.Throws<OpenSshLaunchException>(
            () => OpenSshLaunchBuilder.BuildArguments(PinnedSpec([keyType])));
        Assert.Equal(OpenSshLaunchFailure.UnsupportedPinnedKeyType, ex.Reason);
    }

    [Fact]
    public void LaunchBuilder_RefusesAnAliasWithWhitespace()
    {
        OpenSshLaunchException ex = Assert.Throws<OpenSshLaunchException>(() => OpenSshLaunchBuilder.BuildArguments(
            PinnedSpec(["ssh-ed25519"]) with { HostKeyAlias = "bad alias" }));
        Assert.Equal(OpenSshLaunchFailure.InvalidHostKeyAlias, ex.Reason);
    }
}
