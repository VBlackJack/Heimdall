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

using Heimdall.Core.Ssh;
using Heimdall.Ssh.Agents;

namespace Heimdall.Ssh.Tests;

public class AuthPreflightCheckerTests
{
    private static SshConnectionParams MakeParams(
        string? keyPath = null,
        string? password = null,
        string? keyPassphrase = null) =>
        new()
        {
            Host = "server.example.com",
            Username = "testuser",
            KeyPath = keyPath,
            Password = password,
            KeyPassphrase = keyPassphrase
        };

    // ── Key file existence ─────────────────────────────────────────────

    [Fact]
    public void Check_KeyFileNotFound_ReturnsFailure()
    {
        var connParams = MakeParams(keyPath: @"C:\nonexistent\key.pem");

        var result = AuthPreflightChecker.Check(connParams);

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.KeyFileNotFound, result.FailureCode);
        // A-10: the message is the locale key the application already owns, with the path
        // as its argument, instead of an English sentence composed here.
        Assert.Equal("ErrorSshKeyFileNotFound", result.Message);
        Assert.Equal(@"C:\nonexistent\key.pem", Assert.Single(result.MessageArguments));
    }

    [Fact]
    public void Check_KeyFileExists_ReturnsOk()
    {
        // Use a file that exists on any Windows system
        var connParams = MakeParams(keyPath: @"C:\Windows\System32\drivers\etc\hosts");

        var result = AuthPreflightChecker.Check(connParams);

        Assert.True(result.Success);
    }

    // ── No key, no password, interactive mode ──────────────────────────

    [Fact]
    public void Check_NoKeyNoPassword_InteractiveMode_ReturnsOk()
    {
        // Interactive mode does not require Pageant (user can type password)
        var connParams = MakeParams();

        var result = AuthPreflightChecker.Check(connParams, isTunnelMode: false);

        Assert.True(result.Success);
    }

    // ── Password-only auth ─────────────────────────────────────────────

    [Fact]
    public void Check_PasswordOnly_TunnelMode_ReturnsOk()
    {
        var connParams = MakeParams(password: "mysecret");

        var result = AuthPreflightChecker.Check(connParams, isTunnelMode: true);

        Assert.True(result.Success);
    }

    // ── Key + password (passphrase) ────────────────────────────────────

    [Fact]
    public void Check_KeyWithPassword_TunnelMode_ReturnsOk()
    {
        var connParams = MakeParams(
            keyPath: @"C:\Windows\System32\drivers\etc\hosts",
            password: "passphrase");

        var result = AuthPreflightChecker.Check(connParams, isTunnelMode: true);

        Assert.True(result.Success);
    }

    [Fact]
    public void Check_NoKeyNoPassword_TunnelMode_NoAgent_ReturnsGenericAgentFailure()
    {
        var connParams = MakeParams();
        var registry = new SshAgentRegistry(
            [new FakeAgent("Windows OpenSSH Agent", available: false, [])]);

        var result = AuthPreflightChecker.Check(connParams, isTunnelMode: true, agentRegistry: registry);

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.PageantKeyUnavailable, result.FailureCode);
        Assert.Equal("ErrorNoSshAgentRunning", result.Message);
    }

    [Fact]
    public void Check_NoKeyNoPassword_TunnelMode_EmptyAgent_ReturnsNoIdentities()
    {
        var connParams = MakeParams();
        var registry = new SshAgentRegistry(
            [new FakeAgent("Windows OpenSSH Agent", available: true, [])]);

        var result = AuthPreflightChecker.Check(connParams, isTunnelMode: true, agentRegistry: registry);

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.PageantNoIdentities, result.FailureCode);
        Assert.Equal("ErrorSshAgentHasNoIdentities", result.Message);
    }

    [Fact]
    public void Check_NoKeyNoPassword_TunnelMode_AgentWithIdentity_ReturnsOk()
    {
        var connParams = MakeParams();
        var registry = new SshAgentRegistry(
            [new FakeAgent("Windows OpenSSH Agent", available: true, [new FakeAgentKey()])]);

        var result = AuthPreflightChecker.Check(connParams, isTunnelMode: true, agentRegistry: registry);

        Assert.True(result.Success);
    }

    [Fact]
    public void Check_KeyWithKeyPassphraseOnly_TunnelMode_ReturnsOk()
    {
        var connParams = MakeParams(
            keyPath: @"C:\Windows\System32\drivers\etc\hosts",
            keyPassphrase: "passphrase");

        var result = AuthPreflightChecker.Check(connParams, isTunnelMode: true);

        Assert.True(result.Success);
    }

    // ── Null params ────────────────────────────────────────────────────

    [Fact]
    public void Check_NullParams_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => AuthPreflightChecker.Check(null!));
    }

    // ── PreflightResult factory methods ────────────────────────────────

    [Fact]
    public void PreflightResult_Ok_HasCorrectValues()
    {
        var result = PreflightResult.Ok();

        Assert.True(result.Success);
        Assert.Null(result.FailureCode);
        Assert.Null(result.Message);
    }

    [Fact]
    public void PreflightResult_Fail_HasCorrectValues()
    {
        var result = PreflightResult.Fail(SshFailureCode.KeyFileNotFound, "Not found");

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.KeyFileNotFound, result.FailureCode);
        Assert.Equal("Not found", result.Message);
    }

    private sealed class FakeAgent(
        string name,
        bool available,
        IReadOnlyList<ISshAgentKey> identities) : ISshAgent
    {
        public string Name { get; } = name;
        public bool IsAvailable() => available;
        public IReadOnlyList<ISshAgentKey> GetIdentities() => identities;
    }

    private sealed class FakeAgentKey : ISshAgentKey
    {
        public string Comment => "fake";
        public string KeyType => "ssh-ed25519";
        public byte[] PublicKeyBlob => [0, 0, 0, 11, 115, 115, 104, 45, 101, 100, 50, 53, 53, 49, 57];
        public byte[] Sign(byte[] data, SshAgentSignFlags flags) => [1];
    }
}
