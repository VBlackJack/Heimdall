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

using Heimdall.Core.Configuration;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;

namespace Heimdall.Core.Tests.Security;

public sealed class SshFleetComplianceAnalyzerTests
{
    [Fact]
    public void Analyze_FlagsLegacyPolicyPasswordOnlyRevokedAndWeakHostKey()
    {
        var profiles = new List<ServerProfileDto>
        {
            new()
            {
                Id = "1",
                DisplayName = "legacy-switch",
                ConnectionType = "SSH",
                RemoteServer = "10.0.0.1",
                SshCryptoPolicy = SshCryptoPolicy.LegacyCompatible,
                SshPasswordEncrypted = "enc"
            },
            new()
            {
                Id = "2",
                DisplayName = "modern-srv",
                ConnectionType = "SSH",
                RemoteServer = "10.0.0.2",
                SshAgentKeyFingerprint = "SHA256:abc"
            }
        };

        var hostKeys = new List<SshFleetHostKeySnapshot>
        {
            new("10.0.0.1", 22, "ssh-rsa", "SHA256:weak", IsRevoked: false),
            new("10.0.0.3", 22, "ssh-ed25519", "SHA256:revoked", IsRevoked: true)
        };

        var report = SshFleetComplianceAnalyzer.Analyze(
            profiles,
            [],
            hostKeys,
            SshCryptoPolicy.Standard,
            fileTextReader: _ => null);

        Assert.Equal(2, report.ProfilesAudited);
        Assert.Equal(2, report.HostKeysAudited);
        Assert.Contains(report.Findings, f => f.Code == "LEGACY_CRYPTO_POLICY");
        Assert.Contains(report.Findings, f => f.Code == "PASSWORD_ONLY_AUTH");
        Assert.Contains(report.Findings, f => f.Code == "HOSTKEY_WEAK_ALGO");
        Assert.Contains(report.Findings, f => f.Code == "HOSTKEY_REVOKED" && f.Severity == SshFleetFindingSeverity.Critical);
        Assert.True(report.ScorePercent < 100);
    }

    [Fact]
    public void FormatCsvReport_NeutralizesFormulaInjection()
    {
        var report = new SshFleetComplianceReport(
            1, 0, 0, 80,
            [new SshFleetFinding(SshFleetFindingSeverity.Warning, "=cmd|' /C calc'!A0", "Profile", "LEGACY_CRYPTO_POLICY", "Legacy")]);

        string csv = SshFleetComplianceAnalyzer.FormatCsvReport(report);

        Assert.Contains("\"'=cmd|' /C calc'!A0\"", csv, StringComparison.Ordinal);
    }
}
