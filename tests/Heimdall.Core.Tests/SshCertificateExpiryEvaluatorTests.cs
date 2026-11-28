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

namespace Heimdall.Core.Tests;

public sealed class SshCertificateExpiryEvaluatorTests : IDisposable
{
    private readonly string _tempDir;

    public SshCertificateExpiryEvaluatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"heimdall-cert-exp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EvaluateContent_DetectsExpiredExpiringSoonAndValidUserCertificates()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        string expiredLine = BuildTestUserCertLine(
            OpenSshCertificateType.User,
            validAfter: (ulong)now.AddDays(-60).ToUnixTimeSeconds(),
            validBefore: (ulong)now.AddDays(-2).ToUnixTimeSeconds());
        var expired = SshCertificateExpiryEvaluator.EvaluateContent(expiredLine, nowUtc: now);
        Assert.Equal(SshCertificateExpiryStatus.Expired, expired.Status);
        Assert.Equal(0, expired.DaysRemaining);

        string soonLine = BuildTestUserCertLine(
            OpenSshCertificateType.User,
            validAfter: (ulong)now.AddDays(-10).ToUnixTimeSeconds(),
            validBefore: (ulong)now.AddDays(5).ToUnixTimeSeconds());
        var soon = SshCertificateExpiryEvaluator.EvaluateContent(soonLine, nowUtc: now);
        Assert.Equal(SshCertificateExpiryStatus.ExpiringSoon, soon.Status);
        Assert.Equal(5, soon.DaysRemaining);

        string validLine = BuildTestUserCertLine(
            OpenSshCertificateType.User,
            validAfter: (ulong)now.AddDays(-1).ToUnixTimeSeconds(),
            validBefore: (ulong)now.AddDays(90).ToUnixTimeSeconds());
        var valid = SshCertificateExpiryEvaluator.EvaluateContent(validLine, nowUtc: now);
        Assert.Equal(SshCertificateExpiryStatus.Valid, valid.Status);
    }

    [Fact]
    public void EvaluateContent_IgnoresHostCertificates()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        string hostCertLine = BuildTestUserCertLine(
            OpenSshCertificateType.Host,
            validAfter: (ulong)now.AddDays(-10).ToUnixTimeSeconds(),
            validBefore: (ulong)now.AddDays(-1).ToUnixTimeSeconds());

        var result = SshCertificateExpiryEvaluator.EvaluateContent(hostCertLine, nowUtc: now);
        Assert.Equal(SshCertificateExpiryStatus.None, result.Status);
    }

    [Fact]
    public void Evaluate_AutoDiscoversSiblingCertificate_AndIgnoresUncPaths()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        string keyPath = Path.Combine(_tempDir, "id_ed25519");
        string certPath = keyPath + "-cert.pub";
        File.WriteAllText(certPath, BuildTestUserCertLine(
            OpenSshCertificateType.User,
            validAfter: (ulong)now.AddDays(-1).ToUnixTimeSeconds(),
            validBefore: (ulong)now.AddDays(1).ToUnixTimeSeconds()));

        var siblingResult = SshCertificateExpiryEvaluator.Evaluate(keyPath, explicitCertPath: null, nowUtc: now);
        Assert.Equal(SshCertificateExpiryStatus.ExpiringSoon, siblingResult.Status);
        Assert.Equal(1, siblingResult.DaysRemaining);
        Assert.Equal(certPath, siblingResult.ResolvedPath);

        var uncResult = SshCertificateExpiryEvaluator.Evaluate(
            keyPath: null,
            explicitCertPath: @"\\attacker.example\share\id_ed25519-cert.pub",
            nowUtc: now);
        Assert.Equal(SshCertificateExpiryStatus.None, uncResult.Status);
    }

    private static string BuildTestUserCertLine(
        OpenSshCertificateType certType,
        ulong validAfter,
        ulong validBefore)
    {
        using var ms = new MemoryStream();
        WriteString(ms, Encoding.ASCII.GetBytes(OpenSshCertificateParser.CertEd25519));
        WriteString(ms, new byte[32]); // nonce
        WriteString(ms, new byte[32]); // ed25519 pk
        WriteUInt64(ms, 42); // serial
        WriteUInt32(ms, (uint)certType);
        WriteString(ms, Encoding.UTF8.GetBytes("test-user-cert"));

        using var principalsMs = new MemoryStream();
        WriteString(principalsMs, Encoding.UTF8.GetBytes("ops"));
        WriteString(ms, principalsMs.ToArray());

        WriteUInt64(ms, validAfter);
        WriteUInt64(ms, validBefore);
        WriteString(ms, []); // critical options
        WriteString(ms, []); // extensions
        WriteString(ms, []); // reserved

        using var caMs = new MemoryStream();
        WriteString(caMs, Encoding.ASCII.GetBytes("ssh-ed25519"));
        WriteString(caMs, new byte[32]);
        WriteString(ms, caMs.ToArray()); // signature key

        WriteString(ms, new byte[64]); // signature
        return $"{OpenSshCertificateParser.CertEd25519} {Convert.ToBase64String(ms.ToArray())} test@heimdall";
    }

    private static void WriteUInt32(Stream s, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteUInt64(Stream s, ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteString(Stream s, byte[] data)
    {
        WriteUInt32(s, (uint)data.Length);
        s.Write(data);
    }
}
