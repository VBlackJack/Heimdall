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

namespace Heimdall.Ssh.Tests;

public class GatewayChainResolverTests
{
    private static readonly Func<string, string?> NoOpDecrypt = _ => null;
    private static readonly Func<string, string?> PassthroughDecrypt = s => $"decrypted:{s}";

    // ── Single gateway ──────────────────────────────────────────────────

    [Fact]
    public void ResolveChain_SingleGateway_ReturnsSingleEntry()
    {
        var gateways = new List<SshGatewayDto>
        {
            new() { Id = "gw1", Host = "bastion.example.com", Port = 22, User = "admin" }
        };

        var chain = GatewayChainResolver.ResolveChain("gw1", gateways, NoOpDecrypt);

        Assert.Single(chain);
        Assert.Equal("bastion.example.com", chain[0].Host);
        Assert.Equal(22, chain[0].Port);
        Assert.Equal("admin", chain[0].Username);
    }

    // ── Multi-hop chain ─────────────────────────────────────────────────

    [Fact]
    public void ResolveChain_TwoHops_ReturnsRootFirst()
    {
        var gateways = new List<SshGatewayDto>
        {
            new() { Id = "root", Host = "bastion1.com", Port = 22, User = "user1" },
            new() { Id = "hop2", Host = "bastion2.com", Port = 2222, User = "user2", ParentGatewayId = "root" }
        };

        var chain = GatewayChainResolver.ResolveChain("hop2", gateways, NoOpDecrypt);

        Assert.Equal(2, chain.Count);
        Assert.Equal("bastion1.com", chain[0].Host);
        Assert.Equal("bastion2.com", chain[1].Host);
    }

    [Fact]
    public void ResolveChain_ThreeHops_ReturnsCorrectOrder()
    {
        var gateways = new List<SshGatewayDto>
        {
            new() { Id = "gw1", Host = "hop1.com", Port = 22, User = "u1" },
            new() { Id = "gw2", Host = "hop2.com", Port = 22, User = "u2", ParentGatewayId = "gw1" },
            new() { Id = "gw3", Host = "hop3.com", Port = 22, User = "u3", ParentGatewayId = "gw2" }
        };

        var chain = GatewayChainResolver.ResolveChain("gw3", gateways, NoOpDecrypt);

        Assert.Equal(3, chain.Count);
        Assert.Equal("hop1.com", chain[0].Host);
        Assert.Equal("hop2.com", chain[1].Host);
        Assert.Equal("hop3.com", chain[2].Host);
    }

    // ── Password decryption ─────────────────────────────────────────────

    [Fact]
    public void ResolveChain_DecryptsPasswordViaCallback()
    {
        var gateways = new List<SshGatewayDto>
        {
            new()
            {
                Id = "gw1", Host = "gw.com", Port = 22, User = "user",
                SshPasswordEncrypted = "encrypted_blob"
            }
        };

        var chain = GatewayChainResolver.ResolveChain("gw1", gateways, PassthroughDecrypt);

        Assert.Equal("decrypted:encrypted_blob", chain[0].Password);
    }

    [Fact]
    public void ResolveChain_DecryptsKeyPassphraseViaCallback()
    {
        var gateways = new List<SshGatewayDto>
        {
            new()
            {
                Id = "gw1", Host = "gw.com", Port = 22, User = "user",
                KeyPath = @"C:\keys\id_rsa",
                SshKeyPassphraseEncrypted = "encrypted_key_passphrase"
            }
        };

        var chain = GatewayChainResolver.ResolveChain("gw1", gateways, PassthroughDecrypt);

        Assert.Equal("decrypted:encrypted_key_passphrase", chain[0].KeyPassphrase);
        Assert.False(chain[0].UseLegacyPasswordAsKeyPassphrase);
    }

    [Fact]
    public void ResolveChain_LegacyKeyPassword_SetsLegacyMappingFlag()
    {
        var gateways = new List<SshGatewayDto>
        {
            new()
            {
                Id = "gw1", Name = "legacy-gw", Host = "gw.com", Port = 22, User = "user",
                KeyPath = @"C:\keys\id_rsa",
                SshPasswordEncrypted = "encrypted_password"
            }
        };

        var chain = GatewayChainResolver.ResolveChain("gw1", gateways, PassthroughDecrypt);

        Assert.Equal("decrypted:encrypted_password", chain[0].Password);
        Assert.True(chain[0].UseLegacyPasswordAsKeyPassphrase);
        Assert.Equal("legacy-gw", chain[0].LegacyCredentialName);
    }

    [Fact]
    public void ResolveChain_NoPassword_LeavesPasswordNull()
    {
        var gateways = new List<SshGatewayDto>
        {
            new() { Id = "gw1", Host = "gw.com", Port = 22, User = "user" }
        };

        var chain = GatewayChainResolver.ResolveChain("gw1", gateways, PassthroughDecrypt);

        Assert.Null(chain[0].Password);
    }

