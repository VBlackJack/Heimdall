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
using System.Text.Json.Serialization;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

public class ServerProfileDtoTests
{
    private static readonly JsonSerializerOptions CamelCaseOmitNullOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Default values ──────────────────────────────────────────────────

    [Fact]
    public void DefaultValues_ConnectionType_IsRdp()
    {
        var dto = new ServerProfileDto();

        Assert.Equal("RDP", dto.ConnectionType);
    }

    [Fact]
    public void DefaultValues_RemotePort_Is3389()
    {
        var dto = new ServerProfileDto();

        Assert.Equal(3389, dto.RemotePort);
    }

    [Fact]
    public void DefaultValues_LocalPort_Is33890()
    {
        var dto = new ServerProfileDto();

        Assert.Equal(33890, dto.LocalPort);
    }

    [Fact]
    public void DefaultValues_SshPort_Is22()
    {
        var dto = new ServerProfileDto();

        Assert.Equal(22, dto.SshPort);
    }

    [Fact]
    public void DefaultValues_WinRmDefaults_MatchExpected()
    {
        ServerProfileDto dto = new ServerProfileDto();

        Assert.Equal(5985, dto.WinRmPort);
        Assert.False(dto.HasWinRmPortField);
        Assert.False(dto.WinRmUseSsl);
        Assert.False(dto.WinRmSkipCertificateCheck);
        Assert.Equal(WinRmIdentityMode.CurrentUser, dto.WinRmIdentityMode);
        Assert.Null(dto.WinRmUsername);
        Assert.Null(dto.WinRmPasswordEncrypted);
    }

    [Fact]
    public void DefaultValues_LocalShellElevated_IsFalse()
    {
        var dto = new ServerProfileDto();

        Assert.False(dto.LocalShellElevated);
    }

    [Fact]
    public void DefaultValues_RdpMode_IsEmbedded()
    {
        var dto = new ServerProfileDto();

        Assert.Equal("Embedded", dto.RdpMode);
    }

    [Fact]
    public void DefaultValues_RdpDefaults_MatchExpected()
    {
        var dto = new ServerProfileDto();

        Assert.True(dto.RdpUseGlobalDefaults);
        Assert.True(dto.RdpRedirectClipboard);
        Assert.True(dto.RdpDynamicResolution);
        Assert.True(dto.RdpNla);
        Assert.False(dto.RdpStrictServerAuthentication);
        Assert.Equal(32, dto.RdpColorDepth);
        Assert.True(dto.RdpBitmapCaching);
        Assert.True(dto.RdpCompression);
        Assert.True(dto.RdpAutoReconnect);
        Assert.Equal(RdpResolutionMode.FitWindow, dto.RdpResolutionMode);
        Assert.False(dto.HasRdpResolutionModeField);
        Assert.Equal(0, dto.RdpFixedWidth);
        Assert.Equal(0, dto.RdpFixedHeight);
        Assert.True(dto.RdpInitialSmartSizing);
        Assert.Null(dto.RdpResizeEnableDelayMs);
    }

    [Fact]
    public void TunnelsPanelExpanded_DefaultsToNull()
    {
        var dto = new ServerProfileDto();

        Assert.Null(dto.TunnelsPanelExpanded);
    }

    [Fact]
    public void DefaultValues_OptionalFields_AreNull()
    {
        var dto = new ServerProfileDto();

        Assert.Null(dto.Group);
        Assert.Null(dto.SshGatewayId);
        Assert.Null(dto.RdpUsername);
        Assert.Null(dto.RdpPasswordEncrypted);
        Assert.Null(dto.RdpDomain);
        Assert.Null(dto.ProjectId);
        Assert.Null(dto.SshUsername);
        Assert.Null(dto.SshKeyPath);
        Assert.Null(dto.SshPasswordEncrypted);
        Assert.Null(dto.SshKeyPassphraseEncrypted);
        Assert.False(dto.HasSshKeyPassphraseEncryptedField);
        Assert.Null(dto.Tags);
        Assert.Null(dto.Environment);
        Assert.Null(dto.VaultEntryName);
        Assert.Null(dto.LocalShellExecutable);
        Assert.Null(dto.LocalShellArguments);
        Assert.Null(dto.LocalShellWorkingDirectory);
        Assert.Null(dto.RdpGateway);
    }

