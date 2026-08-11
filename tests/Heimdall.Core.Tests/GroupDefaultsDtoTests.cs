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

using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

public class GroupDefaultsDtoTests
{
    // ── Resolve: exact match ────────────────────────────────────────────

    [Fact]
    public void Resolve_ExactMatch_ReturnsGroupDefaults()
    {
        var defaults = new Dictionary<string, GroupDefaultsDto>
        {
            ["PROD"] = new()
            {
                SshUsername = "deploy",
                SshGatewayId = "gw-prod",
                SshPort = 2222
            }
        };

        var result = GroupDefaultsDto.Resolve("PROD", defaults);

        Assert.Equal("deploy", result.SshUsername);
        Assert.Equal("gw-prod", result.SshGatewayId);
        Assert.Equal(2222, result.SshPort);
    }

    // ── Resolve: hierarchical fallback ──────────────────────────────────

    [Fact]
    public void Resolve_ChildGroup_InheritsFromParent()
    {
        var defaults = new Dictionary<string, GroupDefaultsDto>
        {
            ["PROD"] = new()
            {
                SshUsername = "deploy",
                SshGatewayId = "gw-prod"
            }
        };

        var result = GroupDefaultsDto.Resolve("PROD/Linux", defaults);

        Assert.Equal("deploy", result.SshUsername);
        Assert.Equal("gw-prod", result.SshGatewayId);
    }

    [Fact]
    public void Resolve_LeafValueOverridesRoot_WhenBothProvided()
    {
        // Most-specific group wins: the leaf "PROD/Linux" overrides the root "PROD"
        // for any field both define, while leaf-absent fields still inherit from root.
        var defaults = new Dictionary<string, GroupDefaultsDto>
        {
            ["PROD"] = new()
            {
                SshUsername = "deploy",
                SshGatewayId = "gw-prod"
            },
            ["PROD/Linux"] = new()
            {
                SshUsername = "linux-admin"
            }
        };

        var result = GroupDefaultsDto.Resolve("PROD/Linux", defaults);

        // Leaf "PROD/Linux" wins on username; gateway falls back to root "PROD"
        Assert.Equal("linux-admin", result.SshUsername);
        Assert.Equal("gw-prod", result.SshGatewayId);
    }

    [Fact]
    public void Resolve_LeafOnlyFields_InheritedWhenRootLacksValue()
    {
        var defaults = new Dictionary<string, GroupDefaultsDto>
        {
            ["PROD"] = new()
            {
                SshUsername = "deploy"
            },
            ["PROD/Linux"] = new()
            {
                SshKeyPath = "/keys/linux.pem"
            }
        };

        var result = GroupDefaultsDto.Resolve("PROD/Linux", defaults);

        // Root provides username, leaf provides key path (no conflict)
        Assert.Equal("deploy", result.SshUsername);
        Assert.Equal("/keys/linux.pem", result.SshKeyPath);
    }

    [Fact]
    public void Resolve_ThreeLevelHierarchy_DeepestFieldsOverrideAncestors()
    {
        var defaults = new Dictionary<string, GroupDefaultsDto>
        {
            ["PROD"] = new()
            {
                SshUsername = "root",
                SshPort = 22,
                Environment = "Production"
            },
            ["PROD/Linux"] = new()
            {
                SshKeyPath = "/keys/linux.pem"
            },
            ["PROD/Linux/Web"] = new()
            {
                SshPort = 2222
            }
        };

        var result = GroupDefaultsDto.Resolve("PROD/Linux/Web", defaults);

        // The deepest group that sets a field wins: "PROD/Linux/Web" overrides the
        // SshPort from "PROD"; fields only the ancestors set still inherit downward.
        Assert.Equal("root", result.SshUsername);
        Assert.Equal(2222, result.SshPort);
        Assert.Equal("/keys/linux.pem", result.SshKeyPath);
        Assert.Equal("Production", result.Environment);
    }

    // ── Resolve: no match ───────────────────────────────────────────────

