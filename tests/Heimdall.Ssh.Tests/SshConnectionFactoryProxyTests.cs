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
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

public sealed class SshConnectionFactoryProxyTests
{
    [Theory]
    [InlineData(SshProxyType.Http, ProxyTypes.Http)]
    [InlineData(SshProxyType.Socks5, ProxyTypes.Socks5)]
    [InlineData(SshProxyType.Socks4, ProxyTypes.Socks4)]
    public void BuildConnectionInfo_AppliesValidProxyConfiguration(SshProxyType configured, ProxyTypes expected)
    {
        var connParams = new SshConnectionParams
        {
            Host = "ssh.internal.test",
            Port = 22,
            Username = "ops",
            Password = "pw",
            ProxyType = configured,
            ProxyHost = " proxy.corp.test ",
            ProxyPort = 8080,
            ProxyUsername = "puser",
            ProxyPassword = "psecret"
        };

        ConnectionInfo info = SshConnectionFactory.BuildConnectionInfo(
            connParams,
            [new PasswordAuthenticationMethod("ops", "pw")]);

        Assert.Equal(expected, info.ProxyType);
        Assert.Equal("proxy.corp.test", info.ProxyHost);
        Assert.Equal(8080, info.ProxyPort);
        Assert.Equal("puser", info.ProxyUsername);
        Assert.Equal("psecret", info.ProxyPassword);
    }

    [Fact]
    public void BuildConnectionInfo_IgnoresDisabledProxyAndFailsClosedOnIncompleteEnabledProxy()
    {
        var noProxy = new SshConnectionParams
        {
            Host = "ssh.internal.test",
            Port = 22,
            Username = "ops",
            Password = "pw",
            ProxyType = SshProxyType.None,
            ProxyHost = "proxy.corp.test",
            ProxyPort = 8080
        };
        ConnectionInfo infoNone = SshConnectionFactory.BuildConnectionInfo(
            noProxy,
            [new PasswordAuthenticationMethod("ops", "pw")]);
        Assert.Equal(ProxyTypes.None, infoNone.ProxyType);

        var badPort = new SshConnectionParams
        {
            Host = "ssh.internal.test",
            Port = 22,
            Username = "ops",
            Password = "pw",
            ProxyType = SshProxyType.Socks5,
            ProxyHost = "proxy.corp.test",
            ProxyPort = 0
        };
        Assert.Throws<ArgumentException>(() =>
            SshConnectionFactory.BuildConnectionInfo(
                badPort,
                [new PasswordAuthenticationMethod("ops", "pw")]));

        var missingHost = new SshConnectionParams
        {
            Host = "ssh.internal.test",
            Port = 22,
            Username = "ops",
            Password = "pw",
            ProxyType = SshProxyType.Http,
            ProxyHost = " ",
            ProxyPort = 8080
        };
        Assert.Throws<ArgumentException>(() =>
            SshConnectionFactory.BuildConnectionInfo(
                missingHost,
                [new PasswordAuthenticationMethod("ops", "pw")]));
    }

    [Fact]
    public void GatewayChainResolver_ToConnectionParams_PropagatesGatewayProxySettings()
    {
        var gw = new SshGatewayDto
        {
            Id = "gw-proxy",
            Name = "Corp Bastion",
            Host = "bastion.corp.test",
            Port = 22,
            User = "ops",
            SshProxyType = SshProxyType.Http,
            SshProxyHost = "proxy.corp.test",
            SshProxyPort = 3128,
            SshProxyUsername = "corp-user",
            SshProxyPasswordEncrypted = "enc-proxy-pw"
        };

        List<SshConnectionParams> resolved = GatewayChainResolver.ToConnectionParams(
            [gw],
            enc => enc == "enc-proxy-pw" ? "plain-proxy-pw" : null);

        var hop = Assert.Single(resolved);
        Assert.Equal(SshProxyType.Http, hop.ProxyType);
        Assert.Equal("proxy.corp.test", hop.ProxyHost);
        Assert.Equal(3128, hop.ProxyPort);
        Assert.Equal("corp-user", hop.ProxyUsername);
        Assert.Equal("plain-proxy-pw", hop.ProxyPassword);
    }
}