    // ── JSON round-trip ─────────────────────────────────────────────────

    [Fact]
    public void JsonRoundTrip_PreservesAllFields()
    {
        var original = new ServerProfileDto
        {
            Id = "srv-001",
            DisplayName = "Production DB",
            RemoteServer = "db.example.com",
            RemotePort = 3389,
            LocalPort = 13389,
            Group = "Databases",
            SshGatewayId = "gw-01",
            RdpUsername = @"CORP\admin",
            RdpDomain = "CORP",
            ConnectionType = "RDP",
            WinRmPort = 5986,
            WinRmUsername = @"CORP\operator",
            WinRmPasswordEncrypted = "encrypted-winrm-password",
            WinRmUseSsl = true,
            WinRmSkipCertificateCheck = true,
            WinRmIdentityMode = WinRmIdentityMode.Credential,
            SshUsername = "deploy",
            SshPort = 2222,
            SshMode = "Embedded",
            SshAgentForwarding = true,
            SshKeyPath = @"C:\keys\id_rsa",
            SshPasswordEncrypted = "encrypted-password",
            SshKeyPassphraseEncrypted = "encrypted-key-passphrase",
            IsFavorite = true,
            SortOrder = 5,
            Tags = "production,critical",
            RdpMode = "Embedded",
            RdpMultiMonitor = true,
            RdpResolutionMode = RdpResolutionMode.Fixed,
            RdpFixedWidth = 2560,
            RdpFixedHeight = 1440,
            RdpInitialSmartSizing = false,
            RdpResizeEnableDelayMs = 3000,
            RdpStrictServerAuthentication = true,
            Environment = "Production",
            VaultEntryName = "prod-db/rdp",
            LocalShellExecutable = "pwsh.exe",
            LocalShellElevated = true,
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ServerProfileDto>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.DisplayName, deserialized.DisplayName);
        Assert.Equal(original.RemoteServer, deserialized.RemoteServer);
        Assert.Equal(original.RemotePort, deserialized.RemotePort);
        Assert.Equal(original.LocalPort, deserialized.LocalPort);
        Assert.Equal(original.Group, deserialized.Group);
        Assert.Equal(original.SshGatewayId, deserialized.SshGatewayId);
        Assert.Equal(original.RdpUsername, deserialized.RdpUsername);
        Assert.Equal(original.RdpDomain, deserialized.RdpDomain);
        Assert.Equal(original.ConnectionType, deserialized.ConnectionType);
        Assert.Equal(original.WinRmPort, deserialized.WinRmPort);
        Assert.True(deserialized.HasWinRmPortField);
        Assert.Equal(original.WinRmUsername, deserialized.WinRmUsername);
        Assert.Equal(original.WinRmPasswordEncrypted, deserialized.WinRmPasswordEncrypted);
        Assert.Equal(original.WinRmUseSsl, deserialized.WinRmUseSsl);
        Assert.Equal(original.WinRmSkipCertificateCheck, deserialized.WinRmSkipCertificateCheck);
        Assert.Equal(original.WinRmIdentityMode, deserialized.WinRmIdentityMode);
        Assert.Equal(original.SshUsername, deserialized.SshUsername);
        Assert.Equal(original.SshPort, deserialized.SshPort);
        Assert.Equal(original.SshMode, deserialized.SshMode);
        Assert.Equal(original.SshAgentForwarding, deserialized.SshAgentForwarding);
        Assert.Equal(original.SshKeyPath, deserialized.SshKeyPath);
        Assert.Equal(original.SshPasswordEncrypted, deserialized.SshPasswordEncrypted);
        Assert.Equal(original.SshKeyPassphraseEncrypted, deserialized.SshKeyPassphraseEncrypted);
        Assert.True(deserialized.HasSshKeyPassphraseEncryptedField);
        Assert.Equal(original.IsFavorite, deserialized.IsFavorite);
        Assert.Equal(original.SortOrder, deserialized.SortOrder);
        Assert.Equal(original.Tags, deserialized.Tags);
        Assert.Equal(original.RdpMode, deserialized.RdpMode);
        Assert.Equal(original.RdpMultiMonitor, deserialized.RdpMultiMonitor);
        Assert.Equal(original.RdpResolutionMode, deserialized.RdpResolutionMode);
        Assert.True(deserialized.HasRdpResolutionModeField);
        Assert.Equal(original.RdpFixedWidth, deserialized.RdpFixedWidth);
        Assert.Equal(original.RdpFixedHeight, deserialized.RdpFixedHeight);
        Assert.Equal(original.RdpInitialSmartSizing, deserialized.RdpInitialSmartSizing);
        Assert.Equal(original.RdpResizeEnableDelayMs, deserialized.RdpResizeEnableDelayMs);
        Assert.Equal(original.RdpStrictServerAuthentication, deserialized.RdpStrictServerAuthentication);
        Assert.Equal(original.Environment, deserialized.Environment);
        Assert.Equal(original.VaultEntryName, deserialized.VaultEntryName);
        Assert.Equal(original.LocalShellExecutable, deserialized.LocalShellExecutable);
        Assert.Equal(original.LocalShellElevated, deserialized.LocalShellElevated);
    }

