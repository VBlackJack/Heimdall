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

namespace Heimdall.Core.Tests;

public sealed class SshLocalForwardSpecTests
{
    [Theory]
    [InlineData("8080 127.0.0.1:80", "127.0.0.1", 8080, "127.0.0.1", 80)]
    [InlineData("127.0.0.1:5432 db.internal:5432", "127.0.0.1", 5432, "db.internal", 5432)]
    [InlineData("9090:10.0.0.5:443", "127.0.0.1", 9090, "10.0.0.5", 443)]
    [InlineData("::1:8443 app.local:443", "::1", 8443, "app.local", 443)]
    public void TryParse_AcceptsValidOpenSshAndCompactSpecs(
        string raw,
        string expectedBind,
        int expectedLocalPort,
        string expectedRemoteHost,
        int expectedRemotePort)
    {
        Assert.True(SshLocalForwardSpec.TryParse(raw, out var spec));
        Assert.NotNull(spec);
        Assert.Equal(expectedBind, spec.BindAddress);
        Assert.Equal(expectedLocalPort, spec.LocalPort);
        Assert.Equal(expectedRemoteHost, spec.RemoteHost);
        Assert.Equal(expectedRemotePort, spec.RemotePort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0 127.0.0.1:80")]
    [InlineData("70000 127.0.0.1:80")]
    [InlineData("8080 bad host:80")]
    [InlineData("8080 127.0.0.1:0")]
    public void TryParse_RejectsInvalidSpecs(string raw)
    {
        Assert.False(SshLocalForwardSpec.TryParse(raw, out _));
    }

    [Fact]
    public void SshConfigExporter_EmitsLocalForwardDirectives()
    {
        var server = new ServerProfileDto
        {
            DisplayName = "App",
            ConnectionType = "SSH",
            RemoteServer = "app.example.com",
            SshPort = 22,
            SshLocalForwards = ["8080:127.0.0.1:80", "5432:db.internal:5432"]
        };

        var exported = SshConfigExporter.ExportInventory([server], []);

        Assert.Contains("LocalForward 127.0.0.1:8080 127.0.0.1:80", exported, StringComparison.Ordinal);
        Assert.Contains("LocalForward 127.0.0.1:5432 db.internal:5432", exported, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedTunnelPresetDto_FormatsModeSummaries()
    {
        var local = new SavedTunnelPresetDto
        {
            Name = "Postgres",
            Mode = SavedTunnelPresetDto.ModeLocal,
            LocalPort = 5432,
            RemoteHost = "db.internal",
            RemotePort = 5432
        };
        var socks = new SavedTunnelPresetDto
        {
            Name = "Proxy",
            Mode = SavedTunnelPresetDto.ModeDynamicSocks5,
            LocalPort = 1080
        };
        var remote = new SavedTunnelPresetDto
        {
            Name = "Webhook",
            Mode = SavedTunnelPresetDto.ModeRemote,
            RemoteBindPort = 8080,
            RemoteLocalPort = 3000
        };

        Assert.Equal("Postgres (-L :5432 -> db.internal:5432)", local.DisplayLabel);
        Assert.Equal("Proxy (-D 127.0.0.1:1080)", socks.DisplayLabel);
        Assert.Equal("Webhook (-R :8080 -> 127.0.0.1:3000)", remote.DisplayLabel);
    }
}
