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
using System.IO;
using System.Text;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class SshStep2HelpersTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "heimdall-ssh-step2-" + Guid.NewGuid().ToString("N"));

    public SshStep2HelpersTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SshStartupTrustGate_SetAndTake_ConsumesSingleDecisionPerProfileInstance()
    {
        var profileA = new ServerProfileDto { Id = "a", DisplayName = "A", RemoteServer = "a.example" };
        var profileB = new ServerProfileDto { Id = "b", DisplayName = "B", RemoteServer = "b.example" };

        Assert.Null(SshStartupTrustGate.Take(profileA));

        SshStartupTrustGate.Set(profileA, approved: true);
        SshStartupTrustGate.Set(profileB, approved: false);

        var decisionA = SshStartupTrustGate.Take(profileA);
        Assert.NotNull(decisionA);
        Assert.True(decisionA.Approved);
        Assert.Null(SshStartupTrustGate.Take(profileA));

        var decisionB = SshStartupTrustGate.Take(profileB);
        Assert.NotNull(decisionB);
        Assert.False(decisionB.Approved);
    }

    [Fact]
    public void SshCertificateWarningHelper_EvaluateServerAndGateways_CombinesTargetAndGatewayWarnings()
    {
        string targetCert = Path.Combine(_tempDir, "target-cert.pub");
        string gatewayCert = Path.Combine(_tempDir, "gw-cert.pub");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        File.WriteAllText(targetCert, BuildTestUserCertLine(
            now.AddDays(-10),
            now.AddDays(-1)));
        File.WriteAllText(gatewayCert, BuildTestUserCertLine(
            now.AddDays(5),
            now.AddDays(30)));

        var settings = new AppSettings
        {
            SshGateways =
            [
                new SshGatewayDto
                {
                    Id = "gw-1",
                    Name = "Bastion",
                    Host = "bastion.example.com",
                    Port = 22,
                    User = "ops",
                    SshCertificatePath = gatewayCert
                }
            ]
        };

        var server = new ServerProfileDto
        {
            Id = "srv-1",
            DisplayName = "App",
            RemoteServer = "app.internal",
            SshCertificatePath = targetCert,
            SshGatewayId = "gw-1"
        };

        var summary = SshCertificateWarningHelper.EvaluateServerAndGateways(
            server,
            settings,
            new LocalizationManager());

        Assert.True(summary.HasInvalidValidityWindow);
        Assert.NotNull(summary.WarningMessage);
        Assert.Contains("Bastion:", summary.WarningMessage, StringComparison.Ordinal);

        string? hinted = SshCertificateWarningHelper.AppendInvalidWindowHint("Auth failed.", summary);
        Assert.NotNull(hinted);
        Assert.StartsWith("Auth failed. ", hinted, StringComparison.Ordinal);
        Assert.Contains("Bastion:", hinted, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionHelpers_CreateGatewayConnectionParams_DelegatesFaithfullyToGatewayChainResolver()
    {
        var gateway = new SshGatewayDto
        {
            Id = "gw-1",
            Name = "Jump1",
            Host = "jump.example.com",
            Port = 2222,
            User = "ops",
            KeyPath = @"C:\keys\id_ed25519",
            SshCertificatePath = @"C:\keys\id_ed25519-cert.pub",
            SshProxyType = SshProxyType.Socks5,
            SshProxyHost = "127.0.0.1",
            SshProxyPort = 9050
        };

        SshConnectionParams viaHelper = ConnectionHelpers.CreateGatewayConnectionParams(
            gateway,
            SshAgentPreference.PageantOnly,
            SshCryptoPolicy.StrictModern);
        SshConnectionParams direct = GatewayChainResolver.ToConnectionParams(
            [gateway],
            static p => p,
            SshAgentPreference.PageantOnly,
            SshCryptoPolicy.StrictModern)[0];

        Assert.Equal(direct.Host, viaHelper.Host);
        Assert.Equal(direct.Port, viaHelper.Port);
        Assert.Equal(direct.Username, viaHelper.Username);
        Assert.Equal(direct.KeyPath, viaHelper.KeyPath);
        Assert.Equal(direct.CertificatePath, viaHelper.CertificatePath);
        Assert.Equal(direct.ProxyType, viaHelper.ProxyType);
        Assert.Equal(direct.ProxyHost, viaHelper.ProxyHost);
        Assert.Equal(direct.ProxyPort, viaHelper.ProxyPort);
        Assert.Equal(direct.SshAgentPreference, viaHelper.SshAgentPreference);
        Assert.Equal(direct.CryptoPolicy, viaHelper.CryptoPolicy);
    }

    private static string BuildTestUserCertLine(DateTimeOffset validAfter, DateTimeOffset validBefore)
    {
        using var ms = new MemoryStream();
        WriteString(ms, Encoding.ASCII.GetBytes(OpenSshCertificateParser.CertEd25519));
        WriteString(ms, new byte[32]);
        WriteString(ms, new byte[32]);
        WriteUInt64(ms, 42);
        WriteUInt32(ms, (uint)OpenSshCertificateType.User);
        WriteString(ms, Encoding.UTF8.GetBytes("test-user"));
        WriteString(ms, []);
        WriteUInt64(ms, (ulong)validAfter.ToUnixTimeSeconds());
        WriteUInt64(ms, (ulong)validBefore.ToUnixTimeSeconds());
        WriteString(ms, []);
        WriteString(ms, []);
        WriteString(ms, []);
        WriteString(ms, new byte[32]);
        WriteString(ms, new byte[64]);
        return $"{OpenSshCertificateParser.CertEd25519} {Convert.ToBase64String(ms.ToArray())} test@heimdall";
    }

    private static void WriteString(Stream s, byte[] data)
    {
        WriteUInt32(s, (uint)data.Length);
        s.Write(data, 0, data.Length);
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
}