    [Fact]
    public void ResolveChain_WithKeyPath_SetsKeyPath()
    {
        var gateways = new List<SshGatewayDto>
        {
            new()
            {
                Id = "gw1", Host = "gw.com", Port = 22, User = "user",
                KeyPath = @"C:\keys\id_rsa"
            }
        };

        var chain = GatewayChainResolver.ResolveChain("gw1", gateways, NoOpDecrypt);

        Assert.Equal(@"C:\keys\id_rsa", chain[0].KeyPath);
    }

    // ── Circular dependency detection ───────────────────────────────────

    [Fact]
    public void ResolveChain_CircularDependency_ThrowsInvalidOperation()
    {
        var gateways = new List<SshGatewayDto>
        {
            new() { Id = "gw1", Host = "a.com", Port = 22, User = "u", ParentGatewayId = "gw2" },
            new() { Id = "gw2", Host = "b.com", Port = 22, User = "u", ParentGatewayId = "gw1" }
        };

        GatewayChainException ex = Assert.Throws<GatewayChainException>(
            () => GatewayChainResolver.ResolveChain("gw1", gateways, NoOpDecrypt));
        Assert.Contains("Circular dependency", ex.Message);
        Assert.Equal(SshFailureCode.CircularChainDependency, ex.Code);
    }

    [Fact]
    public void ResolveChain_SelfReference_ThrowsInvalidOperation()
    {
        var gateways = new List<SshGatewayDto>
        {
            new() { Id = "gw1", Host = "a.com", Port = 22, User = "u", ParentGatewayId = "gw1" }
        };

        GatewayChainException ex = Assert.Throws<GatewayChainException>(
            () => GatewayChainResolver.ResolveChain("gw1", gateways, NoOpDecrypt));
        Assert.Contains("Circular dependency", ex.Message);
        Assert.Equal(SshFailureCode.CircularChainDependency, ex.Code);
    }

    // ── Depth limit ─────────────────────────────────────────────────────

    [Fact]
    public void ResolveChain_ExceedsMaxDepth_ThrowsInvalidOperation()
    {
        var gateways = new List<SshGatewayDto>();
        for (int i = 0; i < 10; i++)
        {
            gateways.Add(new SshGatewayDto
            {
                Id = $"gw{i}",
                Host = $"hop{i}.com",
                Port = 22,
                User = "user",
                ParentGatewayId = i > 0 ? $"gw{i - 1}" : null
            });
        }

        GatewayChainException ex = Assert.Throws<GatewayChainException>(
            () => GatewayChainResolver.ResolveChain("gw9", gateways, NoOpDecrypt, maxDepth: 3));
        Assert.Contains("depth exceeds maximum", ex.Message);
        Assert.Equal(SshFailureCode.ChainDepthExceeded, ex.Code);
    }

    // ── Missing gateway ─────────────────────────────────────────────────

    [Fact]
    public void ResolveChain_TargetNotFound_ThrowsArgumentException()
    {
        var gateways = new List<SshGatewayDto>
        {
            new() { Id = "gw1", Host = "a.com", Port = 22, User = "u" }
        };

        var ex = Assert.Throws<ArgumentException>(
            () => GatewayChainResolver.ResolveChain("nonexistent", gateways, NoOpDecrypt));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void ResolveChain_ParentNotFound_ThrowsArgumentException()
    {
        var gateways = new List<SshGatewayDto>
        {
            new() { Id = "gw1", Host = "a.com", Port = 22, User = "u", ParentGatewayId = "missing" }
        };

        var ex = Assert.Throws<ArgumentException>(
            () => GatewayChainResolver.ResolveChain("gw1", gateways, NoOpDecrypt));
        Assert.Contains("not found", ex.Message);
    }

    // ── Null argument validation ────────────────────────────────────────

    [Fact]
    public void ResolveChain_NullGatewayId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => GatewayChainResolver.ResolveChain(null!, [], NoOpDecrypt));
    }

    [Fact]
    public void ResolveChain_EmptyGatewayId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => GatewayChainResolver.ResolveChain("  ", [], NoOpDecrypt));
    }

    [Fact]
    public void ResolveChain_NullGateways_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => GatewayChainResolver.ResolveChain("gw1", null!, NoOpDecrypt));
    }

    [Fact]
    public void ResolveChain_NullDecryptCallback_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => GatewayChainResolver.ResolveChain("gw1", [], null!));
    }

    // ── Case-insensitive ID matching ────────────────────────────────────

    [Fact]
    public void ResolveChain_CaseInsensitiveGatewayIds()
    {
        var gateways = new List<SshGatewayDto>
        {
            new() { Id = "GW-Root", Host = "root.com", Port = 22, User = "u" },
            new() { Id = "gw-child", Host = "child.com", Port = 22, User = "u", ParentGatewayId = "gw-root" }
        };

        var chain = GatewayChainResolver.ResolveChain("GW-CHILD", gateways, NoOpDecrypt);

        Assert.Equal(2, chain.Count);
        Assert.Equal("root.com", chain[0].Host);
        Assert.Equal("child.com", chain[1].Host);
    }
}
