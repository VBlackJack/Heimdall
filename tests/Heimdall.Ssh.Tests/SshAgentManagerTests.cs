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

using System.Buffers.Binary;
using System.Text;
using Heimdall.Core.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.OpenSsh;

namespace Heimdall.Ssh.Tests;

public sealed class SshAgentManagerTests
{
    private sealed class FakeAgentKey(byte[] blob, string keyType, string comment) : ISshAgentKey
    {
        public byte[] PublicKeyBlob { get; } = blob;
        public string KeyType { get; } = keyType;
        public string Comment { get; } = comment;
        public byte[] Sign(byte[] data, SshAgentSignFlags flags = SshAgentSignFlags.None) => [];
    }

    private sealed class FakeMutableAgent : IMutableSshAgent
    {
        public string Name => "FakeAgent";
        public bool Available { get; set; } = true;
        public List<byte[]> AddedMessages { get; } = [];
        public List<byte[]> RemovedBlobs { get; } = [];
        public bool RemovedAll { get; private set; }
        public List<ISshAgentKey> Keys { get; } = [];

        public bool IsAvailable() => Available;
        public IReadOnlyList<ISshAgentKey> GetIdentities() => Keys;

        public bool AddIdentity(byte[] addIdentityWireMessage)
        {
            AddedMessages.Add((byte[])addIdentityWireMessage.Clone());
            return true;
        }

        public bool RemoveIdentity(byte[] publicKeyBlob)
        {
            RemovedBlobs.Add((byte[])publicKeyBlob.Clone());
            Keys.RemoveAll(k => k.PublicKeyBlob.SequenceEqual(publicKeyBlob));
            return true;
        }

        public bool RemoveAllIdentities()
        {
            RemovedAll = true;
            Keys.Clear();
            return true;
        }
    }

    [Theory]
    [InlineData(SshKeyAlgorithm.Ed25519, null)]
    [InlineData(SshKeyAlgorithm.Ed25519, 3600u)]
    [InlineData(SshKeyAlgorithm.Rsa2048, null)]
    [InlineData(SshKeyAlgorithm.EcdsaP256, 7200u)]
    public void BuildAddIdentityMessage_ProducesValidProtocolAgentFraming(
        SshKeyAlgorithm algorithm,
        uint? lifetimeSeconds)
    {
        var pair = SshKeyPairGenerator.Generate(algorithm, "agent@test", "secret-pass", SshPrivateKeyFormat.OpenSsh);

        var wire = SshAgentKeyPayloadBuilder.BuildAddIdentityMessage(
            pair.PrivateKey,
            "secret-pass",
            "agent@test",
            lifetimeSeconds);

        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(0, 4));
        Assert.Equal((uint)(wire.Length - 4), declaredLength);

        var expectedType = lifetimeSeconds.HasValue
            ? OpenSshAgentProtocol.SshAgentcAddIdConstrained
            : OpenSshAgentProtocol.SshAgentcAddIdentity;
        Assert.Equal(expectedType, wire[4]);

        if (lifetimeSeconds.HasValue)
        {
            Assert.Equal(OpenSshAgentProtocol.SshAgentConstrainLifetime, wire[^5]);
            Assert.Equal(lifetimeSeconds.Value, BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(wire.Length - 4, 4)));
        }
    }

    [Fact]
    public void SshAgentManager_AddsAndRemovesKeysViaMutableAgent()
    {
        var agent = new FakeMutableAgent();
        var registry = new SshAgentRegistry([agent]);
        var pair = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, "demo@host", "", SshPrivateKeyFormat.OpenSsh);

        var addResult = SshAgentManager.AddKey(registry, pair.PrivateKey, null, "demo@host", lifetimeSeconds: 1800);
        Assert.True(addResult.Success);
        Assert.Equal("FakeAgent", addResult.AgentName);
        Assert.Single(agent.AddedMessages);

        // Simulate the agent now holding the public key blob
        var pubBlob = Convert.FromBase64String(pair.PublicKeyOpenSsh.Split(' ')[1]);
        var fp = SshAgentKeyFingerprints.Compute(pubBlob);
        agent.Keys.Add(new FakeAgentKey(pubBlob, "ssh-ed25519", "demo@host"));

        var removeResult = SshAgentManager.RemoveKeyByFingerprint(registry, fp);
        Assert.True(removeResult.Success);
        Assert.Single(agent.RemovedBlobs);
        Assert.Empty(agent.Keys);

        var clearResult = SshAgentManager.RemoveAllKeys(registry);
        Assert.True(clearResult.Success);
        Assert.True(agent.RemovedAll);
    }

    [Fact]
    public void OpenSshAgentProtocol_RemoveAndSuccessParsing_WorkCorrectly()
    {
        var blob = Encoding.ASCII.GetBytes("sample-key-blob");
        var removeMsg = OpenSshAgentProtocol.BuildRemoveIdentityMessage(blob);
        Assert.Equal(OpenSshAgentProtocol.SshAgentcRemoveIdentity, removeMsg[4]);

        var okResponse = new byte[] { 0, 0, 0, 1, OpenSshAgentProtocol.SshAgentSuccess };
        var failResponse = new byte[] { 0, 0, 0, 1, OpenSshAgentProtocol.SshAgentFailure };
        Assert.True(OpenSshAgentProtocol.ParseSuccessResponse(okResponse));
        Assert.False(OpenSshAgentProtocol.ParseSuccessResponse(failResponse));
    }
}
