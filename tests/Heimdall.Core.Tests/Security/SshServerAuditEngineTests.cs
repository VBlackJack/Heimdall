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
}
