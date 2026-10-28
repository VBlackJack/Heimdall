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

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The proxy end to end, through a real WebSocket client on a real loopback listener: who may
/// connect (the token and the page's origin), what happens when the VNC server cannot be reached,
/// and that one bad request does not stop the proxy serving the next.
/// </summary>
public sealed class WebSocketVncProxyTests
{
    private const string PageOrigin = "https://heimdall-vnc.local";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly byte[] Banner = Encoding.ASCII.GetBytes("RFB 003.008\n");

    // ---- S3-2: the token and the origin ----

    // Kills: a token that is not random per proxy (a constant, or the port), or one that is not
    // the 32 bytes the design names.
    [Fact]
    public void TheToken_IsRandomPerProxy_AndThirtyTwoBytesOfBase64Url()
    {
        using var first = new WebSocketVncProxy("127.0.0.1", 1);
        using var second = new WebSocketVncProxy("127.0.0.1", 1);

        Assert.NotEqual(first.Token, second.Token);
        Assert.Matches(new Regex("^[A-Za-z0-9_-]{43}$"), first.Token);
        Assert.EndsWith("/" + first.Token, first.WebSocketUrl, StringComparison.Ordinal);
    }

    // noVNC logs the URL it connects to and the page's console reaches the log file: the token
    // would sit in a file that is read and shared long after the proxy is gone.
    // Kills: the redaction removed.
    [Fact]
    public void TheToken_IsBlankedInALoggedText()
    {
        using var proxy = new WebSocketVncProxy("127.0.0.1", 1);

        string logged = WebSocketVncProxy.RedactToken($"connecting to {proxy.WebSocketUrl}", proxy.Token);

        Assert.DoesNotContain(proxy.Token, logged, StringComparison.Ordinal);
        Assert.Equal($"connecting to ws://127.0.0.1:{proxy.ListenPort}/{WebSocketVncProxy.RedactedToken}", logged);
        Assert.Equal("nothing to hide", WebSocketVncProxy.RedactToken("nothing to hide", null));
    }

