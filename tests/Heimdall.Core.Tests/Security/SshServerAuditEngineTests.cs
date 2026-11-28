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

using Heimdall.Core.Security;

namespace Heimdall.Core.Tests.Security;

public sealed class SshServerAuditEngineTests
{
    [Fact]
    public void Evaluate_ModernPqStrictKexServer_GradesAPlusAndAnssiCompliant()
    {
        var snap = new SshKexInitSnapshot(
            "srv.corp.local",
            22,
            "SSH-2.0-OpenSSH_9.9",
            ["mlkem768x25519-sha256", "curve25519-sha256", "kex-strict-s-v00@openssh.com"],
            ["ssh-ed25519", "rsa-sha2-512"],
            ["chacha20-poly1305@openssh.com", "aes256-gcm@openssh.com"],
            ["chacha20-poly1305@openssh.com", "aes256-gcm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com", "hmac-sha2-256-etm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com", "hmac-sha2-256-etm@openssh.com"],
            ["none", "zlib@openssh.com"],
            ["none", "zlib@openssh.com"]);

        var report = SshServerAuditEngine.Evaluate(snap);

        Assert.Equal(SshAuditGrade.APlus, report.Grade);
        Assert.True(report.HasPostQuantumKex);
        Assert.True(report.HasStrictKex);
        Assert.True(report.IsAnssiCompliant);
    }

    [Fact]
    public void Evaluate_LegacyServerWithSha1AndDss_GradesFAndFlagsAnssiFail()
    {
        var snap = new SshKexInitSnapshot(
            "legacy.corp",
            22,
            "SSH-2.0-OpenSSH_5.3",
            ["diffie-hellman-group1-sha1", "diffie-hellman-group14-sha1"],
            ["ssh-dss", "ssh-rsa"],
            ["3des-cbc", "aes128-cbc"],
            ["3des-cbc", "aes128-cbc"],
            ["hmac-sha1", "hmac-md5"],
            ["hmac-sha1", "hmac-md5"],
            ["none", "zlib"],
            ["none", "zlib"]);

        var report = SshServerAuditEngine.Evaluate(snap);

        Assert.Equal(SshAuditGrade.F, report.Grade);
        Assert.False(report.IsAnssiCompliant);
        Assert.False(report.HasPostQuantumKex);
        Assert.Contains(report.Findings, f => f.Code == "INSECURE_ALGORITHMS");
    }