    [Fact]
    public void Resolve_NoMatch_ReturnsEmptyDefaults()
    {
        var defaults = new Dictionary<string, GroupDefaultsDto>
        {
            ["PROD"] = new() { SshUsername = "deploy" }
        };

        var result = GroupDefaultsDto.Resolve("DEV", defaults);

        Assert.Null(result.SshUsername);
        Assert.Null(result.SshGatewayId);
        Assert.Null(result.SshKeyPath);
        Assert.Null(result.SshPort);
    }

    [Fact]
    public void Resolve_NullGroupName_ReturnsEmptyDefaults()
    {
        var defaults = new Dictionary<string, GroupDefaultsDto>
        {
            ["PROD"] = new() { SshUsername = "deploy" }
        };

        var result = GroupDefaultsDto.Resolve(null, defaults);

        Assert.Null(result.SshUsername);
    }

    [Fact]
    public void Resolve_EmptyGroupName_ReturnsEmptyDefaults()
    {
        var defaults = new Dictionary<string, GroupDefaultsDto>
        {
            ["PROD"] = new() { SshUsername = "deploy" }
        };

        var result = GroupDefaultsDto.Resolve("", defaults);

        Assert.Null(result.SshUsername);
    }

    [Fact]
    public void Resolve_EmptyDictionary_ReturnsEmptyDefaults()
    {
        var result = GroupDefaultsDto.Resolve("PROD", new Dictionary<string, GroupDefaultsDto>());

        Assert.Null(result.SshUsername);
    }

    // ── ApplyTo: fills empty server fields ──────────────────────────────

    [Fact]
    public void ApplyTo_SetsGatewayWhenServerFieldEmpty()
    {
        var groupDefaults = new GroupDefaultsDto { SshGatewayId = "gw-default" };
        var server = new ServerProfileDto();

        groupDefaults.ApplyTo(server);

        Assert.Equal("gw-default", server.SshGatewayId);
    }

    [Fact]
    public void ApplyTo_SetsSshUsernameWhenServerFieldEmpty()
    {
        var groupDefaults = new GroupDefaultsDto { SshUsername = "deploy" };
        var server = new ServerProfileDto();

        groupDefaults.ApplyTo(server);

        Assert.Equal("deploy", server.SshUsername);
    }

    [Fact]
    public void ApplyTo_SetsSshKeyPathWhenServerFieldEmpty()
    {
        var groupDefaults = new GroupDefaultsDto { SshKeyPath = "/keys/id_rsa" };
        var server = new ServerProfileDto();

        groupDefaults.ApplyTo(server);

        Assert.Equal("/keys/id_rsa", server.SshKeyPath);
    }

    [Fact]
    public void ApplyTo_PreservesExplicitSshPort22()
    {
        GroupDefaultsDto groupDefaults = new() { SshPort = 2222 };
        ServerProfileDto server = new() { SshPort = 22 };

        groupDefaults.ApplyTo(server);

        Assert.True(server.HasSshPortField);
        Assert.Equal(22, server.SshPort);
    }

    [Fact]
    public void ApplyTo_InheritsSshPortWhenJsonFieldIsAbsent()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        ServerProfileDto? server = JsonSerializer.Deserialize<ServerProfileDto>(
            """{"displayName":"Legacy","connectionType":"SSH"}""",
            options);
        Assert.NotNull(server);
        Assert.False(server.HasSshPortField);

        GroupDefaultsDto groupDefaults = new() { SshPort = 2222 };
        groupDefaults.ApplyTo(server);

