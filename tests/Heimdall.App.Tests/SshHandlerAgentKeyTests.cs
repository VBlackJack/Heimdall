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
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.OpenSsh;
using Heimdall.Ssh.Pageant;
using Heimdall.Ssh.Plink;
using Renci.SshNet.Common;
using TransportDisconnectReason = Renci.SshNet.Messages.Transport.DisconnectReason;

namespace Heimdall.App.Tests;

/// <summary>
/// A session that names one agent key is refused when the key is not usable, offers only that key
/// to the embedded client, and reaches Plink only through a public key file it can honour.
/// </summary>
/// <remarks>
/// The dial and the launch are replaced and nothing else is: the preflight, the classification, the
/// retry decision, the host-key gate and the launch preparation all run as they ship.
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshHandlerAgentKeyTests
{
    private const string TrustedHost = "server01.contoso.local";
    private const string TrustedFingerprint = "SHA256:stored-test-fingerprint";
    private const string KeyType = "ssh-ed25519";

    private static readonly byte[] BlobA = Blob(KeyType, 1);
    private static readonly byte[] BlobB = Blob(KeyType, 2);
    private static readonly byte[] BlobC = Blob(KeyType, 3);

    /// <summary>A public key blob: a length-prefixed type name followed by a payload byte.</summary>
    internal static byte[] Blob(string type, byte payload)
    {
        byte[] name = Encoding.ASCII.GetBytes(type);
        byte[] blob = new byte[sizeof(uint) + name.Length + 1];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)name.Length);
        name.CopyTo(blob, sizeof(uint));
        blob[^1] = payload;
        return blob;
    }

    private const int TunnelLocalPort = 54321;

    private static string FingerprintOf(byte[] blob) => SshAgentKeyFingerprints.Compute(blob);

    [Fact]
    public async Task ChosenKeyNotLoaded_RefusesBeforeDial_NoPlinkRetry()
    {
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])]);
        string missing = FingerprintOf(BlobC);

        ConnectionResult result = await harness.ConnectAsync(harness.Server(missing));

        Assert.False(result.Success);
        Assert.Contains(missing, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(SessionFailureStage.SshPreflight, result.Failure?.Stage);
        Assert.Null(result.FallbackFrom);

        // Nothing that costs the user a decision or a connection ran before the refusal.
        Assert.Equal(0, harness.TunnelSetups);
        Assert.Equal(0, harness.HostKeyProbes);
        Assert.Equal(0, harness.ShellConnects);
        Assert.Equal(0, harness.Launches);
    }

    [Fact]
    public async Task ChosenKeyNotLoaded_AtDial_IsNeverRetriedThroughPlink()
    {
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])],
            connectShell: (_, _, _, _, _, _, _) =>
                throw new SshAgentKeyNotLoadedException(FingerprintOf(BlobA)));

        ConnectionResult result = await harness.ConnectAsync(harness.Server(FingerprintOf(BlobA)));

        // The key was there at the preflight and gone at the dial: the failure is the factory's.
        Assert.False(result.Success);
        Assert.Contains(FingerprintOf(BlobA), result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, harness.ShellConnects);
        Assert.Equal(0, harness.Launches);
        Assert.Null(result.FallbackFrom);
    }

    [Fact]
    public async Task ChosenKey_AgentKeysAtDialIsOne()
    {
        SshConnectionParams? dialled = null;
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(
                PageantAgent.AgentName,
                [new FakeKey("a", BlobA), new FakeKey("b", BlobB), new FakeKey("c", BlobC)])],
            connectShell: (_, connectionParams, _, _, _, _, _) =>
            {
                dialled = connectionParams;
                throw new SshConnectionException(
                    "Too many authentication failures",
                    TransportDisconnectReason.ProtocolError);
            });

        ConnectionResult result = await harness.ConnectAsync(harness.Server(FingerprintOf(BlobB)));

        // The agent holds three keys, one is offered: the message states one.
        Assert.False(result.Success);
        Assert.Equal(FingerprintOf(BlobB), dialled?.AgentKeyFingerprint);
        Assert.Equal(
            harness.Localizer[SshAuthFailureLocaleKeys.TooManyAuthFailuresOneAgentKey],
            result.ErrorMessage);
    }

    [Fact]
    public async Task NoChosenKey_AgentKeysAtDialIsTheAgentCount()
    {
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(
                PageantAgent.AgentName,
                [new FakeKey("a", BlobA), new FakeKey("b", BlobB), new FakeKey("c", BlobC)])],
            connectShell: (_, _, _, _, _, _, _) =>
                throw new SshConnectionException(
                    "Too many authentication failures",
                    TransportDisconnectReason.ProtocolError));

        ConnectionResult result = await harness.ConnectAsync(harness.Server(fingerprint: null));

        Assert.Equal(
            harness.Localizer.Format(SshAuthFailureLocaleKeys.TooManyAuthFailuresAgentKeys, 3),
            result.ErrorMessage);
    }

    [Fact]
    public async Task External_ChosenKey_Refuses()
    {
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])]);
        ServerProfileDto server = harness.Server(FingerprintOf(BlobA));
        server.SshMode = "External";

        ConnectionResult result = await harness.ConnectAsync(server);

        Assert.False(result.Success);
        Assert.Equal(
            harness.Localizer[SshLocalizationKeys.ErrorSshAgentKeyLauncherUnsupported],
            result.ErrorMessage);
        Assert.Equal(0, harness.TunnelSetups);
        Assert.Equal(0, harness.Launches);
    }

    [Fact]
    public async Task ChosenKey_ForwardingOn_KeyOnlyInOpenSsh_RefusesAtPreflight()
    {
        using Harness harness = await Harness.CreateAsync(
        [
            new FakeAgent(OpenSshPipeAgent.AgentName, [new FakeKey("a", BlobA)]),
            new FakeAgent(PageantAgent.AgentName, [new FakeKey("b", BlobB)]),
        ]);
        ServerProfileDto server = harness.Server(FingerprintOf(BlobA));
        server.SshAgentForwarding = true;

        ConnectionResult result = await harness.ConnectAsync(server);

        Assert.False(result.Success);
        Assert.Equal(
            harness.Localizer.Format(SshLocalizationKeys.ErrorSshAgentKeyNotInPageant, FingerprintOf(BlobA)),
            result.ErrorMessage);
        Assert.Equal(0, harness.TunnelSetups);
    }

    [Fact]
    public async Task ChosenKey_RetryThroughUnattestedPlink_ReturnsTheEmbeddedFailure()
    {
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])],
            connectShell: (_, _, _, _, _, _, _) =>
                throw new SshAuthenticationException("Permission denied (publickey)."));

        ConnectionResult result = await harness.ConnectAsync(harness.Server(FingerprintOf(BlobA)));

        // Plink could not be held to one key here, so it is not tried: the refusal stands.
        Assert.False(result.Success);
        Assert.Equal(0, harness.Launches);
        Assert.Null(result.FallbackFrom);
        Assert.NotEqual(
            harness.Localizer[SshLocalizationKeys.ErrorSshAgentKeyLauncherUnsupported],
            result.ErrorMessage);
    }

    [Fact]
    public async Task ChosenKey_UnattestedPlink_Refuses()
    {
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])]);

        ConnectionResult result = await harness.ConnectViaPlinkAsync(harness.Server(FingerprintOf(BlobA)));

        Assert.False(result.Success);
        Assert.Equal(
            harness.Localizer[SshLocalizationKeys.ErrorSshAgentKeyPlinkNotBundled],
            result.ErrorMessage);
        Assert.Equal(0, harness.HostKeyProbes);
        Assert.Equal(0, harness.Launches);
    }

    [Fact]
    public async Task ChosenKey_NotInPageant_Refuses()
    {
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(OpenSshPipeAgent.AgentName, [new FakeKey("a", BlobA)])],
            attested: true);

        ConnectionResult result = await harness.ConnectViaPlinkAsync(harness.Server(FingerprintOf(BlobA)));

        Assert.False(result.Success);
        Assert.Equal(
            harness.Localizer.Format(SshLocalizationKeys.ErrorSshAgentKeyNotInPageant, FingerprintOf(BlobA)),
            result.ErrorMessage);
        Assert.Equal(0, harness.Launches);
    }

    [Fact]
    public async Task ChosenKeyAndKeyFile_ThroughPlink_Refuses()
    {
        string keyFile = Path.GetTempFileName();
        try
        {
            using Harness harness = await Harness.CreateAsync(
                [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])],
                attested: true);
            ServerProfileDto server = harness.Server(FingerprintOf(BlobA));
            server.SshKeyPath = keyFile;

            ConnectionResult result = await harness.ConnectViaPlinkAsync(server);

            Assert.False(result.Success);
            Assert.Equal(
                harness.Localizer[SshLocalizationKeys.ErrorSshAgentKeyPlinkKeyFile],
                result.ErrorMessage);
            Assert.Equal(0, harness.Launches);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public async Task ChosenKeyNotLoaded_ThroughPlink_Refuses()
    {
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])],
            attested: true);

        ConnectionResult result = await harness.ConnectViaPlinkAsync(harness.Server(FingerprintOf(BlobC)));

        Assert.False(result.Success);
        Assert.Contains(FingerprintOf(BlobC), result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, harness.Launches);
    }

    [Fact]
    public async Task AgentPublicKeyFile_IsOneOpenSshLine_AndIsDeletedAfterUse()
    {
        string? keyFilePath = null;
        string? passwordFilePath = null;
        string? content = null;
        string? argsSeen = null;
        bool failTheLaunch = true;
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("comment-a", BlobA), new FakeKey("b", BlobB)])],
            attested: true,
            start: (_, args) =>
            {
                argsSeen = args;
                keyFilePath = ExtractQuotedArgument(args, "-i");
                passwordFilePath = ExtractQuotedArgument(args, "-pwfile");
                content = File.ReadAllText(keyFilePath, Encoding.ASCII);
                if (failTheLaunch)
                {
                    throw new InvalidOperationException("launch refused");
                }
            },
            dialogAnswer: "typed-secret");
        ServerProfileDto server = harness.Server(FingerprintOf(BlobB));

        ConnectionResult failed = await harness.ConnectViaPlinkAsync(server);

        // One line: the key type, a space, the base64 of the blob, a newline. No comment, no
        // second line, nothing that is not public. The Pageant key picked is the chosen one.
        Assert.False(failed.Success);
        Assert.Equal($"{KeyType} {Convert.ToBase64String(BlobB)}\n", content);
        Assert.DoesNotContain("comment-a", content, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(argsSeen!, " -i "));

        // A launch that fails leaves nothing behind, the password file included.
        Assert.False(File.Exists(keyFilePath));
        Assert.False(File.Exists(passwordFilePath));

        // A launch that succeeds keeps it for the session: the key is read when authentication
        // starts, so the first byte of output proves nothing. Closing the tab disposes the
        // session, which detaches the exit notification: the file must go then.
        failTheLaunch = false;
        ConnectionResult started = await harness.ConnectViaPlinkAsync(server);
        try
        {
            Assert.True(started.Success, started.ErrorMessage);
            Assert.True(File.Exists(keyFilePath));

            ((TerminalSessionResult)started.Session!).Session.Dispose();

            Assert.False(File.Exists(keyFilePath));
        }
        finally
        {
            if (started.Session is TerminalSessionResult terminal)
            {
                terminal.Session.Dispose();
            }

            if (keyFilePath is not null)
            {
                File.Delete(keyFilePath);
            }

            if (passwordFilePath is not null)
            {
                File.Delete(passwordFilePath);
            }
        }
    }

    [Fact]
    public async Task ChosenKey_PlinkPath_PromptsForAPasswordAsWithoutAChosenKey()
    {
        string? passwordFilePath = null;
        string? argsSeen = null;
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])],
            attested: true,
            start: (_, args) =>
            {
                argsSeen = args;
                passwordFilePath = ExtractQuotedArgument(args, "-pwfile");
            },
            dialogAnswer: "typed-secret");

        ConnectionResult result = await harness.ConnectViaPlinkAsync(harness.Server(FingerprintOf(BlobA)));
        try
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Single(harness.Prompts);
            Assert.Contains("-pwfile", argsSeen, StringComparison.Ordinal);
        }
        finally
        {
            (result.Session as TerminalSessionResult)?.Session.Dispose();
            if (passwordFilePath is not null)
            {
                File.Delete(passwordFilePath);
            }
        }
    }

    [Fact]
    public async Task AnyFailureWhilePreparingTheLaunch_LeavesNoPasswordFileAndNoKeyFile()
    {
        List<string> deleted = [];
        string? passwordFilePath = null;
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])],
            attested: true,
            dialogAnswer: "typed-secret",
            writeKey: (_, _) => throw new InvalidOperationException("writer failed"),
            delete: path =>
            {
                deleted.Add(path!);
                File.Delete(path!);
            },
            start: (_, args) => passwordFilePath = ExtractQuotedArgument(args, "-pwfile"));

        ConnectionResult result = await harness.ConnectViaPlinkAsync(harness.Server(FingerprintOf(BlobA)));

        // The writer fails after the password file exists: both are freed, and nothing launched.
        Assert.False(result.Success);
        Assert.Equal("writer failed", result.ErrorMessage);
        Assert.Equal(0, harness.Launches);
        Assert.Contains(deleted, path => !path.Contains("agentkey_", StringComparison.Ordinal));
        Assert.Contains(deleted, path => path.Contains("agentkey_", StringComparison.Ordinal));
        Assert.All(deleted, path => Assert.False(File.Exists(path), path));
        Assert.Null(passwordFilePath);
    }

    [Theory]
    [InlineData("sk-ssh-ed25519@openssh.com")]
    [InlineData("sk-ecdsa-sha2-nistp256@openssh.com")]
    public async Task PlinkKeyType_NotParseableByPlink_Refuses(string type)
    {
        byte[] blob = Blob(type, 9);
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("sk", blob, type)])],
            attested: true,
            writeKey: (_, _) => throw new InvalidOperationException("no file may be written"));

        ConnectionResult result = await harness.ConnectViaPlinkAsync(harness.Server(FingerprintOf(blob)));

        Assert.False(result.Success);
        Assert.Equal(
            harness.Localizer.Format(SshLocalizationKeys.ErrorSshAgentKeyPlinkKeyType, FingerprintOf(blob)),
            result.ErrorMessage);
        Assert.Equal(0, harness.Launches);
    }

    [Fact]
    public async Task PlinkKeyType_Unknown_Refuses()
    {
        byte[] blob = [1, 1, 1, 1];
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("odd", blob, "unknown")])],
            attested: true);

        ConnectionResult result = await harness.ConnectViaPlinkAsync(harness.Server(FingerprintOf(blob)));

        Assert.False(result.Success);
        Assert.Equal(
            harness.Localizer.Format(SshLocalizationKeys.ErrorSshAgentKeyPlinkKeyType, FingerprintOf(blob)),
            result.ErrorMessage);
    }

    [Theory]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed448")]
    [InlineData("ecdsa-sha2-nistp256")]
    [InlineData("ecdsa-sha2-nistp384")]
    [InlineData("ecdsa-sha2-nistp521")]
    [InlineData("ssh-rsa")]
    [InlineData("ssh-dss")]
    [InlineData("ssh-ed25519-cert-v01@openssh.com")]
    public void PlinkKeyType_Allowed_IsAccepted(string type)
    {
        Assert.True(SshAgentKeyPreflight.IsPlinkParseable(new FakeKey("k", Blob(type, 1), type)));
    }

    [Theory]
    [InlineData("ssh-rsa-cert-v01@openssh.com")]
    [InlineData("ssh-ed448-cert-v01@openssh.com")]
    public async Task PlinkKeyType_UnmeasuredCertificate_Refuses(string type)
    {
        byte[] blob = Blob(type, 4);
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("cert", blob, type)])],
            attested: true);

        ConnectionResult result = await harness.ConnectViaPlinkAsync(harness.Server(FingerprintOf(blob)));

        Assert.False(result.Success);
        Assert.Equal(
            harness.Localizer.Format(SshLocalizationKeys.ErrorSshAgentKeyPlinkKeyType, FingerprintOf(blob)),
            result.ErrorMessage);
        Assert.Equal(0, harness.Launches);
    }

    [Fact]
    public async Task CancelDuringTheRetryDecision_ReleasesTheTunnel()
    {
        using ManualResetEventSlim release = new();
        using CancellationTokenSource cts = new();
        using Harness harness = await Harness.CreateAsync(
            [new SwitchingAgent(release, [new FakeKey("a", BlobA)])],
            attested: true,
            connectShell: (_, _, _, _, _, _, _) =>
            {
                // The retry decision then waits on an agent that never answers; the cancel lands there.
                cts.CancelAfter(TimeSpan.FromMilliseconds(150));
                throw new SshAuthenticationException("Permission denied (publickey).");
            });
        harness.UsesTunnel = true;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => harness.ConnectAsync(harness.Server(FingerprintOf(BlobA)), cts.Token));

            Assert.Equal(1, harness.TunnelReleases);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task PlinkKeyType_Certificate_IsAccepted()
    {
        const string CertType = "ssh-ed25519-cert-v01@openssh.com";
        byte[] blob = Blob(CertType, 5);
        string? keyFilePath = null;
        string? content = null;
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("cert", blob, CertType)])],
            attested: true,
            dialogAnswer: "typed-secret",
            start: (_, args) =>
            {
                keyFilePath = ExtractQuotedArgument(args, "-i");
                content = File.ReadAllText(keyFilePath, Encoding.ASCII);
            });

        ConnectionResult result = await harness.ConnectViaPlinkAsync(harness.Server(FingerprintOf(blob)));
        try
        {
            Assert.True(result.Success, result.ErrorMessage);

            // The type token comes from the blob itself.
            Assert.Equal($"{CertType} {Convert.ToBase64String(blob)}\n", content);
        }
        finally
        {
            (result.Session as TerminalSessionResult)?.Session.Dispose();
            harness.DeleteLeftovers();
        }
    }

    [Fact]
    public async Task KeyInBothAgents_PlinkPathAcceptsIt()
    {
        using Harness harness = await Harness.CreateAsync(
        [
            new FakeAgent(OpenSshPipeAgent.AgentName, [new FakeKey("a", BlobA)]),
            new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)]),
        ],
        attested: true,
        dialogAnswer: "typed-secret");

        ConnectionResult result = await harness.ConnectViaPlinkAsync(harness.Server(FingerprintOf(BlobA)));
        try
        {
            // The first agent in preference order holds the key, but Pageant holds it too.
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, harness.Launches);
        }
        finally
        {
            (result.Session as TerminalSessionResult)?.Session.Dispose();
            harness.DeleteLeftovers();
        }
    }

    [Fact]
    public async Task EachPlinkRefusal_HasItsOwnMessage()
    {
        string fingerprint = FingerprintOf(BlobA);
        string keyFile = Path.GetTempFileName();
        string[] messages = new string[5];
        try
        {
            using (Harness h = await Harness.CreateAsync(
                [new FakeAgent(OpenSshPipeAgent.AgentName, [new FakeKey("a", BlobA)])], attested: true))
            {
                messages[0] = (await h.ConnectViaPlinkAsync(h.Server(fingerprint))).ErrorMessage!;
            }

            byte[] sk = Blob("sk-ssh-ed25519@openssh.com", 1);
            using (Harness h = await Harness.CreateAsync(
                [new FakeAgent(PageantAgent.AgentName, [new FakeKey("sk", sk, "sk-ssh-ed25519@openssh.com")])],
                attested: true))
            {
                messages[1] = (await h.ConnectViaPlinkAsync(h.Server(FingerprintOf(sk)))).ErrorMessage!;
            }

            using (Harness h = await Harness.CreateAsync(
                [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])], attested: true))
            {
                ServerProfileDto withKeyFile = h.Server(fingerprint);
                withKeyFile.SshKeyPath = keyFile;
                messages[2] = (await h.ConnectViaPlinkAsync(withKeyFile)).ErrorMessage!;
            }

            using (Harness h = await Harness.CreateAsync(
                [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])]))
            {
                messages[3] = (await h.ConnectViaPlinkAsync(h.Server(fingerprint))).ErrorMessage!;
            }

            using (Harness h = await Harness.CreateAsync(
                [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])]))
            {
                ServerProfileDto external = h.Server(fingerprint);
                external.SshMode = "External";
                messages[4] = (await h.ConnectAsync(external)).ErrorMessage!;
            }
        }
        finally
        {
            File.Delete(keyFile);
        }

        Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
        Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task RetryRefusedAfterThePlinkGuardLooked_KeepsTheEmbeddedFailureAndAddsTheRefusal()
    {
        using Harness harness = await Harness.CreateAsync(
            [new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)])],
            attestedAt: call => call == 0,
            connectShell: (_, _, _, _, _, _, _) =>
                throw new SshAuthenticationException("Permission denied (publickey)."));

        ConnectionResult result = await harness.ConnectAsync(harness.Server(FingerprintOf(BlobA)));

        // The first look (the retry decision) said yes, the second (the plink guard) said no.
        string refusal = harness.Localizer[SshLocalizationKeys.ErrorSshAgentKeyPlinkNotBundled];
        Assert.False(result.Success);
        Assert.EndsWith(refusal, result.ErrorMessage, StringComparison.Ordinal);
        Assert.True(result.ErrorMessage!.Length > refusal.Length);
        Assert.Equal(0, harness.Launches);
    }

    [Fact]
    public async Task Locate_AnAgentThatDoesNotAnswerInTime_IsTreatedAsNotLoaded()
    {
        using ManualResetEventSlim release = new();
        var registry = new SshAgentRegistry([new HangingAgent(release)]);
        try
        {
            SshAgentIdentityMatch? match = await SshAgentKeyPreflight.LocateAsync(
                FingerprintOf(BlobA),
                registry,
                timeout: TimeSpan.FromMilliseconds(100));

            Assert.Null(match);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void BuildPipeModeArguments_WithAgentPublicKeyFile()
    {
        string args = SshHandler.BuildPipeModeArguments(
            keyPath: null,
            compression: false,
            agentForwarding: true,
            x11Forwarding: false,
            port: 22,
            target: "operator@server01",
            hostKeyFingerprint: "SHA256:abc",
            agentPublicKeyFilePath: "agent.pub");

        Assert.Equal(
            "-ssh -t -no-antispoof -i \"agent.pub\" -A -P 22 -hostkey \"SHA256:abc\" operator@server01",
            args);
    }

    [Fact]
    public void BuildPipeModeArguments_KeyFileAndAgentPublicKeyFile_AreNotBothAccepted()
    {
        Assert.Throws<ArgumentException>(() => SshHandler.BuildPipeModeArguments(
            keyPath: "id.ppk",
            compression: false,
            agentForwarding: false,
            x11Forwarding: false,
            port: 22,
            target: "operator@server01",
            hostKeyFingerprint: null,
            agentPublicKeyFilePath: "agent.pub"));
    }

    [Fact]
    public void NoChosenKey_PlinkArguments_AreUnchanged()
    {
        string withKeyFile = SshHandler.BuildPipeModeArguments(
            "id.ppk", true, true, false, 2222, "operator@server01", "SHA256:abc", "pw.txt", "session");
        string withNothing = SshHandler.BuildPipeModeArguments(
            null, false, false, false, 22, "server01", null);

        Assert.Equal(
            "-load session -ssh -t -no-antispoof -i \"id.ppk\" -C -A -P 2222 -hostkey \"SHA256:abc\" -pwfile \"pw.txt\" operator@server01",
            withKeyFile);
        Assert.Equal("-ssh -t -no-antispoof -P 22 server01", withNothing);
    }

    [Fact]
    public void PublicKeyLine_IsKeyTypeSpaceBase64AndNewline()
    {
        string line = SshAgentKeyPreflight.BuildPublicKeyLine(new FakeKey("ignored", BlobA));

        Assert.Equal($"{KeyType} {Convert.ToBase64String(BlobA)}\n", line);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task Localize_AgentKeyNotLoaded_NamesFingerprint(string locale)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        string fingerprint = FingerprintOf(BlobA);
        SshFailureInfo failure = FailureClassifier.Classify(new SshAgentKeyNotLoadedException(fingerprint));

        string message = SshFailureLocalizer.Localize(failure, localizer, "host").Message;

        // The catalogue's sentence with the fingerprint in its placeholder, not the English
        // sentence of the exception pushed into it.
        Assert.Equal(localizer.Format(SshLocalizationKeys.ErrorSshAgentKeyNotLoaded, fingerprint), message);
        Assert.Contains(fingerprint, message, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", message, StringComparison.Ordinal);
        Assert.DoesNotContain("reachable SSH agent", message, StringComparison.Ordinal);
    }

    private static string ExtractQuotedArgument(string args, string flag)
    {
        Match match = Regex.Match(args, flag + " \"([^\"]+)\"");
        Assert.True(match.Success, args);
        return match.Groups[1].Value;
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _plinkPath = Path.GetTempFileName();
        private readonly SshHandler _handler;
        private readonly Func<int, bool> _attestedAt;
        private readonly List<ISshAgent> _agents;
        private readonly List<string> _createdPaths = [];
        private int _attestationCalls;

        private Harness(
            List<ISshAgent> agents,
            LocalizationManager localizer,
            Func<int, bool> attestedAt,
            SshHandler.ConnectShellSession? connectShell,
            Action<string, string>? start,
            string? dialogAnswer,
            Action<string, ISshAgentKey>? writeKey,
            Action<string?>? delete)
        {
            _agents = agents;
            _attestedAt = attestedAt;
            Localizer = localizer;
            RecordingPasswordDialogProxy? dialog = null;
            if (dialogAnswer is not null)
            {
                IDialogService proxy = System.Reflection.DispatchProxy.Create<IDialogService, RecordingPasswordDialogProxy>();
                dialog = (RecordingPasswordDialogProxy)proxy;
                dialog.Answer = dialogAnswer;
                Prompts = dialog.PasswordPrompts;
                _dialog = proxy;
            }

            HostKeyStore hostKeyStore = new();
            hostKeyStore.Trust(TrustedHost, DefaultPorts.Ssh, TrustedFingerprint);
            _handler = new SshHandler(
                new CountingTunnelService(this),
                new ConnectionStateMachine(),
                localizer,
                new HostKeyStore(),
                new HostKeyTrustService(hostKeyStore),
                AutoAcceptHostKeyVerifier.Instance,
                new X11ServerManager(new InMemoryConfigManager(), localizer),

                // Nothing is asked unless a test scripts an answer: touching the dialog fails loudly.
                dialogService: _dialog!,
                plinkHostKeyProbe: new CountingProbe(this),
                plinkPasswordFileJanitor: new PlinkPasswordFileJanitor(enumerateFiles: static _ => []),
                plinkAttestation: AcquireAttestation,
                deletePlinkPasswordFile: path => (delete ?? DeleteTracked)(path),
                agentRegistryFactory: _ => new SshAgentRegistry(_agents),
                connectShellSession: (session, connectionParams, store, verifier, columns, rows, token) =>
                {
                    ShellConnects++;
                    if (connectShell is null)
                    {
                        return Task.CompletedTask;
                    }

                    return connectShell(session, connectionParams, store, verifier, columns, rows, token);
                },
                startPipeModeSession: (_, executable, args, _, _, _) =>
                {
                    Launches++;
                    foreach (string flag in new[] { "-i", "-pwfile" })
                    {
                        Match named = Regex.Match(args, flag + " \"([^\"]+)\"");
                        if (named.Success)
                        {
                            _createdPaths.Add(named.Groups[1].Value);
                        }
                    }

                    start?.Invoke(executable, args);
                    return Task.CompletedTask;
                },
                puttySessionRegistry: new InMemoryPuttySessionRegistry(),
                writeAgentKeyFile: writeKey);
        }

        private readonly IDialogService? _dialog;

        public LocalizationManager Localizer { get; }

        public List<(string Title, string Message)> Prompts { get; } = [];

        public int TunnelSetups { get; set; }

        public int HostKeyProbes { get; set; }

        public int ShellConnects { get; private set; }

        public int Launches { get; private set; }

        public static async Task<Harness> CreateAsync(
            List<ISshAgent> agents,
            bool attested = false,
            SshHandler.ConnectShellSession? connectShell = null,
            Action<string, string>? start = null,
            string? dialogAnswer = null,
            Action<string, ISshAgentKey>? writeKey = null,
            Action<string?>? delete = null,
            Func<int, bool>? attestedAt = null)
        {
            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            return new Harness(
                agents,
                localizer,
                attestedAt ?? (_ => attested),
                connectShell,
                start,
                dialogAnswer,
                writeKey,
                delete);
        }

        /// <summary>Removes the temp files of the password and the key a launch left behind.</summary>
        public void DeleteLeftovers()
        {
            foreach (string path in _createdPaths.ToList())
            {
                File.Delete(path);
            }
        }

        private void DeleteTracked(string? path)
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }

        public ServerProfileDto Server(string? fingerprint) => new()
        {
            Id = "ssh-agent-key",
            DisplayName = "agent key",
            ConnectionType = "SSH",
            RemoteServer = TrustedHost,
            SshPort = DefaultPorts.Ssh,
            SshMode = "Embedded",
            SshUsername = "operator",
            SshAgentKeyFingerprint = fingerprint,
            UseDirectConnection = true,
        };

        public Task<ConnectionResult> ConnectAsync(ServerProfileDto server) =>
            _handler.ConnectAsync(server, Settings(), CancellationToken.None);

        public Task<ConnectionResult> ConnectAsync(ServerProfileDto server, CancellationToken ct) =>
            _handler.ConnectAsync(server, Settings(), ct);

        public bool UsesTunnel { get; set; }

        public int TunnelReleases { get; set; }

        public Task<ConnectionResult> ConnectViaPlinkAsync(ServerProfileDto server) =>
            _handler.ConnectSshViaPlinkAsync(
                server,
                Settings(),
                TrustedHost,
                DefaultPorts.Ssh,
                usesTunnel: false,
                originalFailure: null,
                CancellationToken.None);

        public void Dispose()
        {
            _handler.Dispose();
            File.Delete(_plinkPath);
        }

        private AppSettings Settings() => new() { PlinkPath = _plinkPath };

        // A lease pinning the real file, the way an attested build is held, or one that grants
        // nothing: the two answers the handler distinguishes.
        private PlinkAttestationLease AcquireAttestation(string? path) =>
            _attestedAt(_attestationCalls++)
                ? new PlinkAttestationLease(
                    new FileStream(_plinkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                    _plinkPath)
                : PlinkAttestationLease.NotAttested;
    }

    private sealed class CountingTunnelService(Harness harness) : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            harness.TunnelSetups++;
            return Task.FromResult(harness.UsesTunnel
                ? new TunnelSetupOutcome(true, true, "127.0.0.1", TunnelLocalPort, null, null)
                : new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
            harness.TunnelReleases++;
        }
    }

    private sealed class CountingProbe(Harness harness) : IPlinkHostKeyProbe
    {
        public Task<PlinkHostKeyPresentation?> ProbeAsync(
            string plinkPath,
            string host,
            int port,
            string? username,
            int timeoutMs,
            CancellationToken ct)
        {
            harness.HostKeyProbes++;
            return Task.FromResult<PlinkHostKeyPresentation?>(null);
        }
    }

    /// <summary>Lists its keys once, then blocks: the first look succeeds, the next never answers.</summary>
    internal sealed class SwitchingAgent(ManualResetEventSlim release, IReadOnlyList<ISshAgentKey> identities) : ISshAgent
    {
        private int _calls;

        public string Name => PageantAgent.AgentName;

        public bool IsAvailable() => true;

        public IReadOnlyList<ISshAgentKey> GetIdentities()
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                release.Wait();
            }

            return identities;
        }
    }

    internal sealed class HangingAgent(ManualResetEventSlim release) : ISshAgent
    {
        public string Name => PageantAgent.AgentName;

        public bool IsAvailable() => true;

        public IReadOnlyList<ISshAgentKey> GetIdentities()
        {
            release.Wait();
            return [];
        }
    }

    /// <summary>Answers the password prompt with a scripted value and records the ask.</summary>
    internal class RecordingPasswordDialogProxy : System.Reflection.DispatchProxy
    {
        public List<(string Title, string Message)> PasswordPrompts { get; } = [];

        public string? Answer { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowPasswordInputAsync))
            {
                PasswordPrompts.Add(((string)args![0]!, (string)args![1]!));
                return Task.FromResult(Answer);
            }

            throw new NotSupportedException($"unexpected dialog call: {targetMethod?.Name}");
        }
    }

    internal sealed class FakeAgent(
        string name,
        IReadOnlyList<ISshAgentKey> identities,
        bool available = true) : ISshAgent
    {
        public string Name { get; } = name;

        public bool IsAvailable() => available;

        public IReadOnlyList<ISshAgentKey> GetIdentities() => identities;
    }

    internal sealed class FakeKey(string comment, byte[] publicKeyBlob, string? keyType = null) : ISshAgentKey
    {
        public string Comment => comment;

        public string KeyType => keyType ?? SshHandlerAgentKeyTests.KeyType;

        public byte[] PublicKeyBlob => publicKeyBlob;

        public byte[] Sign(byte[] data, SshAgentSignFlags flags) => [1];
    }
}