    [Fact]
    public void JsonDeserialization_LegacyCredentialFields_RemainsImportable()
    {
        const string Json = """
        {
            "id": "legacy-with-credentials",
            "displayName": "Legacy With Credentials",
            "remoteServer": "10.0.0.1",
            "rdpPasswordEncrypted": "rdp-secret",
            "sshPasswordEncrypted": "ssh-secret",
            "winRmPasswordEncrypted": "winrm-secret",
            "ftpPasswordEncrypted": "ftp-secret",
            "telnetPasswordEncrypted": "telnet-secret",
            "sshKeyPassphraseEncrypted": "key-secret",
            "vncPassword": "vnc-secret"
        }
        """;
        JsonSerializerOptions importOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        ServerProfileDto? dto = JsonSerializer.Deserialize<ServerProfileDto>(Json, importOptions);

        Assert.NotNull(dto);
        Assert.Equal("rdp-secret", dto!.RdpPasswordEncrypted);
        Assert.Equal("ssh-secret", dto.SshPasswordEncrypted);
        Assert.Equal("winrm-secret", dto.WinRmPasswordEncrypted);
        Assert.Equal("ftp-secret", dto.FtpPasswordEncrypted);
        Assert.Equal("telnet-secret", dto.TelnetPasswordEncrypted);
        Assert.Equal("key-secret", dto.SshKeyPassphraseEncrypted);
        Assert.True(dto.HasSshKeyPassphraseEncryptedField);
        Assert.Equal("vnc-secret", dto.VncPassword);
    }

    [Fact]
    public void TunnelsPanelExpanded_RoundTrip_NullOmitsKey()
    {
        var original = new ServerProfileDto
        {
            Id = "srv-null",
            DisplayName = "Null State",
            RemoteServer = "10.0.0.1",
            TunnelsPanelExpanded = null
        };

        var json = JsonSerializer.Serialize(original, CamelCaseOmitNullOptions);
        var deserialized = JsonSerializer.Deserialize<ServerProfileDto>(json, CamelCaseOmitNullOptions);

        Assert.DoesNotContain("tunnelsPanelExpanded", json);
        Assert.NotNull(deserialized);
        Assert.Null(deserialized.TunnelsPanelExpanded);
    }

