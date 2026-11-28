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

using Heimdall.Ssh;

namespace Heimdall.Ssh.Tests;

public sealed class SshKeyRotatorTests
{
    [Fact]
    public void BuildRemoveOldKeyCommand_ContainsSymlinkGuardSameDirMktempFixedGrepAndNewKeyInvariant()
    {
        var oldPair = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "old@host");
        var newPair = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "new@host");

        string cmd = SshKeyRotator.BuildRemoveOldKeyCommand(oldPair.PublicKeyOpenSsh, newPair.PublicKeyOpenSsh);

        Assert.Contains("[ -L \"$ak\" ]", cmd, StringComparison.Ordinal);
        Assert.Contains(SshKeyRotator.SymlinkRefusedMarker, cmd, StringComparison.Ordinal);
        Assert.Contains("mktemp \"$HOME/.ssh/.authorized_keys.heimdall.XXXXXX\"", cmd, StringComparison.Ordinal);
        Assert.Contains("grep -Fv -- \"$old_id\" \"$ak\" > \"$tmp\"", cmd, StringComparison.Ordinal);
        Assert.Contains("[ -s \"$tmp\" ] && grep -Fq -- \"$new_id\" \"$tmp\"", cmd, StringComparison.Ordinal);
        Assert.Contains(SshKeyRotator.InvariantFailedMarker, cmd, StringComparison.Ordinal);
        Assert.Contains(SshKeyRotator.OldKeyRemovedMarker, cmd, StringComparison.Ordinal);
        Assert.Contains("if chmod 600 \"$tmp\" &&", cmd, StringComparison.Ordinal);
        Assert.Contains("else rm -f \"$tmp\";", cmd, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRemoveOldKeyCommand_IdenticalOrInvalidKeys_ThrowsArgumentException()
    {
        var pair = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "same");

        Assert.Throws<ArgumentException>(() =>
            SshKeyRotator.BuildRemoveOldKeyCommand(pair.PublicKeyOpenSsh, pair.PublicKeyOpenSsh));
        Assert.Throws<ArgumentException>(() =>
            SshKeyRotator.BuildRemoveOldKeyCommand("not-a-key", pair.PublicKeyOpenSsh));
    }

    [Fact]
    public void BuildIsolatedVerificationParams_DisablesAgentPasswordCertAndKbdInteractive()
    {
        var baseParams = new SshConnectionParams
        {
            Host = "127.0.0.1",
            Port = 54321,
            LogicalHost = "prod.corp",
            LogicalPort = 22,
            Username = "admin",
            Password = "secret",
            CertificatePath = "C:\\old-cert.pub",
            AgentKeyFingerprint = "SHA256:abc",
            KeyboardInteractiveResponder = _ => "123456"
        };

        var isolated = SshKeyRotator.BuildIsolatedVerificationParams(baseParams, "C:\\new_key", "pass");

        Assert.True(isolated.DisableAgentAuthentication);
        Assert.Null(isolated.Password);
        Assert.Null(isolated.CertificatePath);
        Assert.Null(isolated.AgentKeyFingerprint);
        Assert.Null(isolated.KeyboardInteractiveResponder);
        Assert.Equal("C:\\new_key", isolated.KeyPath);
        Assert.Equal("pass", isolated.KeyPassphrase);
        Assert.Equal("prod.corp", isolated.HostKeyVerificationHost);
        Assert.Equal(22, isolated.HostKeyVerificationPort);
    }
}
