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
using Heimdall.Ssh.Pageant;

namespace Heimdall.Ssh.Tests;

public sealed class SshAgentKeyMemoryAndInspectorTests
{
    [Fact]
    public void SshAgentKeyMemory_Prioritize_MovesRememberedKeyFirstAndPreservesOrder()
    {
        var memory = new SshAgentKeyMemory(maxEntries: 4);
        var keyA = new FakeAgentKey("ssh-ed25519", [1, 2, 3, 4], "key-a");
        var keyB = new FakeAgentKey("ssh-rsa", [5, 6, 7, 8], "key-b");
        var keyC = new FakeAgentKey("ssh-ed25519", [9, 10, 11, 12], "key-c");
        var fpC = SshAgentKeyFingerprints.Compute(keyC.PublicKeyBlob);

        memory.Remember("prod.example.com", 22, fpC);

        var ordered = memory.Prioritize("PROD.EXAMPLE.COM", 22, [keyA, keyB, keyC]);

        Assert.Equal([keyC, keyA, keyB], ordered);
    }

    [Fact]
    public void SshAgentKeyMemory_RespectsMaximumCapacityBound()
    {
        var memory = new SshAgentKeyMemory(maxEntries: 2);
        memory.Remember("h1.test", 22, "SHA256:aaa");
        memory.Remember("h2.test", 22, "SHA256:bbb");
        memory.Remember("h3.test", 22, "SHA256:ccc");

        Assert.True(memory.Count <= 2);
        Assert.Equal("SHA256:ccc", memory.GetPreferredFingerprint("h3.test", 22));
    }

    [Fact]
    public void SshAgentInspector_ListsKeysAcrossHealthyAgentsAndSkipsFailingOnes()
    {
        var key = new FakeAgentKey("ssh-ed25519", [10, 20, 30, 40], "ops@workstation");
        var registry = new SshAgentRegistry(
        [
            new ThrowingAgent("BrokenAgent"),
            new FakeAgent("OpenSSH", [key])
        ]);

        var listed = SshAgentInspector.ListLoadedKeys(registry);

        var info = Assert.Single(listed);
        Assert.Equal("OpenSSH", info.AgentName);
        Assert.Equal("ssh-ed25519", info.KeyType);
        Assert.Equal("ops@workstation", info.Comment);
        Assert.Equal(SshAgentKeyFingerprints.Compute(key.PublicKeyBlob), info.Fingerprint);
        Assert.StartsWith("ssh-ed25519 ", info.PublicKeyOpenSsh, StringComparison.Ordinal);
        Assert.EndsWith(" ops@workstation", info.PublicKeyOpenSsh, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pageant", true)]
    [InlineData("gpg-agent", true)]
    [InlineData("GPG-AGENT", true)]
    [InlineData("wsl2-ssh-pageant", true)]
    [InlineData("ssh-pageant", true)]
    [InlineData("keepassxc", true)]
    [InlineData("powershell", false)]
    [InlineData("cmd", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PageantClient_IsTrustedProcessName_AllowsKnownAgentsAndRejectsOthers(
        string? processName,
        bool expected)
    {
        Assert.Equal(expected, PageantClient.IsTrustedProcessName(processName));
    }

    [Fact]
    public void SshPublicKeyDeployer_ValidatesKeyAndEncodesCommandStrictlyAsBase64()
    {
        var generated = SshKeyPairGenerator.Generate(
            SshKeyAlgorithm.Ed25519,
            comment: "alice@host $(whoami); rm -rf /",
            passphrase: null);

        Assert.True(SshPublicKeyDeployer.TryValidatePublicKeyLine(generated.PublicKeyOpenSsh, out var normalized));

        var command = SshPublicKeyDeployer.BuildDeployCommand(normalized);
        Assert.DoesNotContain("$(whoami)", command, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf", command, StringComparison.Ordinal);
        Assert.Contains("base64 -d", command, StringComparison.Ordinal);
        Assert.Contains("chmod 700", command, StringComparison.Ordinal);
        Assert.Contains("chmod 600", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI\nmalicious")]
    [InlineData("unknown-algo AAAAC3NzaC1lZDI1NTE5AAAAIAECAwQFBgcICQoLDA0ODw==")]
    [InlineData("ssh-ed25519 not_valid_base64!!!")]
    public void SshPublicKeyDeployer_RejectsMalformedOrMultilinePublicKeys(string candidate)
    {
        Assert.False(SshPublicKeyDeployer.TryValidatePublicKeyLine(candidate, out _));
    }

    [Fact]
    public void HostKeyTrustService_Verify_SupportsAdditionalTrustedAlgorithmWithoutReplacingPrimary()
    {
        var store = new HostKeyStore();
        var trustService = new HostKeyTrustService(store);
        var rsaFp = HostKeyFormats.ComputeSha256Fingerprint([1, 2, 3, 4]);
        var edFp = HostKeyFormats.ComputeSha256Fingerprint([5, 6, 7, 8]);
        var rogueFp = HostKeyFormats.ComputeSha256Fingerprint([9, 9, 9, 9]);

        trustService.Trust("dual.example.com", 22, rsaFp, "ssh-rsa", HostKeySource.UserConfirmed);
        store.TrustAdditionalAlgorithm("dual.example.com", 22, edFp, "ssh-ed25519", HostKeySource.UserConfirmed);

        var rsaVerify = trustService.Verify("dual.example.com", 22, rsaFp, "ssh-rsa");
        var edVerify = trustService.Verify("dual.example.com", 22, edFp, "ssh-ed25519");
        var rogueVerify = trustService.Verify("dual.example.com", 22, rogueFp, "ssh-ed25519");

        Assert.True(rsaVerify.Trusted);
        Assert.False(rsaVerify.FirstUse);
        Assert.True(edVerify.Trusted);
        Assert.False(edVerify.FirstUse);
        Assert.False(rogueVerify.Trusted);
        Assert.Equal(2, store.GetEntries("dual.example.com", 22).Count);

        Assert.True(store.Remove("dual.example.com", 22));
        Assert.Empty(store.GetEntries("dual.example.com", 22));
    }

    private sealed class FakeAgentKey(string keyType, byte[] publicKeyBlob, string comment) : ISshAgentKey
    {
        public string KeyType { get; } = keyType;
        public byte[] PublicKeyBlob { get; } = publicKeyBlob;
        public string Comment { get; } = comment;
        public byte[] Sign(byte[] data, SshAgentSignFlags flags = SshAgentSignFlags.None) => [];
    }

    private sealed class FakeAgent(string name, IReadOnlyList<ISshAgentKey> keys) : ISshAgent
    {
        public string Name { get; } = name;
        public bool IsAvailable() => true;
        public IReadOnlyList<ISshAgentKey> GetIdentities() => keys;
    }

    private sealed class ThrowingAgent(string name) : ISshAgent
    {
        public string Name { get; } = name;
        public bool IsAvailable() => true;
        public IReadOnlyList<ISshAgentKey> GetIdentities() => throw new InvalidOperationException("agent busy");
    }
}