        Assert.Equal(2222, server.SshPort);
    }

    [Fact]
    public void ApplyTo_PreservesExplicitSshPort22FromJson()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        ServerProfileDto? server = JsonSerializer.Deserialize<ServerProfileDto>(
            """{"displayName":"Explicit","connectionType":"SSH","sshPort":22}""",
            options);
        Assert.NotNull(server);
        Assert.True(server.HasSshPortField);

        GroupDefaultsDto groupDefaults = new() { SshPort = 2222 };
        groupDefaults.ApplyTo(server);

        Assert.Equal(22, server.SshPort);
    }

    [Fact]
    public void JsonRoundTrip_PreservesAbsentSshPortField()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        ServerProfileDto? server = JsonSerializer.Deserialize<ServerProfileDto>(
            """{"displayName":"Legacy","connectionType":"SSH"}""",
            options);
        Assert.NotNull(server);
        Assert.False(server.HasSshPortField);

        string json = JsonSerializer.Serialize(server, options);
        ServerProfileDto? roundTripped = JsonSerializer.Deserialize<ServerProfileDto>(json, options);

        Assert.NotNull(roundTripped);
        Assert.False(roundTripped.HasSshPortField);
        Assert.Equal(22, roundTripped.SshPort);
    }

    [Fact]
    public async Task ConfigManagerRoundTrip_PreservesAbsentSshPortField()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            "Heimdall.SshPortPresence." + Guid.NewGuid().ToString("N"));
        string configPath = Path.Combine(tempPath, "config");
        Directory.CreateDirectory(configPath);

        try
        {
            string serversPath = Path.Combine(configPath, "servers.json");
            await File.WriteAllTextAsync(
                serversPath,
                """[{"id":"legacy","displayName":"Legacy","connectionType":"SSH"}]""");
            ConfigManager manager = new(tempPath);

            List<ServerProfileDto> loaded = await manager.LoadServersAsync();
            Assert.False(Assert.Single(loaded).HasSshPortField);

            await manager.SaveServersAsync(loaded);

            string persistedJson = await File.ReadAllTextAsync(serversPath);
            Assert.DoesNotContain("\"sshPort\"", persistedJson, StringComparison.OrdinalIgnoreCase);
            ServerProfileDto persisted = Assert.Single(await manager.LoadServersAsync());
            Assert.False(persisted.HasSshPortField);
            Assert.Equal(22, persisted.SshPort);
        }
        finally
        {
            Directory.Delete(tempPath, recursive: true);
        }
    }

    // ── ApplyTo: does NOT override existing values ──────────────────────

    [Fact]
    public void ApplyTo_DoesNotOverrideServerGateway()
    {
        var groupDefaults = new GroupDefaultsDto { SshGatewayId = "gw-default" };
        var server = new ServerProfileDto { SshGatewayId = "gw-custom" };

        groupDefaults.ApplyTo(server);

        Assert.Equal("gw-custom", server.SshGatewayId);
    }

    [Fact]
    public void ApplyTo_DoesNotOverrideServerSshUsername()
    {
        var groupDefaults = new GroupDefaultsDto { SshUsername = "deploy" };
        var server = new ServerProfileDto { SshUsername = "custom-user" };

        groupDefaults.ApplyTo(server);

        Assert.Equal("custom-user", server.SshUsername);
    }

    [Fact]
    public void ApplyTo_DoesNotOverrideServerSshKeyPath()
    {
        var groupDefaults = new GroupDefaultsDto { SshKeyPath = "/keys/default.pem" };
        var server = new ServerProfileDto { SshKeyPath = "/keys/custom.pem" };

        groupDefaults.ApplyTo(server);

        Assert.Equal("/keys/custom.pem", server.SshKeyPath);
    }

    [Fact]
    public void ApplyTo_DoesNotOverrideSshPortWhenNotDefault()
    {
        var groupDefaults = new GroupDefaultsDto { SshPort = 2222 };
        var server = new ServerProfileDto { SshPort = 9999 };

        groupDefaults.ApplyTo(server);

        Assert.Equal(9999, server.SshPort);
    }

    // -- ApplyTo: ConnectionType / Environment inheritance ---------------
    // Producer: GroupDefaultsDto.ApplyTo
    // (src/Heimdall.Core/Configuration/GroupDefaultsDto.cs) now writes the resolved
    // ConnectionType and Environment onto the profile using the same "apply only when
    // the server's own value is unset" guard as the other inherited fields. Before
    // this fix Resolve merged both fields but ApplyTo silently dropped them.

    [Fact]
    public void ApplyTo_SetsConnectionTypeWhenServerFieldEmpty()
    {
        // ServerProfileDto.ConnectionType (ServerProfileDto.cs) is a non-nullable
        // string defaulting to "RDP"; an explicitly empty value is the unset sentinel.
        var groupDefaults = new GroupDefaultsDto { ConnectionType = "SSH" };
        var server = new ServerProfileDto { ConnectionType = "" };

        groupDefaults.ApplyTo(server);

        Assert.Equal("SSH", server.ConnectionType);
    }

    [Fact]
    public void ApplyTo_SetsEnvironmentWhenServerFieldEmpty()
    {
        var groupDefaults = new GroupDefaultsDto { Environment = "Production" };
        var server = new ServerProfileDto();

        groupDefaults.ApplyTo(server);

        Assert.Equal("Production", server.Environment);
    }

    [Fact]
    public void ApplyTo_DoesNotOverrideServerConnectionType()
    {
        var groupDefaults = new GroupDefaultsDto { ConnectionType = "SSH" };
        var server = new ServerProfileDto { ConnectionType = "RDP" };

        groupDefaults.ApplyTo(server);

        Assert.Equal("RDP", server.ConnectionType);
    }

    [Fact]
    public void ApplyTo_DoesNotOverrideServerEnvironment()
    {
        var groupDefaults = new GroupDefaultsDto { Environment = "Production" };
        var server = new ServerProfileDto { Environment = "Lab" };

        groupDefaults.ApplyTo(server);

        Assert.Equal("Lab", server.Environment);
    }

    [Fact]
    public void ApplyTo_EmptyGroupConnectionType_DoesNotBlankServerProtocol()
    {
        // A group default without a ConnectionType must never overwrite the server's
        // own (non-nullable) protocol with null/empty.
        var groupDefaults = new GroupDefaultsDto { ConnectionType = null };
        var server = new ServerProfileDto { ConnectionType = "" };

        groupDefaults.ApplyTo(server);

        Assert.Equal("", server.ConnectionType);
    }

    // -- Resolve + ApplyTo: nested-group precedence ----------------------

    [Fact]
    public void ResolveAndApplyTo_NestedGroups_AppliesLeafOnlyConnectionTypeAndEnvironment()
    {
        // When only the most-specific group declares the value (no conflict at higher
        // levels), Resolve carries it down and ApplyTo writes it onto the profile.
        var defaults = new Dictionary<string, GroupDefaultsDto>
        {
            ["PROD"] = new() { SshUsername = "deploy" },
            ["PROD/Linux/Web"] = new() { ConnectionType = "SSH", Environment = "Production" }
        };

        var resolved = GroupDefaultsDto.Resolve("PROD/Linux/Web", defaults);
        var server = new ServerProfileDto { ConnectionType = "" };
        resolved.ApplyTo(server);

        Assert.Equal("SSH", server.ConnectionType);
        Assert.Equal("Production", server.Environment);
    }

    [Fact]
    public void ResolveAndApplyTo_NestedGroups_MostSpecificValueWinsForBothFields()
    {
        // Producer: GroupDefaultsDto.Resolve leaf-first ??= pass
        // (GroupDefaultsDto.cs) keeps the deepest group's value, so the
        // most-specific group wins over its ancestors. ApplyTo then writes the
        // resolved ConnectionType/Environment onto the profile.
        var defaults = new Dictionary<string, GroupDefaultsDto>
        {
            ["PROD"] = new() { ConnectionType = "SSH", Environment = "Production" },
            ["PROD/Linux/Web"] = new() { ConnectionType = "SFTP", Environment = "Staging" }
        };

        var resolved = GroupDefaultsDto.Resolve("PROD/Linux/Web", defaults);
        var server = new ServerProfileDto { ConnectionType = "" };
        resolved.ApplyTo(server);

        Assert.Equal("SFTP", server.ConnectionType);
        Assert.Equal("Staging", server.Environment);
    }
}