    // A client that does not hold the token never reaches the target, whatever else it sends.
    // Kills: the token check removed (the connect then succeeds with the page's origin).
    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData("suffix")]
    [InlineData("prefix")]
    public async Task AClientWithoutTheToken_GetsForbidden(string variant)
    {
        using var server = new FakeVncServer();
        using var proxy = StartProxy(server.Port);

        string path = variant switch
        {
            "wrong" => new string('A', proxy.Token.Length),
            "" => string.Empty,
            "suffix" => proxy.Token + "x",
            _ => proxy.Token[..^1],
        };

        var (status, connected) = await TryConnectAsync(
            $"ws://127.0.0.1:{proxy.ListenPort}/{path}", PageOrigin);

        Assert.False(connected);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(0, server.Accepted);
    }

    [Fact]
    public async Task AClientWithTheToken_AndThePageOrigin_ConnectsAndReceivesTheServersBanner()
    {
        using var server = new FakeVncServer();
        using var proxy = StartProxy(server.Port);

        using var client = await ConnectAsync(proxy.WebSocketUrl, PageOrigin);

        Assert.Equal(WebSocketState.Open, client.State);
        Assert.Equal(Banner, await ReadAsync(client, Banner.Length));
    }

    // A wrong password reconnects on the same proxy: the token must outlive the first connection.
    // Kills: a token spent by its first use.
    [Fact]
    public async Task TheToken_StaysValidForALaterConnectionOnTheSameProxy()
    {
        using var server = new FakeVncServer();
        using var proxy = StartProxy(server.Port);

        using (var first = await ConnectAsync(proxy.WebSocketUrl, PageOrigin))
        {
            Assert.Equal(Banner, await ReadAsync(first, Banner.Length));
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
        }

        using var second = await ConnectAsync(proxy.WebSocketUrl, PageOrigin);
        Assert.Equal(Banner, await ReadAsync(second, Banner.Length));
    }

    // The token alone is not enough: another loopback page that learned it still has the wrong
    // origin. Kills: the loopback allowlist the origin check used to carry.
    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1:8088")]
    [InlineData("https://localhost")]
    [InlineData("")]
    public async Task AClientWithTheToken_ButNotThePageOrigin_GetsForbidden(string origin)
    {
        using var server = new FakeVncServer();
        using var proxy = StartProxy(server.Port);

        var (status, connected) = await TryConnectAsync(proxy.WebSocketUrl, origin);

        Assert.False(connected);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(0, server.Accepted);
    }

    // ---- R3-1: why the server could not be reached ----

    // Kills: one reason for every failure.
    [Fact]
    public async Task ARefusedTarget_RaisesRefused()
    {
        VncTargetFailure failure = await FailureOfAsync(
            (_, _, _) => throw new SocketException((int)SocketError.ConnectionRefused),
            TimeSpan.FromSeconds(30));

        Assert.Equal(VncTargetFailure.Refused, failure);
    }

    // Kills: the timeout reported as the generic failure, or no timeout at all (the connector
    // below never returns, so the proxy would wait for it).
    [Fact]
    public async Task ATargetThatNeverAnswers_RaisesTimedOut_AfterTheConnectTimeout()
    {
        VncTargetFailure failure = await FailureOfAsync(
            async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Stream.Null;
            },
            TimeSpan.FromMilliseconds(150));

        Assert.Equal(VncTargetFailure.TimedOut, failure);
    }

    [Theory]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.HostNotFound)]
    public async Task AnUnreachableTarget_RaisesUnreachable(SocketError error)
    {
        VncTargetFailure failure = await FailureOfAsync(
            (_, _, _) => throw new SocketException((int)error),
            TimeSpan.FromSeconds(30));

        Assert.Equal(VncTargetFailure.Unreachable, failure);
    }

    [Fact]
    public void TheConnectTimeout_IsTenSeconds()
    {
        Assert.Equal(10, WebSocketVncProxy.TargetConnectTimeoutSeconds);
    }

    [Fact]
    public async Task ARefusedConnect_ToARealClosedPort_RaisesRefused()
    {
        // The default connector, on a port nothing listens on: the real mapping, not a stand-in.
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        VncTargetFailure failure = await FailureOfDefaultConnectorAsync(closedPort);

        Assert.Equal(VncTargetFailure.Refused, failure);
    }

    // ---- R3-7: one failed request does not end the proxy ----

    // An upgrade request that cannot be accepted (no WebSocket key) used to end the accept loop;
    // the next connect then waited on a proxy nobody served.
    // Kills: the per-request catch removed.
    [Fact]
    public async Task AnAbortedUpgrade_DoesNotStopTheProxyServingTheNextClient()
    {
        using var server = new FakeVncServer();
        using var proxy = StartProxy(server.Port);

        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, proxy.ListenPort);
            string request =
                $"GET /{proxy.Token} HTTP/1.1\r\nHost: 127.0.0.1:{proxy.ListenPort}\r\n"
                + "Upgrade: websocket\r\nConnection: Upgrade\r\n"
                + $"Origin: {PageOrigin}\r\n\r\n";
            await raw.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
            await raw.GetStream().FlushAsync();
        }

        using var client = await ConnectAsync(proxy.WebSocketUrl, PageOrigin);

        Assert.Equal(WebSocketState.Open, client.State);
        Assert.Equal(Banner, await ReadAsync(client, Banner.Length));
    }

    // ---- helpers ----

    private static WebSocketVncProxy StartProxy(int targetPort)
    {
        var proxy = new WebSocketVncProxy(
            "127.0.0.1",
            targetPort,
            async (host, port, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(host, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
            TimeSpan.FromSeconds(WebSocketVncProxy.TargetConnectTimeoutSeconds));
        proxy.Start();
        return proxy;
    }

    private static async Task<VncTargetFailure> FailureOfAsync(
        Func<string, int, CancellationToken, Task<Stream>> connect,
        TimeSpan timeout)
    {
        using var proxy = new WebSocketVncProxy("127.0.0.1", 1, connect, timeout);
        var raised = new TaskCompletionSource<VncTargetFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.TargetConnectFailed += failure => raised.TrySetResult(failure);
        proxy.Start();

        using var client = await ConnectAsync(proxy.WebSocketUrl, PageOrigin);
        return await raised.Task.WaitAsync(Patience);
    }

    private static async Task<VncTargetFailure> FailureOfDefaultConnectorAsync(int port)
    {
        using var proxy = new WebSocketVncProxy("127.0.0.1", port);
        var raised = new TaskCompletionSource<VncTargetFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.TargetConnectFailed += failure => raised.TrySetResult(failure);
        proxy.Start();

        using var client = await ConnectAsync(proxy.WebSocketUrl, PageOrigin);
        return await raised.Task.WaitAsync(Patience);
    }

    private static async Task<ClientWebSocket> ConnectAsync(string url, string origin)
    {
        var client = new ClientWebSocket();
        client.Options.AddSubProtocol("binary");
        client.Options.CollectHttpResponseDetails = true;
        if (origin.Length > 0)
        {
            client.Options.SetRequestHeader("Origin", origin);
        }

        using var cts = new CancellationTokenSource(Patience);
        await client.ConnectAsync(new Uri(url), cts.Token);
        return client;
    }

    private static async Task<(HttpStatusCode Status, bool Connected)> TryConnectAsync(string url, string origin)
    {
        var client = new ClientWebSocket();
        try
        {
            client.Options.AddSubProtocol("binary");
            client.Options.CollectHttpResponseDetails = true;
            if (origin.Length > 0)
            {
                client.Options.SetRequestHeader("Origin", origin);
            }

            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(new Uri(url), cts.Token);
            return (HttpStatusCode.SwitchingProtocols, true);
        }
        catch (WebSocketException)
        {
            return (client.HttpStatusCode, false);
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task<byte[]> ReadAsync(ClientWebSocket client, int count)
    {
        var received = new List<byte>();
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(Patience);

        while (received.Count < count)
        {
            var result = await client.ReceiveAsync(buffer, cts.Token);
            received.AddRange(buffer.AsSpan(0, result.Count).ToArray());
        }

        return [.. received];
    }

    /// <summary>A VNC server stand-in: says its protocol banner to whoever connects, then waits.</summary>
    private sealed class FakeVncServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = [];
        private readonly CancellationTokenSource _cts = new();
        private int _accepted;

        public FakeVncServer()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        private async Task AcceptAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    lock (_clients)
                    {
                        _clients.Add(client);
                    }

                    Interlocked.Increment(ref _accepted);
                    await client.GetStream().WriteAsync(Banner, _cts.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
                // The test is over.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            lock (_clients)
            {
                foreach (TcpClient client in _clients)
                {
                    client.Dispose();
                }
            }

            _cts.Dispose();
        }
    }
}
