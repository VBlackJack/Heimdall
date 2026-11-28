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

using System.Diagnostics;
using Heimdall.Core.Ssh;
using Xunit.Abstractions;

namespace Heimdall.Ssh.Tests;

public sealed class OpenSshLaunchArgumentTests(ITestOutputHelper output)
{
    private const string KnownHosts = @"C:\temp\kh.txt";

    private static readonly string[] Inbox81Kex =
    [
        "curve25519-sha256", "curve25519-sha256@libssh.org", "ecdh-sha2-nistp256", "ecdh-sha2-nistp384",
        "ecdh-sha2-nistp521", "diffie-hellman-group16-sha512", "diffie-hellman-group18-sha512",
    ];

    private static readonly string[] Inbox95Kex =
    [
        "sntrup761x25519-sha512@openssh.com", "curve25519-sha256", "curve25519-sha256@libssh.org",
        "ecdh-sha2-nistp256", "ecdh-sha2-nistp384", "ecdh-sha2-nistp521",
        "diffie-hellman-group16-sha512", "diffie-hellman-group18-sha512",
    ];

    private static readonly string[] Ciphers =
    [
        "chacha20-poly1305@openssh.com", "aes128-ctr", "aes192-ctr", "aes256-ctr",
        "aes128-gcm@openssh.com", "aes256-gcm@openssh.com",
    ];

    private static readonly string[] Macs =
    [
        "umac-128-etm@openssh.com", "hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com",
        "hmac-sha2-256", "hmac-sha2-512",
    ];

    private static OpenSshLaunchSpec Spec() => new()
    {
        Host = "prod.example.com",
        Username = "alice",
        EphemeralKnownHostsPath = KnownHosts,
    };

    private static OpenSshClientCapabilities Caps(
        OpenSshVersion version,
        string[]? kex = null,
        string[]? ciphers = null,
        string[]? macs = null,
        bool known = true)
    {
        OpenSshAlgorithmList List(string[]? names, string[] fallback)
            => known ? new OpenSshAlgorithmList(names ?? fallback, true) : OpenSshAlgorithmList.Unknown;

        return new OpenSshClientCapabilities(
            @"C:\Windows\System32\OpenSSH\ssh.exe",
            version,
            List(kex, Inbox95Kex),
            List(ciphers, Ciphers),
            List(macs, Macs),
            OpenSshAlgorithmList.Unknown);
    }

    private static OpenSshLaunchException Refused(OpenSshLaunchSpec spec)
        => Assert.Throws<OpenSshLaunchException>(() => OpenSshLaunchBuilder.Build(spec));

