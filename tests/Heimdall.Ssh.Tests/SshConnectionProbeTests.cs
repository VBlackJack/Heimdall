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

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Heimdall.Ssh.Tests;

public sealed class SshConnectionProbeTests
{
    [Fact]
    public async Task ProbeAsync_ClosedPort_ReturnsNetworkFailure()
    {
        var port = GetClosedLoopbackPort();

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, 1000);

        Assert.False(result.Success);
        Assert.True(
            result.FailureCode is SshFailureCode.NetworkRefused or SshFailureCode.NetworkTimedOut,
            $"Expected a network failure, got {result.FailureCode}.");
        Assert.True(
            result.MessageKey == SshConnectionProbe.MessageKeyConnectionRefused
                || result.MessageKey == SshConnectionProbe.MessageKeyConnectionTimedOut,
            $"Expected a network failure message key, got {result.MessageKey}.");
    }

    [Fact]
    public async Task ProbeAsync_MissingBanner_ReturnsProtocolFailureMessageKey()
    {
        var (port, serverTask) = StartSingleResponseServer("");

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, 1000);
        await serverTask;

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.ProtocolError, result.FailureCode);
        Assert.Null(result.Banner);
        Assert.Equal(SshConnectionProbe.MessageKeyMissingBanner, result.MessageKey);
        Assert.Empty(result.MessageArguments);
    }

    [Fact]
    public async Task ProbeAsync_NonSshBanner_ReturnsProtocolFailure()
    {
        var (port, serverTask) = StartSingleResponseServer("HTTP/1.1 200 OK\r\n");

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, 1000);
        await serverTask;

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.ProtocolError, result.FailureCode);
        Assert.Equal("HTTP/1.1 200 OK", result.Banner);
        Assert.Equal(SshConnectionProbe.MessageKeyNonSshBanner, result.MessageKey);
        Assert.Empty(result.MessageArguments);
    }

    [Fact]
    public async Task ProbeAsync_NonSshUtf8Banner_DecodesDiagnosticText()
    {
        const string banner = "Acc\u00e8s refus\u00e9";
        var (port, serverTask) = StartSingleResponseServer(banner + "\r\n");

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, 1000);
        await serverTask;

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.ProtocolError, result.FailureCode);
        Assert.Equal(banner, result.Banner);
        Assert.Equal(SshConnectionProbe.MessageKeyNonSshBanner, result.MessageKey);
    }

    [Fact]
    public async Task ProbeAsync_PreLoginLineBeforeSshBanner_ReturnsSuccess()
    {
        var (port, serverTask) = StartSingleResponseServer("Authorized access only\r\nSSH-2.0-OpenSSH_9\r\n");

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, 1000);
        await serverTask;

        Assert.True(result.Success);
        Assert.Equal("SSH-2.0-OpenSSH_9", result.Banner);
        Assert.Null(result.FailureCode);
        Assert.Null(result.MessageKey);
        Assert.Empty(result.MessageArguments);
    }

    [Fact]
    public async Task ProbeAsync_SshBanner_ReturnsSuccess()
    {
        var (port, serverTask) = StartSingleResponseServer("SSH-2.0-MockServer\r\n");

        var result = await SshConnectionProbe.ProbeAsync("127.0.0.1", port, 1000);
        await serverTask;

        Assert.True(result.Success);
        Assert.Equal("SSH-2.0-MockServer", result.Banner);
        Assert.Null(result.FailureCode);
        Assert.Null(result.MessageKey);
        Assert.Empty(result.MessageArguments);
    }

    private static int GetClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static (int Port, Task ServerTask) StartSingleResponseServer(string response)
    {
        return StartSingleResponseServer(Encoding.UTF8.GetBytes(response));
    }

    private static (int Port, Task ServerTask) StartSingleResponseServer(byte[] response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using (listener)
            using (var client = await listener.AcceptTcpClientAsync())
            await using (var stream = client.GetStream())
            {
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            }
        });

        return (port, serverTask);
    }
}