    [Fact]
    public void TunnelsPanelExpanded_RoundTrip_True()
    {
        var original = new ServerProfileDto
        {
            Id = "srv-true",
            DisplayName = "True State",
            RemoteServer = "10.0.0.1",
            TunnelsPanelExpanded = true
        };

        var json = JsonSerializer.Serialize(original, CamelCaseOmitNullOptions);
        var deserialized = JsonSerializer.Deserialize<ServerProfileDto>(json, CamelCaseOmitNullOptions);

        Assert.Contains("\"tunnelsPanelExpanded\": true", json);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.TunnelsPanelExpanded);
    }

    [Fact]
    public void TunnelsPanelExpanded_RoundTrip_False()
    {
        var original = new ServerProfileDto
        {
            Id = "srv-false",
            DisplayName = "False State",
            RemoteServer = "10.0.0.1",
            TunnelsPanelExpanded = false
        };

        var json = JsonSerializer.Serialize(original, CamelCaseOmitNullOptions);
        var deserialized = JsonSerializer.Deserialize<ServerProfileDto>(json, CamelCaseOmitNullOptions);

        Assert.Contains("\"tunnelsPanelExpanded\": false", json);
        Assert.NotNull(deserialized);
        Assert.False(deserialized.TunnelsPanelExpanded);
    }

    [Fact]
    public void TunnelsPanelExpanded_LegacyJson_DeserialisesToNull()
    {
        var json = """
        {
            "id": "srv-legacy",
            "displayName": "Legacy",
            "remoteServer": "10.0.0.1"
        }
        """;

        var dto = JsonSerializer.Deserialize<ServerProfileDto>(json, CamelCaseOmitNullOptions);

        Assert.NotNull(dto);
        Assert.Null(dto.TunnelsPanelExpanded);
    }

    [Fact]
    public void VaultEntryName_DefaultsToNull()
    {
        var dto = new ServerProfileDto();

        Assert.Null(dto.VaultEntryName);
    }

    [Fact]
    public void VaultEntryName_RoundTrip_PreservesValue()
    {
        var original = new ServerProfileDto
        {
            Id = "srv-vault",
            DisplayName = "Display Only",
            RemoteServer = "10.0.0.1",
            VaultEntryName = "team-vault/prod-db"
        };

        var json = JsonSerializer.Serialize(original, CamelCaseOmitNullOptions);
        var deserialized = JsonSerializer.Deserialize<ServerProfileDto>(json, CamelCaseOmitNullOptions);

        Assert.Contains("\"vaultEntryName\": \"team-vault/prod-db\"", json);
        Assert.NotNull(deserialized);
        Assert.Equal("team-vault/prod-db", deserialized.VaultEntryName);
    }

    [Fact]
    public void VaultEntryName_RoundTrip_NullOmitsKey()
    {
        var original = new ServerProfileDto
        {
            Id = "srv-vault-null",
            DisplayName = "Display Only",
            RemoteServer = "10.0.0.1",
            VaultEntryName = null
        };

        var json = JsonSerializer.Serialize(original, CamelCaseOmitNullOptions);
        var deserialized = JsonSerializer.Deserialize<ServerProfileDto>(json, CamelCaseOmitNullOptions);

        Assert.DoesNotContain("vaultEntryName", json);
        Assert.NotNull(deserialized);
        Assert.Null(deserialized.VaultEntryName);
    }

    [Fact]
    public void VaultEntryName_LegacyJson_DeserialisesToNull()
    {
        var json = """
        {
            "id": "srv-legacy",
            "displayName": "Legacy",
            "remoteServer": "10.0.0.1"
        }
        """;

        var dto = JsonSerializer.Deserialize<ServerProfileDto>(json, CamelCaseOmitNullOptions);

        Assert.NotNull(dto);
        Assert.Null(dto.VaultEntryName);
    }

    [Fact]
    public void JsonDeserialization_MissingFields_UsesDefaults()
    {
        var json = """{"Id":"minimal","DisplayName":"Minimal Server","RemoteServer":"10.0.0.1"}""";

        var dto = JsonSerializer.Deserialize<ServerProfileDto>(json);

        Assert.NotNull(dto);
        Assert.Equal("minimal", dto.Id);
        Assert.Equal("RDP", dto.ConnectionType);
        Assert.Equal(3389, dto.RemotePort);
        Assert.Equal(33890, dto.LocalPort);
        Assert.Equal(22, dto.SshPort);
        Assert.Equal("Embedded", dto.RdpMode);
        Assert.Equal("Embedded", dto.SshMode);
        Assert.False(dto.LocalShellElevated);
        Assert.False(dto.WinRmSkipCertificateCheck);
        Assert.Equal(RdpResolutionMode.FitWindow, dto.RdpResolutionMode);
        Assert.False(dto.HasRdpResolutionModeField);
        Assert.True(dto.RdpInitialSmartSizing);
        Assert.Null(dto.RdpResizeEnableDelayMs);
    }

    [Fact]
    public void JsonDeserialization_WinRmProfileWithoutSkipCertificateCheck_DefaultsFalse()
    {
        var json = """
        {
            "ConnectionType": "WINRM",
            "DisplayName": "WinRM Server",
            "RemoteServer": "server01.contoso.local",
            "WinRmUseSsl": true
        }
        """;

        var dto = JsonSerializer.Deserialize<ServerProfileDto>(json);

        Assert.NotNull(dto);
        Assert.Equal("WINRM", dto.ConnectionType);
        Assert.True(dto.WinRmUseSsl);
        Assert.Equal(DefaultPorts.WinRmHttps, dto.WinRmPort);
        Assert.False(dto.HasWinRmPortField);
        Assert.False(dto.WinRmSkipCertificateCheck);
    }

    [Fact]
    public void RdpResizeEnableDelayMs_RoundTrip_ZeroPreservesValue()
    {
        var original = new ServerProfileDto
        {
            RdpResizeEnableDelayMs = 0
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ServerProfileDto>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(0, deserialized.RdpResizeEnableDelayMs);
    }

    [Fact]
    public void JsonSerialization_WinRmIdentityMode_UsesStringEnum()
    {
        ServerProfileDto dto = new ServerProfileDto
        {
            ConnectionType = "WINRM",
            WinRmIdentityMode = WinRmIdentityMode.Credential
        };

        string json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"WinRmIdentityMode\":\"Credential\"", json);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void JsonDeserialization_WinRmIdentityMode_RejectsNumericValues(int numericValue)
    {
        string json = $$"""
        {
            "ConnectionType": "WINRM",
            "WinRmIdentityMode": {{numericValue}}
        }
        """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ServerProfileDto>(json));
    }

    [Fact]
    public void JsonSerialization_UsesBackCompatibleFixedResolutionNames()
    {
        var dto = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Fixed,
            RdpFixedWidth = 1920,
            RdpFixedHeight = 1080
        };

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"rdpFixedResolutionWidth\":1920", json);
        Assert.Contains("\"rdpFixedResolutionHeight\":1080", json);
        Assert.DoesNotContain("rdpFixedWidth", json);
        Assert.DoesNotContain("rdpFixedHeight", json);
    }

    [Fact]
    public void JsonDeserialization_ReadsBackCompatibleFixedResolutionNames()
    {
        var json = """
        {
            "rdpResolutionMode": "Fixed",
            "rdpFixedResolutionWidth": 1920,
            "rdpFixedResolutionHeight": 1080
        }
        """;

        var dto = JsonSerializer.Deserialize<ServerProfileDto>(json);

        Assert.NotNull(dto);
        Assert.Equal(RdpResolutionMode.Fixed, dto.RdpResolutionMode);
        Assert.True(dto.HasRdpResolutionModeField);
        Assert.Equal(1920, dto.RdpFixedWidth);
        Assert.Equal(1080, dto.RdpFixedHeight);
    }

    [Fact]
    public void JsonDeserialization_UnknownPhase2Fields_AreIgnoredByOlderReaders()
    {
        var json = """
        {
            "id": "srv-legacy",
            "displayName": "Legacy Reader",
            "remoteServer": "10.0.0.1",
            "rdpResolutionMode": "Fixed",
            "rdpInitialSmartSizing": false,
            "rdpResizeEnableDelayMs": 3000
        }
        """;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var dto = JsonSerializer.Deserialize<Phase1ServerProfileDto>(json, options);

        Assert.NotNull(dto);
        Assert.Equal("srv-legacy", dto.Id);
        Assert.Equal("Legacy Reader", dto.DisplayName);
        Assert.Equal("10.0.0.1", dto.RemoteServer);
    }

    private sealed class Phase1ServerProfileDto
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string RemoteServer { get; set; } = string.Empty;
        public int RdpFixedResolutionWidth { get; set; }
        public int RdpFixedResolutionHeight { get; set; }
    }
}
