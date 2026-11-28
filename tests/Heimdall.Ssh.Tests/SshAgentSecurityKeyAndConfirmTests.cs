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
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;

namespace Heimdall.Ssh.Tests;

public sealed class SshAgentSecurityKeyAndConfirmTests
{
    private sealed class FakeAgentKey(string keyType, byte[] blob) : ISshAgentKey
    {
        public int? LastTimeoutMs { get; private set; }
        public string Comment => "yubikey";
        public string KeyType => keyType;
        public byte[] PublicKeyBlob => blob;

        public byte[] Sign(byte[] data, SshAgentSignFlags flags) => [1, 2, 3];

        public byte[] Sign(byte[] data, SshAgentSignFlags flags, int timeoutMilliseconds)
        {
            LastTimeoutMs = timeoutMilliseconds;
            return [1, 2, 3];
        }
    }

    [Fact]
    public void SshAgentHostAlgorithm_DeclinedConfirmation_ThrowsAndClassifiesAsAgentSigningDeclined()
    {
        var key = new FakeAgentKey("ssh-ed25519", new byte[32]);
        var alg = new SshAgentHostAlgorithm(
            "ssh-ed25519",
            key,
            confirmSign: _ => false,
            targetHost: "prod.corp",
            targetPort: 22);

        var ex = Assert.Throws<SshAgentSigningDeclinedException>(() => alg.Sign([9, 8, 7]));
        var classified = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.AgentSigningDeclined, classified.Code);
        Assert.True(classified.IsFatal);
    }

    [Fact]
    public void SshAgentHostAlgorithm_SecurityKey_InvokesBeforeSignAndUses30sTimeout()
    {
        var key = new FakeAgentKey("sk-ssh-ed25519@openssh.com", new byte[32]);
        Assert.True(((ISshAgentKey)key).IsSecurityKey);
        bool notified = false;

        var alg = new SshAgentHostAlgorithm(
            "sk-ssh-ed25519@openssh.com",
            key,
            onBeforeSign: k => notified = ReferenceEquals(k, key),
            confirmSign: _ => true);

        byte[] sig = alg.Sign([4, 5, 6]);

        Assert.NotEmpty(sig);
        Assert.True(notified);
        Assert.Equal(ISshAgentKey.SecurityKeySignTimeoutMilliseconds, key.LastTimeoutMs);
    }
}
