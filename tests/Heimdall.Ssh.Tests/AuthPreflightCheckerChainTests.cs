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

/// <summary>
/// A hop that cannot sign in is knowable before the chain is dialled up to it,
/// whichever hop it is.
/// </summary>
public sealed class AuthPreflightCheckerChainTests
{
    private const string ExistingKeyPath = @"C:\Windows\System32\drivers\etc\hosts";

    private static SshConnectionParams Hop(string host, string? keyPath = null, string? password = null) =>
        new()
        {
            Host = host,
            Username = "ssh-user",
            KeyPath = keyPath,
            Password = password
        };

    [Fact]
    public void CheckChain_LaterHopDependsOnAnAbsentAgent_FailsAtThatHop()
    {
        SshAgentRegistry registry = new SshAgentRegistry(
            [new FakeAgent("Windows OpenSSH Agent", available: false, [])]);

        ChainPreflightResult result = AuthPreflightChecker.CheckChain(
            [Hop("root.example.test", keyPath: ExistingKeyPath), Hop("leaf.example.test")],
            isTunnelMode: true,
            registry);

        Assert.False(result.Result.Success);
        Assert.Equal(1, result.FailedHopIndex);
        Assert.Equal(SshFailureCode.PageantKeyUnavailable, result.Result.FailureCode);
    }

    [Fact]
    public void CheckChain_LaterHopHasAMissingKeyFile_FailsAtThatHop()
    {
        ChainPreflightResult result = AuthPreflightChecker.CheckChain(
            [
                Hop("root.example.test", keyPath: ExistingKeyPath),
                Hop("middle.example.test", password: "stored"),
                Hop("leaf.example.test", keyPath: @"C:\nonexistent\leaf.pem")
            ],
            isTunnelMode: true);

        Assert.False(result.Result.Success);
        Assert.Equal(2, result.FailedHopIndex);
        Assert.Equal(SshFailureCode.KeyFileNotFound, result.Result.FailureCode);
    }

    [Fact]
    public void CheckChain_FirstFailingHopWins()
    {
        ChainPreflightResult result = AuthPreflightChecker.CheckChain(
            [
                Hop("root.example.test", keyPath: @"C:\nonexistent\root.pem"),
                Hop("leaf.example.test", keyPath: @"C:\nonexistent\leaf.pem")
            ],
            isTunnelMode: true);

        Assert.False(result.Result.Success);
        Assert.Equal(0, result.FailedHopIndex);
        Assert.Contains("root.pem", result.Result.Message);
    }

    [Fact]
    public void CheckChain_EveryHopUsable_PassesWithNoFailingHop()
    {
        SshAgentRegistry registry = new SshAgentRegistry(
            [new FakeAgent("Windows OpenSSH Agent", available: true, [new FakeAgentKey()])]);

        ChainPreflightResult result = AuthPreflightChecker.CheckChain(
            [Hop("root.example.test", keyPath: ExistingKeyPath), Hop("leaf.example.test")],
            isTunnelMode: true,
            registry);

        Assert.True(result.Result.Success);
        Assert.Equal(-1, result.FailedHopIndex);
    }

    [Fact]
    public void CheckChain_NullChain_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => AuthPreflightChecker.CheckChain(null!));
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
