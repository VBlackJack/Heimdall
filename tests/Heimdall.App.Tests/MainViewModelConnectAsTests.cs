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
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Pure-helper tests for the "Connect as..." transient profile builder. No connection.
/// </summary>
public class MainViewModelConnectAsTests
{
    private static ServerItemViewModel Server(string host, string username, string connectionType) =>
        new ServerItemViewModel
        {
            RemoteServer = host,
            Username = username,
            ConnectionType = connectionType
        };

    [Fact]
    public void BuildTransientProfile_Ssh_CarriesHostUsernameAndDefaultPort_NoPassword()
    {
        var dto = MainViewModel.BuildTransientProfile(Server("host.example.com", "alice", "RDP"), "SSH");

        Assert.StartsWith("adhoc-", dto.Id);
        Assert.Equal("host.example.com", dto.RemoteServer);
        Assert.Equal("SSH", dto.ConnectionType);
        Assert.Equal(DefaultPorts.Ssh, dto.SshPort);
        Assert.Equal("alice", dto.SshUsername);
        Assert.Null(dto.SshPasswordEncrypted);
        Assert.Null(dto.RdpPasswordEncrypted);
    }

    [Fact]
    public void BuildTransientProfile_Sftp_UsesSshFields()
    {
        var dto = MainViewModel.BuildTransientProfile(Server("host", "bob", "SSH"), "SFTP");

        Assert.Equal("SFTP", dto.ConnectionType);
        Assert.Equal(DefaultPorts.Sftp, dto.SshPort);
        Assert.Equal("bob", dto.SshUsername);
        Assert.Null(dto.SshPasswordEncrypted);
    }

    [Fact]
    public void BuildTransientProfile_Rdp_CarriesRemotePortAndRdpUsername_NoPassword()
    {
        var dto = MainViewModel.BuildTransientProfile(Server("host", "carol", "SSH"), "RDP");

        Assert.Equal("RDP", dto.ConnectionType);
        Assert.Equal(DefaultPorts.Rdp, dto.RemotePort);
        Assert.Equal("carol", dto.RdpUsername);
        Assert.Null(dto.RdpPasswordEncrypted);
    }

    [Fact]
    public void BuildTransientProfile_Vnc_SetsPortButNoUsername_NoPassword()
    {
        var dto = MainViewModel.BuildTransientProfile(Server("host", "dave", "SSH"), "VNC");

        Assert.Equal("VNC", dto.ConnectionType);
        Assert.Equal(DefaultPorts.Vnc, dto.VncPort);
        // VNC has no username field; nothing is carried into the SSH username either.
        Assert.Null(dto.SshUsername);
        Assert.Null(dto.VncPassword);
    }

    [Fact]
    public void BuildTransientProfile_Telnet_CarriesTelnetFields_NoPassword()
    {
        var dto = MainViewModel.BuildTransientProfile(Server("host", "erin", "SSH"), "TELNET");

        Assert.Equal("TELNET", dto.ConnectionType);
        Assert.Equal(DefaultPorts.Telnet, dto.TelnetPort);
        Assert.Equal("erin", dto.TelnetUsername);
        Assert.Null(dto.TelnetPasswordEncrypted);
    }

    [Fact]
    public void BuildTransientProfile_LowercaseProtocol_NormalizesToUpper()
    {
        var dto = MainViewModel.BuildTransientProfile(Server("host", "frank", "RDP"), "ssh");

        Assert.Equal("SSH", dto.ConnectionType);
        Assert.Equal("frank", dto.SshUsername);
    }

    [Fact]
    public void BuildTransientProfile_AllProtocols_CarryNoPassword()
    {
        foreach (var protocol in new[] { "SSH", "SFTP", "RDP", "VNC", "TELNET" })
        {
            var dto = MainViewModel.BuildTransientProfile(Server("host", "user", "SSH"), protocol);

            Assert.StartsWith("adhoc-", dto.Id);
            Assert.Null(dto.SshPasswordEncrypted);
            Assert.Null(dto.RdpPasswordEncrypted);
            Assert.Null(dto.VncPassword);
            Assert.Null(dto.TelnetPasswordEncrypted);
            Assert.Null(dto.FtpPasswordEncrypted);
        }
    }
}
