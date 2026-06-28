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

using Heimdall.App.Services;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class RemoteClipboardEndpointKeyTests
{
    [Fact]
    public void FromParts_SameHostPortUser_NormalizesHostCase()
    {
        string left = RemoteClipboardEndpointKey.FromParts("SFTP.EXAMPLE.TEST", 22, "alice");
        string right = RemoteClipboardEndpointKey.FromParts("sftp.example.test", 22, "alice");

        Assert.Equal(right, left);
    }

    [Theory]
    [InlineData("one.example.test", 22, "alice", "two.example.test", 22, "alice")]
    [InlineData("one.example.test", 22, "alice", "one.example.test", 2222, "alice")]
    [InlineData("one.example.test", 22, "alice", "one.example.test", 22, "bob")]
    public void FromParts_DifferentHostPortOrUser_ProducesDifferentKey(
        string leftHost,
        int leftPort,
        string leftUser,
        string rightHost,
        int rightPort,
        string rightUser)
    {
        string left = RemoteClipboardEndpointKey.FromParts(leftHost, leftPort, leftUser);
        string right = RemoteClipboardEndpointKey.FromParts(rightHost, rightPort, rightUser);

        Assert.NotEqual(right, left);
    }

    [Fact]
    public void FromSsh_UsesLogicalHostAndPortWhenPresent()
    {
        SshConnectionParams sshParams = new()
        {
            Host = "127.0.0.1",
            Port = 50022,
            LogicalHost = "SFTP.EXAMPLE.TEST",
            LogicalPort = 22,
            Username = "alice"
        };

        string key = RemoteClipboardEndpointKey.FromSsh(sshParams);

        Assert.Equal("protocol=sftp;host=sftp.example.test;port=22;user=alice", key);
    }

    [Fact]
    public void TryFromEndpointLabel_ParsesUserHostPort()
    {
        bool parsed = RemoteClipboardEndpointKey.TryFromEndpointLabel(
            "alice@SFTP.EXAMPLE.TEST:2222",
            22,
            out string key);

        Assert.True(parsed);
        Assert.Equal("host=sftp.example.test;port=2222;user=alice", key);
    }
}
