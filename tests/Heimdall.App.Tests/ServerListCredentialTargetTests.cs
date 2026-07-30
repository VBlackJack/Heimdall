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

using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Covers the protocol -> credential-field mapping used to inject vault credentials
/// into a profile (<see cref="ServerListViewModel.GetCredentialTarget"/>).
/// </summary>
public sealed class ServerListCredentialTargetTests
{
    [Fact]
    public void GetCredentialTarget_CitrixWithoutStoredPassword_ReturnsNull()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "CITRIX",
            RdpUsername = "unused-user",
            RdpPasswordEncrypted = null
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.Null(target);
        Assert.Equal("unused-user", dto.RdpUsername);
        Assert.Null(dto.RdpPasswordEncrypted);
    }

    [Fact]
    public void GetCredentialTarget_RdpWithoutStoredPassword_TargetsRdpFields()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "RDP",
            RemotePort = 3390,
            RdpUsername = "",
            RdpPasswordEncrypted = null
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.NotNull(target);
        Assert.Equal(3390, target!.Value.Port);
        Assert.Equal("", target.Value.Username);

        target.Value.SetPassword("enc-rdp");
        target.Value.SetUsernameIfEmpty("vaultuser");

        Assert.Equal("enc-rdp", dto.RdpPasswordEncrypted);
        Assert.Equal("vaultuser", dto.RdpUsername);
    }

    [Theory]
    [InlineData("SSH", 2222, "SSH")]
    [InlineData("SFTP", 2222, "SSH")]
    [InlineData("WINRM", 5986, "WINRM")]
    [InlineData("FTP", 2121, "FTP")]
    [InlineData("TELNET", 2323, "TELNET")]
    [InlineData("VNC", 5901, "VNC")]
    public void GetCredentialTarget_OtherSupportedProtocols_PreserveTheirFieldMapping(
        string connectionType,
        int expectedPort,
        string expectedFieldOwner)
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = connectionType,
            SshPort = 2222,
            WinRmPort = 5986,
            WinRmIdentityMode = WinRmIdentityMode.Credential,
            FtpPort = 2121,
            TelnetPort = 2323,
            VncPort = 5901
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.NotNull(target);
        Assert.Equal(expectedPort, target!.Value.Port);
        Assert.Null(target.Value.Username);

        target.Value.SetPassword("encrypted");
        target.Value.SetUsernameIfEmpty("vaultuser");

        AssertCredentialFields(dto, expectedFieldOwner);
    }

    // ── Telnet ──────────────────────────────────────────────────────────

    [Fact]
    public void GetCredentialTarget_Telnet_EmptyPassword_TargetsTelnetFields()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "Telnet",
            TelnetPort = 2323,
            TelnetUsername = "",
            TelnetPasswordEncrypted = null
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.NotNull(target);
        Assert.Equal(2323, target!.Value.Port);
        Assert.Equal("", target.Value.Username);

        target.Value.SetPassword("enc-telnet");
        Assert.Equal("enc-telnet", dto.TelnetPasswordEncrypted);

        target.Value.SetUsernameIfEmpty("vaultuser");
        Assert.Equal("vaultuser", dto.TelnetUsername);
    }

    [Fact]
    public void GetCredentialTarget_Telnet_DoesNotOverwriteExistingUsername()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "Telnet",
            TelnetUsername = "stored",
            TelnetPasswordEncrypted = null
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.NotNull(target);
        target!.Value.SetUsernameIfEmpty("vaultuser");
        Assert.Equal("stored", dto.TelnetUsername);
    }

    [Fact]
    public void GetCredentialTarget_Telnet_StoredPassword_ReturnsNull()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "Telnet",
            TelnetPasswordEncrypted = "already-set"
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.Null(target);
    }

    // ── VNC ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetCredentialTarget_Vnc_EmptyPassword_TargetsVncPasswordWithNoUsername()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "VNC",
            VncPort = 5901,
            VncPassword = null
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.NotNull(target);
        Assert.Equal(5901, target!.Value.Port);
        Assert.Null(target.Value.Username);

        target.Value.SetPassword("enc-vnc");
        Assert.Equal("enc-vnc", dto.VncPassword);

        // VNC has no username field: the callback is a no-op and must not throw.
        target.Value.SetUsernameIfEmpty("ignored");
    }

    [Fact]
    public void GetCredentialTarget_Vnc_StoredPassword_ReturnsNull()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "VNC",
            VncPassword = "already-set"
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.Null(target);
    }

    private static void AssertCredentialFields(ServerProfileDto dto, string expectedFieldOwner)
    {
        Assert.Equal(expectedFieldOwner == "SSH" ? "encrypted" : null, dto.SshPasswordEncrypted);
        Assert.Equal(expectedFieldOwner == "SSH" ? "vaultuser" : null, dto.SshUsername);
        Assert.Equal(expectedFieldOwner == "WINRM" ? "encrypted" : null, dto.WinRmPasswordEncrypted);
        Assert.Equal(expectedFieldOwner == "WINRM" ? "vaultuser" : null, dto.WinRmUsername);
        Assert.Equal(expectedFieldOwner == "FTP" ? "encrypted" : null, dto.FtpPasswordEncrypted);
        Assert.Equal(expectedFieldOwner == "FTP" ? "vaultuser" : null, dto.FtpUsername);
        Assert.Equal(expectedFieldOwner == "TELNET" ? "encrypted" : null, dto.TelnetPasswordEncrypted);
        Assert.Equal(expectedFieldOwner == "TELNET" ? "vaultuser" : null, dto.TelnetUsername);
        Assert.Equal(expectedFieldOwner == "VNC" ? "encrypted" : null, dto.VncPassword);
        Assert.Null(dto.RdpPasswordEncrypted);
        Assert.Null(dto.RdpUsername);
    }
}