    [Fact]
    public void FormatExports_HtmlEncodesAndNeutralizesCsvFormulas()
    {
        var snap = new SshKexInitSnapshot(
            "=evil.host",
            22,
            "SSH-2.0-<script>alert(1)</script>",
            ["curve25519-sha256", "kex-strict-s-v00@openssh.com"],
            ["ssh-ed25519"],
            ["aes256-gcm@openssh.com"],
            ["aes256-gcm@openssh.com"],
            ["hmac-sha2-256-etm@openssh.com"],
            ["hmac-sha2-256-etm@openssh.com"],
            ["none"],
            ["none"]);

        var report = SshServerAuditEngine.Evaluate(snap);
        string html = SshServerAuditEngine.FormatHtmlReport(report);
        string csv = SshServerAuditEngine.FormatCsvReport(report);

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("\"'=evil.host\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ServerWithNonEtmMacOrDhGroup14_IsNotAnssiCompliantAndCannotEarnAPlus()
    {
        var snap = new SshKexInitSnapshot(
            "mixed.corp.local",
            22,
            "SSH-2.0-OpenSSH_9.9",
            ["mlkem768x25519-sha256", "diffie-hellman-group14-sha256", "kex-strict-s-v00@openssh.com"],
            ["ssh-ed25519"],
            ["aes256-gcm@openssh.com"],
            ["aes256-gcm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com", "hmac-sha2-256"],
            ["hmac-sha2-512-etm@openssh.com", "hmac-sha2-256"],
            ["none"],
            ["none"]);

        var report = SshServerAuditEngine.Evaluate(snap);

        Assert.False(report.IsAnssiCompliant);
        Assert.NotEqual(SshAuditGrade.APlus, report.Grade);
        Assert.Equal(SshAuditGrade.A, report.Grade);
    }

    [Fact]
    public void FormatBatchExports_HtmlEncodesAndNeutralizesCsvFormulasForFleet()
    {
        var snap = new SshKexInitSnapshot(
            "srv1.corp",
            22,
            "SSH-2.0-OpenSSH_9.9",
            ["mlkem768x25519-sha256", "kex-strict-s-v00@openssh.com"],
            ["ssh-ed25519"],
            ["aes256-gcm@openssh.com"],
            ["aes256-gcm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com"],
            ["none"],
            ["none"]);
        var report = SshServerAuditEngine.Evaluate(snap);

        SshFleetServerAuditEntry[] entries =
        [
            new("=Prod1", "srv1.corp", 22, "Bastion", report),
            new("Down<script>", "srv2.corp", 2222, null, null, "Connection refused")
        ];

        string csv = SshServerAuditEngine.FormatBatchCsvReport(entries);
        string html = SshServerAuditEngine.FormatBatchHtmlReport(entries);

        Assert.Contains("\"'=Prod1\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"ERROR\"", csv, StringComparison.Ordinal);
        Assert.Contains("Connection refused", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Down&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("ANSSI PASS: 1", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateHardeningDropIn_IncludesValidationHeaderAndSanitizesBanner()
    {
        var snap = new SshKexInitSnapshot(
            "srv.corp\r\nPermitRootLogin yes",
            22,
            "SSH-2.0-OpenSSH_9.9\nInjected",
            ["curve25519-sha256"],
            ["ssh-ed25519"],
            ["aes256-gcm@openssh.com"],
            ["aes256-gcm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com"],
            ["none"],
            ["none"]);
        var report = SshServerAuditEngine.Evaluate(snap);

        string dropIn = SshServerAuditEngine.GenerateHardeningDropIn(report);

        Assert.Contains("sudo sshd -t -f /etc/ssh/sshd_config", dropIn, StringComparison.Ordinal);
        Assert.Contains("KexAlgorithms mlkem768x25519-sha256", dropIn, StringComparison.Ordinal);
        Assert.DoesNotContain("\nPermitRootLogin yes", dropIn, StringComparison.Ordinal);
        Assert.DoesNotContain("\nInjected", dropIn, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareAgainstBaseline_ClassifiesImprovedRegressedUnchangedAndNew()
    {
        var strongSnap = new SshKexInitSnapshot(
            "host-a",
            22,
            "SSH-2.0-OpenSSH_9.9",
            ["mlkem768x25519-sha256", "kex-strict-s-v00@openssh.com"],
            ["ssh-ed25519"],
            ["aes256-gcm@openssh.com"],
            ["aes256-gcm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com"],
            ["none"],
            ["none"]);
        var weakSnap = new SshKexInitSnapshot(
            "host-b",
            22,
            "SSH-2.0-OpenSSH_7.4",
            ["diffie-hellman-group1-sha1"],
            ["ssh-dss"],
            ["3des-cbc"],
            ["3des-cbc"],
            ["hmac-sha1"],
            ["hmac-sha1"],
            ["none"],
            ["none"]);

        var strongReport = SshServerAuditEngine.Evaluate(strongSnap);
        var weakReport = SshServerAuditEngine.Evaluate(weakSnap);

        var previous = SshServerAuditEngine.DeserializeBaselineJson(
            SshServerAuditEngine.SerializeBaselineJson(
            [
                new SshFleetAuditBaselineEntry("host-a:22", "B", false, false, false, DateTimeOffset.UtcNow),
                new SshFleetAuditBaselineEntry("host-b:22", "A", true, false, true, DateTimeOffset.UtcNow),
                new SshFleetAuditBaselineEntry("host-c:22", "A+", true, true, true, DateTimeOffset.UtcNow)
            ]));

        SshFleetServerAuditEntry[] current =
        [
            new("A", "host-a", 22, null, strongReport),
            new("B", "host-b", 22, null, weakReport),
            new("C", "host-c", 22, null, strongReport),
            new("D", "host-d", 22, null, strongReport),
            new("E", "host-e", 22, null, null, "Timeout")
        ];

        var drift = SshServerAuditEngine.CompareAgainstBaseline(current, previous);

        Assert.Equal(1, drift.Improved);
        Assert.Equal(1, drift.Regressed);
        Assert.Equal(1, drift.Unchanged);
        Assert.Equal(1, drift.NewHosts);
        Assert.Equal(SshFleetAuditDriftStatus.Improved, drift.ByEndpoint["host-a:22"]);
        Assert.Equal(SshFleetAuditDriftStatus.Regressed, drift.ByEndpoint["host-b:22"]);
        Assert.Equal(SshFleetAuditDriftStatus.Unchanged, drift.ByEndpoint["host-c:22"]);
        Assert.Equal(SshFleetAuditDriftStatus.New, drift.ByEndpoint["host-d:22"]);
        Assert.Equal(SshFleetAuditDriftStatus.Error, drift.ByEndpoint["host-e:22"]);
    }

    [Fact]
    public void CompareAgainstBaseline_DeduplicatesSharedEndpointsAndPrefersSucceededOverError()
    {
        var strongSnap = new SshKexInitSnapshot(
            "host-dup",
            22,
            "SSH-2.0-OpenSSH_9.9",
            ["mlkem768x25519-sha256", "kex-strict-s-v00@openssh.com"],
            ["ssh-ed25519"],
            ["aes256-gcm@openssh.com"],
            ["aes256-gcm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com"],
            ["hmac-sha2-512-etm@openssh.com"],
            ["none"],
            ["none"]);
        var strongReport = SshServerAuditEngine.Evaluate(strongSnap);
        SshFleetAuditBaselineEntry[] previous =
        [
            new("host-dup:22", "B", false, false, false, DateTimeOffset.UtcNow)
        ];
        SshFleetServerAuditEntry[] current =
        [
            new("Profile1", "host-dup", 22, null, null, "Timeout"),
            new("Profile2", "host-dup", 22, null, strongReport),
            new("Profile3", "host-dup", 22, null, strongReport)
        ];

        var drift = SshServerAuditEngine.CompareAgainstBaseline(current, previous);

        Assert.Equal(1, drift.Improved);
        Assert.Equal(0, drift.Regressed);
        Assert.Equal(0, drift.Unchanged);
        Assert.Equal(0, drift.NewHosts);
        Assert.Equal(SshFleetAuditDriftStatus.Improved, drift.ByEndpoint["host-dup:22"]);
    }
}
