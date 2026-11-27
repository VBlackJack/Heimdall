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

namespace Heimdall.Sftp.Tests;

public sealed class SftpPinnedVerifierSelectionTests
{
    [Fact]
    public void PreResolvedHostKeyVerifier_IsReusedEvenOnDualAlgorithmHost_UnlessRevoked()
    {
        var store = new HostKeyStore();
        var trustService = new HostKeyTrustService(store);
        trustService.Trust("dual.internal", 22, "SHA256:ed25519fp", "ssh-ed25519", HostKeySource.UserConfirmed);
        store.TrustAdditionalAlgorithm("dual.internal", 22, "SHA256:rsafp", "ssh-rsa", HostKeySource.UserConfirmed);

        var preResolved = new PinnedFingerprintVerifier("dual.internal", 22, "SHA256:rsafp");
        var parameters = new SshConnectionParams
        {
            Host = "dual.internal",
            Port = 22,
            Username = "ops",
            SkipHostKeyProbeIfTrusted = true,
            PreResolvedHostKeyVerifier = preResolved
        };

        var selected = SftpBrowser.TrySelectPinnedHostKeyVerifier(parameters, store);
        Assert.Same(preResolved, selected);

        // Once revoked, the pre-resolved verifier must NOT be reused.
        store.AddRevokedKey("dual.internal", "SHA256:rsafp");
        Assert.Null(SftpBrowser.TrySelectPinnedHostKeyVerifier(parameters, store));
    }

    [Fact]
    public void PreResolvedHostKeyVerifier_WithHostOrPortMismatch_IsIgnored()
    {
        var store = new HostKeyStore();
        var preResolved = new PinnedFingerprintVerifier("other.internal", 22, "SHA256:fp");
        var parameters = new SshConnectionParams
        {
            Host = "target.internal",
            Port = 22,
            Username = "ops",
            SkipHostKeyProbeIfTrusted = false,
            PreResolvedHostKeyVerifier = preResolved
        };

        Assert.Null(SftpBrowser.TrySelectPinnedHostKeyVerifier(parameters, store));
    }
}
