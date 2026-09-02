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
using System.Net.WebSockets;

namespace Heimdall.App.Services;

/// <summary>
/// Lightweight WebSocket-to-TCP proxy that bridges a noVNC WebSocket client
/// to a raw VNC (RFB) TCP server. Listens on a random local port, accepting
/// a single WebSocket connection and bidirectionally piping binary frames
/// to/from the VNC server.
/// </summary>
public sealed class WebSocketVncProxy : IDisposable
{
    private const int BufferSize = 65536;

    // noVNC requests this subprotocol; RFC 6455 §4.1 requires the server to echo
    // a requested subprotocol or the browser fails the connection.
    private const string BinarySubProtocol = "binary";

    private static readonly HashSet<string> AllowedOriginHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "heimdall-vnc.local",
        "127.0.0.1",
        "localhost"
    };

    private readonly string _vncHost;
    private readonly int _vncPort;
    private readonly HttpListener _httpListener;
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _tcpClient;
    private bool _disposed;

    /// <summary>
    /// The local port on which the WebSocket proxy is listening.
    /// noVNC connects to <c>ws://localhost:{ListenPort}</c>.
    /// </summary>
    public int ListenPort { get; }

    public WebSocketVncProxy(string vncHost, int vncPort)
    {
        _vncHost = vncHost;
        _vncPort = vncPort;

        // Find a free local port by binding to port 0
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        ListenPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://127.0.0.1:{ListenPort}/");
    }

    /// <summary>
    /// Starts the WebSocket listener and begins accepting connections.
    /// Returns immediately; the proxy runs on background tasks.
    /// </summary>
    public void Start()
    {
        _httpListener.Start();
        _ = AcceptLoopAsync(_cts.Token).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                Core.Logging.FileLogger.Error(
                    $"VNC proxy accept loop failed: {t.Exception.GetBaseException()}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await _httpListener.GetContextAsync().ConfigureAwait(false);

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                // Validate Origin to prevent Cross-Site WebSocket Hijacking (CSWSH).
                // Parse the URI and compare scheme + host exactly - StartsWith would allow
                // subdomain bypasses such as "https://heimdall-vnc.local.attacker.tld".
                var origin = context.Request.Headers["Origin"];
                if (!IsAllowedOrigin(origin))
                {
                    Core.Logging.FileLogger.Warn(
                        $"VNC proxy rejected WebSocket: {(string.IsNullOrEmpty(origin) ? "missing Origin header" : $"untrusted origin: {origin}")}");
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    continue;
                }

                // Echo the "binary" subprotocol when the client offers it: Chromium
                // fails the WebSocket when a requested subprotocol is not echoed back
                // (RFC 6455 §4.1; novnc/websockify#266).
                var offeredProtocols = context.Request.Headers["Sec-WebSocket-Protocol"];
                var acceptedProtocol = SelectSubProtocol(offeredProtocols);
                Core.Logging.FileLogger.Info(
                    $"VNC proxy WebSocket accept: offered subprotocols [{offeredProtocols ?? "<none>"}], accepting [{acceptedProtocol ?? "<none>"}]");

                var wsContext = await context.AcceptWebSocketAsync(subProtocol: acceptedProtocol)
                    .ConfigureAwait(false);

                // Handle one connection at a time (VNC is single-client)
                await HandleConnectionAsync(wsContext.WebSocket, ct).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // HttpListener was disposed during shutdown.
        }
        catch (HttpListenerException)
        {
            // Listener stopped.
        }
    }

    /// <summary>
    /// Returns the subprotocol to echo back to the client, or <c>null</c> when the
    /// client did not offer the <c>binary</c> subprotocol noVNC requests.
    /// Subprotocol names are case-sensitive tokens per RFC 6455.
    /// </summary>
    internal static string? SelectSubProtocol(string? offeredProtocols)
    {
        if (string.IsNullOrEmpty(offeredProtocols))
            return null;

        foreach (var protocol in offeredProtocols.Split(','))
        {
            if (string.Equals(protocol.Trim(), BinarySubProtocol, StringComparison.Ordinal))
                return BinarySubProtocol;
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="origin"/> is a well-formed HTTP(S) URI
    /// whose host is in the <see cref="AllowedOriginHosts"/> allowlist.
    /// Exact host comparison prevents subdomain bypass attacks.
    /// </summary>
    internal static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
            return false;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme is not ("http" or "https"))
            return false;

        return AllowedOriginHosts.Contains(uri.Host);
    }

    private async Task HandleConnectionAsync(WebSocket ws, CancellationToken ct)
    {
        TcpClient? tcp = null;
        NetworkStream? tcpStream = null;

        try
        {
            tcp = new TcpClient();
            await tcp.ConnectAsync(_vncHost, _vncPort, ct).ConfigureAwait(false);
            tcpStream = tcp.GetStream();
            _tcpClient = tcp;

            var wsToTcp = RelayWebSocketToTcpAsync(ws, tcpStream, ct);
            var tcpToWs = RelayTcpToWebSocketAsync(tcpStream, ws, ct);

            await Task.WhenAny(wsToTcp, tcpToWs).ConfigureAwait(false);

            // Observe relay outcomes: the task that ended first carries the root
            // cause; the surviving task will fault later when the finally block
            // disposes the streams under it, so tag it as secondary.
            await ObserveRelayOutcomeAsync(wsToTcp, "ws->tcp").ConfigureAwait(false);
            await ObserveRelayOutcomeAsync(tcpToWs, "tcp->ws").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or WebSocketException)
        {
            // Connection ended or was cancelled.
            Core.Logging.FileLogger.Warn($"VNC proxy connection ended: {ex}");
        }
        finally
        {
            _tcpClient = null;
            tcpStream?.Dispose();
            tcp?.Dispose();

            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn(
                        $"VNC proxy WebSocket close: {ex.Message} | base: {ex.GetBaseException()}");
                }
            }

            ws.Dispose();
        }
    }

    /// <summary>
    /// Observes a relay task so its exception is never lost. A task that already
    /// completed is awaited and its full exception chain logged as the likely root
    /// cause; a still-running task gets a fault continuation tagged as a secondary
    /// fault, because the stream teardown in the caller's finally block will make
    /// it fault moments later regardless of the original failure.
    /// </summary>
    private static async Task ObserveRelayOutcomeAsync(Task relayTask, string direction)
    {
        if (relayTask.IsCompleted)
        {
            try
            {
                await relayTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown; the relay exit log already records it.
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Error($"VNC proxy relay {direction} faulted: {ex}");
            }

            return;
        }

        _ = relayTask.ContinueWith(t =>
        {
            if (t.Exception is not null)
            {
                Core.Logging.FileLogger.Warn(
                    $"VNC proxy relay {direction} secondary fault after teardown (not the root cause): {t.Exception.GetBaseException()}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Reads binary WebSocket frames from noVNC and writes them to the VNC TCP stream.
    /// </summary>
    private static async Task RelayWebSocketToTcpAsync(
        WebSocket ws, NetworkStream tcpStream, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long totalBytes = 0;
        var exitReason = "loop condition (socket state or cancellation)";

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    exitReason = "Close frame received";
                    break;
                }

                if (result.Count > 0)
                {
                    totalBytes += result.Count;
                    await tcpStream.WriteAsync(buffer.AsMemory(0, result.Count), ct)
                        .ConfigureAwait(false);
                    await tcpStream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            exitReason = $"exception: {ex.GetType().Name}";
            throw;
        }
        finally
        {
            Core.Logging.FileLogger.Warn(
                $"VNC proxy relay ws->tcp exited ({exitReason}), {totalBytes} bytes relayed");
        }
    }

    /// <summary>
    /// Reads raw TCP data from the VNC server and sends it as binary WebSocket frames.
    /// </summary>
    private static async Task RelayTcpToWebSocketAsync(
        NetworkStream tcpStream, WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long totalBytes = 0;
        var exitReason = "loop condition (socket state or cancellation)";

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                int bytesRead = await tcpStream.ReadAsync(buffer, ct).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    exitReason = "0-byte read (server closed connection)";
                    break;
                }

                totalBytes += bytesRead;
                await ws.SendAsync(
                    new ArraySegment<byte>(buffer, 0, bytesRead),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            exitReason = $"exception: {ex.GetType().Name}";
            throw;
        }
        finally
        {
            Core.Logging.FileLogger.Warn(
                $"VNC proxy relay tcp->ws exited ({exitReason}), {totalBytes} bytes relayed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cts.Cancel();

        try
        {
            _httpListener.Stop();
            _httpListener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        _tcpClient?.Dispose();
        _cts.Dispose();
    }
}
