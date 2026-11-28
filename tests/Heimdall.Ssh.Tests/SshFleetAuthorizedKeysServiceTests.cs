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

namespace Heimdall.Ssh.Tests;

public sealed class SshFleetAuthorizedKeysServiceTests
{
    [Fact]
    public async Task ScanFleetAndRevoke_FiltersByQueryNeutralizesCsvAndSerializesSameHostRevocations()
    {
        var keyTarget = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "compromised-laptop");
        var keyKeep = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "ops-primary");
        string contentA = $"{keyKeep.PublicKeyOpenSsh}\nfrom=\"10.0.0.1\" {keyTarget.PublicKeyOpenSsh}\n";
        string contentB = $"=HYPERLINK(\"http://evil\") {keyTarget.PublicKeyOpenSsh}\n";

        var parsedA = SshAuthorizedKeysService.ParseLines(contentA);
        var parsedB = SshAuthorizedKeysService.ParseLines(contentB);

        var targets = new[]
        {
            new SshFleetAuthorizedKeysTarget("s1", "Prod-A", new SshConnectionParams { Host = "a.example", Username = "ops" }),
            new SshFleetAuthorizedKeysTarget("s2", "Prod-B", new SshConnectionParams { Host = "b.example", Username = "ops" }),
            new SshFleetAuthorizedKeysTarget("s3", "Offline-C", new SshConnectionParams { Host = "c.example", Username = "ops" })
        };

        var store = new HostKeyStore();
        var verifier = new StrictVerifier();

        var results = await SshFleetAuthorizedKeysService.ScanFleetAsync(
            targets,
            keyTarget.Fingerprint,
            store,
            verifier,
            maxConcurrency: 2,
            listRunner: (target, _) => target.ServerId switch
            {
                "s1" => Task.FromResult(new SshAuthorizedKeysListResult(true, parsedA)),
                "s2" => Task.FromResult(new SshAuthorizedKeysListResult(true, parsedB)),
                _ => Task.FromResult(new SshAuthorizedKeysListResult(false, [], "Timeout", SshAuthorizedKeysErrorCode.ConnectionFailed))
            });

        Assert.Equal(3, results.Count);
        Assert.Single(results[0].MatchedEntries);
        Assert.Equal(2, results[0].TotalKeyCount);
        Assert.Single(results[1].MatchedEntries);
        Assert.False(results[2].Success);

        string csv = SshFleetAuthorizedKeysService.FormatCsvReport(results, keyTarget.Fingerprint);
        Assert.Contains("Prod-A", csv, StringComparison.Ordinal);
        Assert.Contains("'=HYPERLINK", csv, StringComparison.Ordinal);
        Assert.Contains("ERROR", csv, StringComparison.Ordinal);

        string md = SshFleetAuthorizedKeysService.FormatMarkdownReport(results, keyTarget.Fingerprint);
        Assert.Contains("Matches found: 2 across 2 host(s)", md, StringComparison.Ordinal);
        Assert.Contains(keyTarget.Fingerprint, md, StringComparison.Ordinal);

        var revoked = await SshFleetAuthorizedKeysService.RevokeSelectedAsync(
            [
                (targets[0], results[0].MatchedEntries[0]),
                (targets[1], results[1].MatchedEntries[0])
            ],
            allowRemoveLastKey: false,
            store,
            verifier,
            maxConcurrency: 2,
            revokeRunner: (target, entry, allowLast, _) => target.ServerId switch
            {
                "s1" => Task.FromResult(new SshAuthorizedKeysMutationResult(true)),
                _ => Task.FromResult(allowLast
                    ? new SshAuthorizedKeysMutationResult(true)
                    : new SshAuthorizedKeysMutationResult(
                        false,
                        ErrorMessage: "Last key",
                        ErrorCode: SshAuthorizedKeysErrorCode.LastKeyProtected))
            });

        Assert.Equal(2, revoked.Count);
        Assert.True(revoked[0].Mutation.Success);
        Assert.False(revoked[1].Mutation.Success);
        Assert.Equal(SshAuthorizedKeysErrorCode.LastKeyProtected, revoked[1].Mutation.ErrorCode);
    }

    private sealed class StrictVerifier : IHostKeyVerifier
    {
        public Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string presentedFingerprint,
            string? storedFingerprint,
            CancellationToken ct = default)
            => Task.FromResult(HostKeyDecision.Reject);
    }
}
