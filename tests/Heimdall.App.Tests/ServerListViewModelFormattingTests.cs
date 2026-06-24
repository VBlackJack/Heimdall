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

namespace Heimdall.App.Tests;

public class ServerListViewModelFormattingTests
{
    private static ServerItemViewModel Server(string host, int port, string username = "")
    {
        return new ServerItemViewModel
        {
            RemoteServer = host,
            RemotePort = port,
            Username = username
        };
    }

    [Fact]
    public void BuildAddress_NoPort_ReturnsHostOnly()
    {
        var result = ServerListViewModel.BuildAddress(Server("host.example.com", 0));
        Assert.Equal("host.example.com", result);
    }

    [Fact]
    public void BuildAddress_WithPort_ReturnsHostColonPort()
    {
        var result = ServerListViewModel.BuildAddress(Server("host.example.com", 2222));
        Assert.Equal("host.example.com:2222", result);
    }

    [Fact]
    public void BuildSshCommand_WithUsername_PrefixesUser()
    {
        var result = ServerListViewModel.BuildSshCommand(Server("host.example.com", 22, "admin"));
        Assert.Equal("ssh admin@host.example.com", result);
    }

    [Fact]
    public void BuildSshCommand_NoUsername_HostOnly()
    {
        var result = ServerListViewModel.BuildSshCommand(Server("host.example.com", 22));
        Assert.Equal("ssh host.example.com", result);
    }

    [Fact]
    public void BuildSshCommand_DefaultPort22_OmitsPortFlag()
    {
        var result = ServerListViewModel.BuildSshCommand(Server("host.example.com", 22, "admin"));
        Assert.DoesNotContain("-p", result);
    }

    [Fact]
    public void BuildSshCommand_NonDefaultPort_AppendsPortFlag()
    {
        var result = ServerListViewModel.BuildSshCommand(Server("host.example.com", 2200, "admin"));
        Assert.Equal("ssh admin@host.example.com -p 2200", result);
    }

    [Fact]
    public void BuildSshCommand_ZeroPort_OmitsPortFlag()
    {
        var result = ServerListViewModel.BuildSshCommand(Server("host.example.com", 0, "admin"));
        Assert.Equal("ssh admin@host.example.com", result);
    }
}
