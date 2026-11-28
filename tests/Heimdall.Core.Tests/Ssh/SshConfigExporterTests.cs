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
using Heimdall.Core.Ssh;

namespace Heimdall.Core.Tests.Ssh;

public sealed class SshConfigExporterTests
{
    [Fact]
    public void ExportSingleHost_OmitsDefaultsAndIncludesConfiguredOptions()
    {
        var block = SshConfigExporter.ExportSingleHost(new SshConfigHostEntry(
            Alias: "prod web",
            HostName: "10.0.0.10",
            User: "deploy",
            Port: 2222,
            IdentityFile: @"C:\Users\alice\.ssh\id_ed25519",
            ProxyJump: "bastion",
            ForwardAgent: true,
            Compression: true,
            ForwardX11: true,
            DynamicForwardPort: 1080,
            RemoteBindPort: 8080,
            RemoteLocalPort: 3000,
            ServerAliveInterval: 60));

        Assert.Contains("Host prod-web", block, StringComparison.Ordinal);
        Assert.Contains("    HostName 10.0.0.10", block, StringComparison.Ordinal);
        Assert.Contains("    User deploy", block, StringComparison.Ordinal);
        Assert.Contains("    Port 2222", block, StringComparison.Ordinal);
        Assert.Contains(@"    IdentityFile C:\Users\alice\.ssh\id_ed25519", block, StringComparison.Ordinal);
        Assert.Contains("    ProxyJump bastion", block, StringComparison.Ordinal);
        Assert.Contains("    ForwardAgent yes", block, StringComparison.Ordinal);
        Assert.Contains("    Compression yes", block, StringComparison.Ordinal);
        Assert.Contains("    ForwardX11 yes", block, StringComparison.Ordinal);
        Assert.Contains("    DynamicForward 127.0.0.1:1080", block, StringComparison.Ordinal);
        Assert.Contains("    RemoteForward 8080 127.0.0.1:3000", block, StringComparison.Ordinal);
        Assert.Contains("    ServerAliveInterval 60", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportSingleHost_NeutralizesNewlineInjectionInFields()
    {
        var block = SshConfigExporter.ExportSingleHost(new SshConfigHostEntry(
            Alias: "evil\nProxyCommand calc.exe",
            HostName: "app.example.com\r\nPermitLocalCommand yes",
            User: "alice\nForwardAgent yes"));

        Assert.DoesNotContain("PermitLocalCommand", block, StringComparison.Ordinal);
        Assert.DoesNotContain("ProxyCommand", block, StringComparison.Ordinal);
        Assert.DoesNotContain("ForwardAgent", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportInventory_ResolvesGatewayChainsAndNeverExportsSecrets()
    {
        var rootGw = new SshGatewayDto
        {
            Id = "gw-root",
            Name = "Bastion Root",
            Host = "root.example.com",
            Port = 22,
            User = "ops",
            SshPasswordEncrypted = "ENCRYPTED_SECRET_GW"
        };
        var childGw = new SshGatewayDto
        {
            Id = "gw-child",
            Name = "Bastion DMZ",
            Host = "dmz.example.com",
            Port = 2200,
            User = "ops-dmz",
            ParentGatewayId = "gw-root"
        };
        var sshServer = new ServerProfileDto
        {
            Id = "srv-1",
            DisplayName = "App Server",
            ConnectionType = "SSH",
            RemoteServer = "10.20.0.5",
            SshPort = 22,
            SshUsername = "ubuntu",
            SshGatewayId = "gw-child",
            SshPasswordEncrypted = "ENCRYPTED_SECRET_SRV",
            SshKeyPassphraseEncrypted = "ENCRYPTED_PASSPHRASE",
            SshCompression = true
        };
        var rdpServer = new ServerProfileDto
        {
            Id = "srv-rdp",
            DisplayName = "Windows Box",
            ConnectionType = "RDP",
            RemoteServer = "10.20.0.99"
        };

        var exported = SshConfigExporter.ExportInventory([sshServer, rdpServer], [rootGw, childGw], 45);

        Assert.Contains("Host Bastion-Root", exported, StringComparison.Ordinal);
        Assert.Contains("Host Bastion-DMZ", exported, StringComparison.Ordinal);
        Assert.Contains("    ProxyJump Bastion-Root", exported, StringComparison.Ordinal);
        Assert.Contains("Host App-Server", exported, StringComparison.Ordinal);
        Assert.Contains("    ProxyJump Bastion-DMZ", exported, StringComparison.Ordinal);
        Assert.Contains("    Compression yes", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows-Box", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("ENCRYPTED_SECRET", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("ENCRYPTED_PASSPHRASE", exported, StringComparison.Ordinal);

        // Round-trip through OpenSshConfigParser to guarantee valid OpenSSH syntax.
        var reparsed = OpenSshConfigParser.Parse(exported);
        Assert.Equal(3, reparsed.Candidates.Count);
        var appCandidate = Assert.Single(reparsed.Candidates, c => c.Alias == "App-Server");
        Assert.Equal("10.20.0.5", appCandidate.HostName);
        Assert.True(appCandidate.Compression);
        var hop = Assert.Single(appCandidate.ProxyJumpChain);
        Assert.Equal("dmz.example.com", hop.HostName);
    }

    [Fact]
    public void ExportSingleHost_SkipsSetEnvValuesContainingDoubleQuotes()
    {
        var exported = SshConfigExporter.ExportSingleHost(new SshConfigHostEntry(
            Alias: "ops",
            HostName: "ops.example.com",
            User: "admin",
            EnvironmentVariables: ["SAFE_ENV=prod", "UNSAFE_ENV=a\"b"],
            StartupCommand: "tmux new -As ops"));

        Assert.Contains("    SetEnv SAFE_ENV=prod", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("UNSAFE_ENV", exported, StringComparison.Ordinal);
        Assert.Contains("    RemoteCommand tmux new -As ops", exported, StringComparison.Ordinal);
    }
}
