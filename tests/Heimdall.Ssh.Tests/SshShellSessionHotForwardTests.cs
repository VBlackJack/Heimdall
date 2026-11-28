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

using Heimdall.Ssh;

namespace Heimdall.Ssh.Tests;

public sealed class SshShellSessionHotForwardTests
{
    [Fact]
    public void TryBuildSessionForward_Local_BuildsLoopbackBoundForwardWithUniqueId()
    {
        var req = new SshSessionForwardRequest(
            SshSessionForwardKind.Local,
            LocalPort: 8080,
            RemoteHost: "db.internal",
            RemotePort: 5432,
            LocalBindHost: "0.0.0.0",
            Label: "DB");

        bool ok = SshShellSession.TryBuildSessionForward(req, "bastion.corp", out var port, out var info, out var desc, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.NotNull(port);
        Assert.NotNull(info);
        Assert.True(info.IsSessionBound);
        Assert.False(string.IsNullOrWhiteSpace(info.SessionForwardId));
        Assert.Equal("127.0.0.1", info.LocalBindHost);
        Assert.Equal(8080, info.LocalPort);
        Assert.Equal("db.internal", info.RemoteHost);
        Assert.Equal(5432, info.RemotePort);
        Assert.Contains("-L 127.0.0.1:8080:db.internal:5432", desc);
        port.Dispose();
    }

    [Fact]
    public void TryBuildSessionForward_DynamicSocks_BuildsSocks5Info()
    {
        var req = new SshSessionForwardRequest(
            SshSessionForwardKind.DynamicSocks,
            LocalPort: 1080);

        bool ok = SshShellSession.TryBuildSessionForward(req, "edge.corp", out var port, out var info, out _, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.NotNull(port);
        Assert.NotNull(info);
        Assert.True(info.IsSessionBound);
        Assert.Equal(1080, info.SocksProxyPort);
        Assert.False(string.IsNullOrWhiteSpace(info.SessionForwardId));
        port.Dispose();
    }

    [Fact]
    public void TryBuildSessionForward_Remote_BuildsReverseForwardInfo()
    {
        var req = new SshSessionForwardRequest(
            SshSessionForwardKind.Remote,
            LocalPort: 3000,
            RemotePort: 9000);

        bool ok = SshShellSession.TryBuildSessionForward(req, "edge.corp", out var port, out var info, out _, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.NotNull(port);
        Assert.NotNull(info);
        Assert.True(info.IsSessionBound);
        Assert.True(info.IsRemoteOnly);
        Assert.Equal(9000, info.RemoteBindPort);
        Assert.Equal(3000, info.EffectiveRemoteLocalPort);
        port.Dispose();
    }

    [Fact]
    public void TryBuildSessionForward_RejectsInvalidPortsAndMissingHost()
    {
        Assert.False(SshShellSession.TryBuildSessionForward(
            new SshSessionForwardRequest(SshSessionForwardKind.Local, 0, "host", 80),
            "srv", out _, out _, out _, out _));

        Assert.False(SshShellSession.TryBuildSessionForward(
            new SshSessionForwardRequest(SshSessionForwardKind.Local, 8080, "", 80),
            "srv", out _, out _, out _, out _));

        Assert.False(SshShellSession.TryBuildSessionForward(
            new SshSessionForwardRequest(SshSessionForwardKind.Remote, 3000, RemotePort: 70000),
            "srv", out _, out _, out _, out _));
    }

    [Fact]
    public void AddSessionPortForward_WhenDisconnected_FailsCleanly()
    {
        using var session = new SshShellSession();
        var result = session.AddSessionPortForward(new SshSessionForwardRequest(SshSessionForwardKind.DynamicSocks, 1080));

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }
}