    private static string Value(IReadOnlyList<string> args, string prefix)
        => args.Single(a => a.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void Paths_AreAbsolute_AndTheTwoOptionValuesAreDoubleQuoted()
    {
        IReadOnlyList<string> args = OpenSshLaunchBuilder.BuildArguments(Spec() with
        {
            EphemeralKnownHostsPath = @"C:\Users\O'Brien\my profile\kh",
            CertificatePath = @"C:\keys\my certs\id-cert.pub",
            KeyPath = @"C:\keys\my keys\id_ed25519",
        });

        Assert.Equal("UserKnownHostsFile=\"C:\\Users\\O'Brien\\my profile\\kh\"", Value(args, "UserKnownHostsFile="));
        Assert.Equal("CertificateFile=\"C:\\keys\\my certs\\id-cert.pub\"", Value(args, "CertificateFile="));

        int identity = args.ToList().IndexOf("-i");
        Assert.True(identity >= 0);
        Assert.Equal(@"C:\keys\my keys\id_ed25519", args[identity + 1]);
        Assert.Contains("IdentitiesOnly=yes", args);
    }

    [Fact]
    public void NoKeyPath_NeverAddsIdentitiesOnly()
    {
        Assert.DoesNotContain("IdentitiesOnly=yes", OpenSshLaunchBuilder.BuildArguments(Spec()));
    }

    [Theory]
    [InlineData(@"C:\a%b\kh")]
    [InlineData(@"C:\a${HOME}\kh")]
    [InlineData("C:\\a\"b\\kh")]
    [InlineData(@"C:\a\'b\kh")]
    [InlineData("C:\\a\\kh\\")]
    public void PathsSshWouldExpandOrMisread_AreRefusedForEveryPathOption(string path)
    {
        Assert.Equal(OpenSshLaunchFailure.PathNotAllowed, Refused(Spec() with { EphemeralKnownHostsPath = path }).Reason);
        Assert.Equal(OpenSshLaunchFailure.PathNotAllowed, Refused(Spec() with { CertificatePath = path }).Reason);
        Assert.Equal(OpenSshLaunchFailure.PathNotAllowed, Refused(Spec() with { KeyPath = path }).Reason);
    }

    [Fact]
    public void SetEnv_IsQuoted_AndSurvivesTheWindowsCommandLine()
    {
        IReadOnlyList<string> args = OpenSshLaunchBuilder.BuildArguments(Spec() with
        {
            AllowStartupExecution = true,
            EnvironmentVariables = ["GREETING=hello world", "EMPTY="],
        });

        Assert.Contains("SetEnv=GREETING=\"hello world\"", args);
        Assert.Contains("SetEnv=EMPTY=\"\"", args);
        string line = OpenSshLaunchBuilder.JoinWindowsCommandLine(args);
        Assert.Contains("\"SetEnv=GREETING=\\\"hello world\\\"\"", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A=x\"y")]
    [InlineData("A=x'y")]
    [InlineData("A=x\\y")]
    [InlineData("A=x\ty")]
    public void SetEnv_ValuesThatCannotBeQuoted_AreDroppedAndCounted(string entry)
    {
        OpenSshLaunchPlan plan = OpenSshLaunchBuilder.Build(Spec() with
        {
            AllowStartupExecution = true,
            EnvironmentVariables = [entry, "GOOD=1"],
        });

        Assert.DoesNotContain(plan.Arguments, a => a.StartsWith("SetEnv=A=", StringComparison.Ordinal));
        Assert.Contains("SetEnv=GOOD=\"1\"", plan.Arguments);
        Assert.Equal(1, plan.DroppedEnvironmentVariables);
        Assert.Equal(1, plan.DroppedEntryCount);
    }

    [Fact]
    public void SetEnv_BeyondTheLimit_IsReportedNotDroppedSilently()
    {
        string[] entries = Enumerable.Range(0, SshStartupCommandPolicy.MaxEnvironmentVariables + 3)
            .Select(i => $"V{i}=1")
            .ToArray();

        OpenSshLaunchPlan plan = OpenSshLaunchBuilder.Build(Spec() with
        {
            AllowStartupExecution = true,
            EnvironmentVariables = entries,
        });

        Assert.Equal(SshStartupCommandPolicy.MaxEnvironmentVariables, plan.Arguments.Count(a => a.StartsWith("SetEnv=", StringComparison.Ordinal)));
        Assert.Equal(3, plan.DroppedEnvironmentVariables);
    }

    [Fact]
    public void SetEnv_IsRefusedBelow78_AndAllowedFrom78()
    {
        OpenSshLaunchSpec withEnv = Spec() with
        {
            AllowStartupExecution = true,
            EnvironmentVariables = ["A=1"],
        };

        Assert.Equal(
            OpenSshLaunchFailure.SetEnvUnsupported,
            Refused(withEnv with { Capabilities = Caps(new OpenSshVersion(7, 6, 1)) }).Reason);
        Assert.Contains(
            "SetEnv=A=\"1\"",
            OpenSshLaunchBuilder.BuildArguments(withEnv with { Capabilities = Caps(new OpenSshVersion(7, 8, 0)) }));

        // Nothing to send, or startup execution not approved: an old client is not a problem.
        OpenSshClientCapabilities old = Caps(new OpenSshVersion(7, 6, 1));
        Assert.NotEmpty(OpenSshLaunchBuilder.BuildArguments(Spec() with { Capabilities = old }));
        Assert.NotEmpty(OpenSshLaunchBuilder.BuildArguments(withEnv with { Capabilities = old, AllowStartupExecution = false }));
    }

    [Fact]
    public void StrictModern_KeepsOnlyWhatTheBinarySupports_InHeimdallOrder()
    {
        OpenSshLaunchSpec strict = Spec() with { CryptoPolicy = SshCryptoPolicy.StrictModern };

        IReadOnlyList<string> inbox81 = OpenSshLaunchBuilder.BuildArguments(
            strict with { Capabilities = Caps(new OpenSshVersion(8, 1, 1), kex: Inbox81Kex) });
        string kex81 = Value(inbox81, "KexAlgorithms=");
        Assert.DoesNotContain("mlkem768x25519-sha256", kex81, StringComparison.Ordinal);
        Assert.DoesNotContain("sntrup761x25519-sha512@openssh.com", kex81, StringComparison.Ordinal);
        Assert.Equal(
            "KexAlgorithms=curve25519-sha256,curve25519-sha256@libssh.org,ecdh-sha2-nistp521,ecdh-sha2-nistp384,ecdh-sha2-nistp256,diffie-hellman-group18-sha512,diffie-hellman-group16-sha512",
            kex81);

        IReadOnlyList<string> inbox95 = OpenSshLaunchBuilder.BuildArguments(
            strict with { Capabilities = Caps(new OpenSshVersion(9, 5, 0), kex: Inbox95Kex) });
        string kex95 = Value(inbox95, "KexAlgorithms=");
        Assert.StartsWith("KexAlgorithms=sntrup761x25519-sha512@openssh.com,curve25519-sha256", kex95, StringComparison.Ordinal);
        Assert.DoesNotContain("mlkem", kex95, StringComparison.Ordinal);

        // Ciphers and MACs lose what the binary lacks, order kept.
        IReadOnlyList<string> trimmed = OpenSshLaunchBuilder.BuildArguments(strict with
        {
            Capabilities = Caps(
                new OpenSshVersion(8, 1, 1),
                ciphers: ["aes128-ctr", "chacha20-poly1305@openssh.com"],
                macs: ["hmac-sha2-512", "hmac-sha2-512-etm@openssh.com"]),
        });
        Assert.Equal("Ciphers=chacha20-poly1305@openssh.com,aes128-ctr", Value(trimmed, "Ciphers="));
        Assert.Equal("MACs=hmac-sha2-512-etm@openssh.com,hmac-sha2-512", Value(trimmed, "MACs="));
    }

    [Theory]
    [InlineData("kex", OpenSshLaunchFailure.NoCommonKexAlgorithm)]
    [InlineData("cipher", OpenSshLaunchFailure.NoCommonCipher)]
    [InlineData("mac", OpenSshLaunchFailure.NoCommonMac)]
    public void StrictModern_WithAnEmptyIntersection_IsRefused(string which, OpenSshLaunchFailure expected)
    {
        string[] none = ["nothing-in-common"];
        OpenSshClientCapabilities caps = which switch
        {
            "kex" => Caps(new OpenSshVersion(8, 1, 1), kex: none),
            "cipher" => Caps(new OpenSshVersion(8, 1, 1), ciphers: none),
            _ => Caps(new OpenSshVersion(8, 1, 1), macs: none),
        };

        OpenSshLaunchException ex = Refused(Spec() with { CryptoPolicy = SshCryptoPolicy.StrictModern, Capabilities = caps });

        Assert.Equal(expected, ex.Reason);
        Assert.DoesNotContain("nothing-in-common", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StrictModern_WithUnverifiedLists_PassesHeimdallListsUnchanged()
    {
        IReadOnlyList<string> args = OpenSshLaunchBuilder.BuildArguments(Spec() with
        {
            CryptoPolicy = SshCryptoPolicy.StrictModern,
            Capabilities = Caps(new OpenSshVersion(9, 5, 0), known: false),
        });

        Assert.Equal($"KexAlgorithms={OpenSshLaunchBuilder.StrictModernKexAlgorithms}", Value(args, "KexAlgorithms="));
        Assert.Equal($"Ciphers={OpenSshLaunchBuilder.StrictModernCiphers}", Value(args, "Ciphers="));
        Assert.Equal($"MACs={OpenSshLaunchBuilder.StrictModernMacs}", Value(args, "MACs="));
    }

    [Fact]
    public void StandardPolicy_NeverTouchesTheAlgorithmLists()
    {
        IReadOnlyList<string> args = OpenSshLaunchBuilder.BuildArguments(
            Spec() with { Capabilities = Caps(new OpenSshVersion(8, 1, 1), kex: ["nothing-in-common"]) });

        Assert.DoesNotContain(args, a => a.StartsWith("KexAlgorithms=", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalForward_BracketsIpv6BindAndRemoteHost()
    {
        OpenSshLaunchPlan plan = OpenSshLaunchBuilder.Build(Spec() with
        {
            LocalForwards = ["[::1]:8443:[2001:db8::10]:443", "5432:db.internal:5432"],
        });

        Assert.Contains("[::1]:8443:[2001:db8::10]:443", plan.Arguments);
        Assert.Contains("127.0.0.1:5432:db.internal:5432", plan.Arguments);
        Assert.Equal(0, plan.DroppedLocalForwards);
    }

    [Fact]
    public void LocalForward_ThatDoesNotParse_IsCountedNotSilentlyDropped()
    {
        OpenSshLaunchPlan plan = OpenSshLaunchBuilder.Build(Spec() with
        {
            LocalForwards = ["not a forward", "", "5432:db.internal:5432"],
        });

        Assert.Equal(1, plan.DroppedLocalForwards);
        Assert.Equal(1, plan.Arguments.Count(a => a == "-L"));
    }

    [Fact]
    public void ZeroedGatewayPorts_EmitNoDynamicOrRemoteForward()
    {
        IReadOnlyList<string> args = OpenSshLaunchBuilder.BuildArguments(
            Spec() with { SocksProxyPort = 0, RemoteBindPort = 0, RemoteLocalPort = 0 });

        Assert.DoesNotContain("-D", args);
        Assert.DoesNotContain("-R", args);
    }

    [Fact]
    public void RefusalsCarryATypedReason_AndNoUserFacingEnglish()
    {
        Assert.Equal(OpenSshLaunchFailure.InvalidHost, Refused(Spec() with { Host = "bad host!" }).Reason);
        Assert.Equal(OpenSshLaunchFailure.InvalidPort, Refused(Spec() with { Port = 70000 }).Reason);
        Assert.Equal(OpenSshLaunchFailure.InvalidUsername, Refused(Spec() with { Username = "a b;c" }).Reason);
        Assert.Equal(OpenSshLaunchFailure.InvalidKnownHostsPath, Refused(Spec() with { EphemeralKnownHostsPath = @"\\srv\share\kh" }).Reason);
        Assert.Equal(OpenSshLaunchFailure.InvalidHostKeyAlias, Refused(Spec() with { HostKeyAlias = "bad alias" }).Reason);
        Assert.Equal(
            OpenSshLaunchFailure.UnsupportedPinnedKeyType,
            Refused(Spec() with { PinnedKeyTypes = ["ssh-dss"] }).Reason);
    }

    /// <summary>
    /// Runs the real Windows ssh.exe in print-config mode to prove the command line is read the
    /// way the builder assumes. Returns early on a machine without the Windows OpenSSH client.
    /// </summary>
    [Fact]
    public void RealWindowsSsh_ReadsSpacedPathsAndSetEnvAsSingleValues()
    {
        string inbox = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
        OpenSshResolution resolution = OpenSshClientLocator.ResolveAttested(File.Exists(inbox) ? inbox : null);
        if (resolution.Capabilities is not { } capabilities)
        {
            output.WriteLine("No attested Windows ssh.exe on this machine: the real-ssh check is skipped.");
            return;
        }

        output.WriteLine($"ssh.exe {capabilities.ExecutablePath} version {capabilities.Version}");

        string root = Path.Combine(Path.GetTempPath(), "heimdall ssh -G test");
        string knownHosts = Path.Combine(root, "known hosts dir", "kh file");
        string certificate = Path.Combine(root, "cert dir", "id-cert.pub");
        string identity = Path.Combine(root, "key dir", "id_ed25519");
        bool env = capabilities.SupportsSetEnv;
        IReadOnlyList<string> built = OpenSshLaunchBuilder.BuildArguments(Spec() with
        {
            Host = "example.invalid",
            Username = null,
            EphemeralKnownHostsPath = knownHosts,
            CertificatePath = certificate,
            KeyPath = identity,
            AllowStartupExecution = env,
            EnvironmentVariables = env ? ["GREETING=hello world"] : [],
            Capabilities = capabilities,
        });

        string commandLine = OpenSshLaunchBuilder.JoinWindowsCommandLine(["-G", .. built]);
        var info = new ProcessStartInfo(capabilities.ExecutablePath)
        {
            Arguments = commandLine,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(info)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(OpenSshCapabilitiesReader.ProcessTimeout), "ssh -G did not finish.");
        output.WriteLine($"exit {process.ExitCode}: {stderr}");
        Assert.Equal(0, process.ExitCode);

        string[] lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(knownHosts, Unquote(Single(lines, "userknownhostsfile ")));
        Assert.Equal(certificate, Unquote(Single(lines, "certificatefile ")));
        if (env)
        {
            Assert.Equal("GREETING=hello world", Unquote(Single(lines, "setenv ")));
        }
    }

    private static string Single(string[] lines, string prefix)
        => lines.Single(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))[prefix.Length..];

    private static string Unquote(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
    }
}
