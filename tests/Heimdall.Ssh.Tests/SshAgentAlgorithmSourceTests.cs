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

using System.Reflection;
using Heimdall.Core.Ssh;
using Heimdall.Ssh.Agents;
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

public sealed class SshAgentAlgorithmSourceTests
{
    private const string Rsa = "ssh-rsa";

    [Fact]
    public void Order_TwoRsaKeys_IsAlgorithmMajor()
    {
        var k1 = new FakeAgentKey(Rsa, "k1");
        var k2 = new FakeAgentKey(Rsa, "k2");

        var order = Describe(SshAgentAlgorithmSource.Order([k1, k2]));

        Assert.Equal(
            [
                "k1:rsa-sha2-512", "k2:rsa-sha2-512",
                "k1:rsa-sha2-256", "k2:rsa-sha2-256",
                "k1:ssh-rsa", "k2:ssh-rsa",
            ],
            order);
    }

    [Fact]
    public void Order_RsaAndEd25519_PutsNonRsaKeyInFirstPassOnce()
    {
        var rsa = new FakeAgentKey(Rsa, "rsa");
        var ed = new FakeAgentKey("ssh-ed25519", "ed");

        var order = Describe(SshAgentAlgorithmSource.Order([rsa, ed]));

        Assert.Equal(
            ["rsa:rsa-sha2-512", "ed:ssh-ed25519", "rsa:rsa-sha2-256", "rsa:ssh-rsa"],
            order);
    }

    [Fact]
    public void Order_SingleRsaKey_ListsThreeAlgorithms()
    {
        var order = Describe(SshAgentAlgorithmSource.Order([new FakeAgentKey(Rsa, "k")]));

        Assert.Equal(["k:rsa-sha2-512", "k:rsa-sha2-256", "k:ssh-rsa"], order);
    }

    [Fact]
    public void Order_EmptyAgent_GivesNothing()
    {
        Assert.Empty(SshAgentAlgorithmSource.Order([]));
    }

    [Theory]
    [InlineData("rsa-sha2-512", SshAgentSignFlags.RsaSha2_512)]
    [InlineData("rsa-sha2-256", SshAgentSignFlags.RsaSha2_256)]
    [InlineData("ssh-rsa", SshAgentSignFlags.None)]
    public void Source_ExposesOneAlgorithm_AndSignsWithItsFlags(string algorithm, SshAgentSignFlags expectedFlags)
    {
        var key = new FakeAgentKey(Rsa, "k");
        var attempt = SshAgentAlgorithmSource.Order([key]).Single(a => a.Algorithm == algorithm);
        var source = new SshAgentAlgorithmSource(attempt);

        var hostAlgorithm = Assert.Single(source.HostKeyAlgorithms);
        var signature = hostAlgorithm.Sign([9, 9]);

        Assert.Equal(algorithm, hostAlgorithm.Name);
        Assert.Equal([4, 5, 6], signature);
        Assert.Equal(expectedFlags, key.LastSignFlags);
        Assert.Equal(key.PublicKeyBlob, hostAlgorithm.Data);
    }

    [Fact]
    public void Factory_AgentAuth_ExposesAlgorithmMajorOrder_OneAlgorithmPerSource_AllKeysOffered()
    {
        var k1 = new FakeAgentKey(Rsa, "k1");
        var k2 = new FakeAgentKey(Rsa, "k2");
        var ed = new FakeAgentKey("ssh-ed25519", "ed");
        var registry = new SshAgentRegistry([new FakeAgent("agent", [k1, k2, ed])]);
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = null,
            KeyPath = null,
        };

        var method = typeof(SshConnectionFactory)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "BuildAuthMethods" && m.GetParameters().Length == 2);
        var methods = (List<AuthenticationMethod>)method.Invoke(null, [connParams, registry])!;
        var agentMethod = methods.OfType<PrivateKeyAuthenticationMethod>().Single();

        var sources = agentMethod.KeyFiles.ToList();
        Assert.All(sources, source => Assert.Single(source.HostKeyAlgorithms));

        var flattened = sources
            .SelectMany(source => source.HostKeyAlgorithms)
            .ToList();
        var keysById = new[] { k1, k2, ed }.ToDictionary(key => key.Id);
        var actual = flattened
            .Select(algorithm => $"{keysById.Values.Single(key => key.PublicKeyBlob.SequenceEqual(algorithm.Data)).Id}:{algorithm.Name}")
            .ToList();

        // Every position: key and algorithm, so every key of the agent is offered.
        Assert.Equal(
            [
                "k1:rsa-sha2-512", "k2:rsa-sha2-512", "ed:ssh-ed25519",
                "k1:rsa-sha2-256", "k2:rsa-sha2-256",
                "k1:ssh-rsa", "k2:ssh-rsa",
            ],
            actual);
    }

    private static List<string> Describe(IReadOnlyList<SshAgentAttempt> attempts) =>
        attempts
            .Select(a => $"{((FakeAgentKey)a.Key).Id}:{a.Algorithm}")
            .ToList();

    private sealed class FakeAgent(string name, IReadOnlyList<ISshAgentKey> identities) : ISshAgent
    {
        public string Name { get; } = name;
        public bool IsAvailable() => true;
        public IReadOnlyList<ISshAgentKey> GetIdentities() => identities;
    }

    private sealed class FakeAgentKey(string keyType, string id) : ISshAgentKey
    {
        public string Id { get; } = id;
        public SshAgentSignFlags LastSignFlags { get; private set; }
        public string Comment => Id;
        public string KeyType => keyType;

        // Distinct per key so a test can tell which key an algorithm entry belongs to.
        public byte[] PublicKeyBlob { get; } =
            [.. OpenSshAgentProtocolTests.BuildKeyBlob(keyType), .. System.Text.Encoding.ASCII.GetBytes(id)];

        public byte[] Sign(byte[] data, SshAgentSignFlags flags)
        {
            LastSignFlags = flags;
            return [4, 5, 6];
        }
    }
}
