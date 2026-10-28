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

using System.Buffers.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Heimdall.App.Services;

/// <summary>Why the proxy could not reach the VNC server it bridges to.</summary>
public enum VncTargetFailure
{
    /// <summary>The address answered and refused: nothing listens on that port.</summary>
    Refused,

    /// <summary>Nothing answered within the connect timeout.</summary>
    TimedOut,

    /// <summary>The name did not resolve, or no route leads to the address.</summary>
    Unreachable,
}

/// <summary>Raised by the proxy when the connection to the VNC server cannot be made.</summary>
public sealed class VncTargetConnectException : Exception
{
    public VncTargetConnectException(VncTargetFailure failure, Exception? inner = null)
        : base($"VNC target connect failed: {failure}", inner)
    {
        Failure = failure;
    }

    public VncTargetFailure Failure { get; }
}

/// <summary>
/// Lightweight WebSocket-to-TCP proxy that bridges a noVNC WebSocket client
/// to a raw VNC (RFB) TCP server. Listens on a random local port, accepting
/// WebSocket connections one at a time and bidirectionally piping binary frames
/// to/from the VNC server.
/// </summary>
/// <remarks>
/// Any local process can reach a loopback port, and a web page can open a WebSocket to it, so the
/// Origin check alone does not say who is asking. Every proxy therefore carries a random token for
/// its whole lifetime (a password retry reconnects on the same proxy) and only the URL given to
/// the page, <see cref="WebSocketUrl"/>, holds it.
/// </remarks>
public sealed class WebSocketVncProxy : IDisposable
{
    private const int BufferSize = 65536;

    /// <summary>The size of the random token, in bytes before encoding.</summary>
    internal const int TokenByteLength = 32;

    /// <summary>How long the proxy waits for the VNC server to accept the TCP connection.</summary>
    internal const int TargetConnectTimeoutSeconds = 10;

    /// <summary>Pause before the accept loop asks again after a failure that is not a shutdown.</summary>
    private const int AcceptRetryDelayMilliseconds = 50;

    // noVNC requests this subprotocol; RFC 6455 §4.1 requires the server to echo
    // a requested subprotocol or the browser fails the connection.
    private const string BinarySubProtocol = "binary";

    /// <summary>
    /// The page that talks to the proxy. A WebSocket handshake from a page carries that page's
    /// origin, so the origin the proxy accepts is the one of this document and no other.
    /// </summary>
    internal const string PageDocumentUrl = "https://heimdall-vnc.local/vnc.html";

    private static readonly WebViewDocumentPolicy PagePolicy = new(PageDocumentUrl);

    private readonly string _vncHost;
    private readonly int _vncPort;
    private readonly Func<string, int, CancellationToken, Task<Stream>> _connectTarget;
    private readonly TimeSpan _connectTimeout;
    private readonly byte[] _expectedPathBytes;
    private readonly HttpListener _httpListener;
    private readonly CancellationTokenSource _cts = new();
    private Stream? _targetStream;
    private bool _disposed;

    /// <summary>
    /// The local port on which the WebSocket proxy is listening.
    /// noVNC connects to <see cref="WebSocketUrl"/>.
    /// </summary>
    public int ListenPort { get; }

    /// <summary>
    /// The secret path segment a client must present, random per proxy. Not for logs.
    /// </summary>
    internal string Token { get; }

    /// <summary>What replaces the token in a text that is logged.</summary>
    internal const string RedactedToken = "<token>";

    /// <summary>
    /// Blanks the token in a text bound for the log. The page logs the URL it connects to, and a
    /// log is read and shared long after the proxy is gone: the token has no place in it.
    /// </summary>
    internal static string RedactToken(string text, string? token)
    {
        return string.IsNullOrEmpty(token)
            ? text
            : text.Replace(token, RedactedToken, StringComparison.Ordinal);
    }

    /// <summary>The URL the page connects to: loopback, the port, and the token as the path.</summary>
    public string WebSocketUrl => $"ws://127.0.0.1:{ListenPort}/{Token}";

    /// <summary>
    /// Raised when the VNC server cannot be reached, with the reason. It comes from the proxy and
    /// not from the page: the page only ever sees its socket close, whatever the cause.
    /// </summary>
    public event Action<VncTargetFailure>? TargetConnectFailed;

    public WebSocketVncProxy(string vncHost, int vncPort)
        : this(vncHost, vncPort, ConnectTcpAsync, TimeSpan.FromSeconds(TargetConnectTimeoutSeconds))
    {
    }

    /// <param name="connectTarget">Opens the stream to the VNC server; replaced by a test.</param>
    /// <param name="connectTimeout">The longest <paramref name="connectTarget"/> may take.</param>
    internal WebSocketVncProxy(
        string vncHost,
        int vncPort,
        Func<string, int, CancellationToken, Task<Stream>> connectTarget,
        TimeSpan connectTimeout)
    {
        ArgumentNullException.ThrowIfNull(connectTarget);

        _vncHost = vncHost;
        _vncPort = vncPort;
        _connectTarget = connectTarget;
        _connectTimeout = connectTimeout;

        Token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));
        _expectedPathBytes = Encoding.UTF8.GetBytes("/" + Token);

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

    /// <summary>
    /// Serves requests until the listener is disposed. A request that fails (an aborted
    /// upgrade, a client that vanished mid-handshake) costs that request only: it used to end
    /// the loop, and the next connect then waited forever on a proxy nobody was serving.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _httpListener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
            {
                if (ct.IsCancellationRequested || !_httpListener.IsListening)
                {
                    // HttpListener was stopped or disposed during shutdown.
                    return;
                }

                Core.Logging.FileLogger.Warn($"VNC proxy could not accept a request: {ex.Message}");
                await DelayBeforeNextAcceptAsync(ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ServeRequestAsync(context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpListenerException
                or IOException or ObjectDisposedException or InvalidOperationException)
            {
                Core.Logging.FileLogger.Warn($"VNC proxy request failed, serving the next one: {ex.Message}");
                AbortResponse(context);
            }
        }
    }

    private static async Task DelayBeforeNextAcceptAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AcceptRetryDelayMilliseconds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: the loop condition ends it.
        }
    }

    private static void AbortResponse(HttpListenerContext context)
    {
        try
        {
            context.Response.Abort();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
        {
            // Already closed by the failed upgrade.
        }
    }

    private async Task ServeRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        // The token comes first: a client that does not hold it learns nothing else about the proxy.
        if (!IsAuthorizedPath(context.Request.Url?.AbsolutePath))
        {
            Core.Logging.FileLogger.Warn("VNC proxy rejected WebSocket: missing or wrong token");
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        // Validate Origin to prevent Cross-Site WebSocket Hijacking (CSWSH).
        // Parse the URI and compare scheme + host + port exactly - StartsWith would allow
        // subdomain bypasses such as "https://heimdall-vnc.local.attacker.tld".
        var origin = context.Request.Headers["Origin"];
        if (!IsAllowedOrigin(origin))
        {
            Core.Logging.FileLogger.Warn(
                $"VNC proxy rejected WebSocket: {(string.IsNullOrEmpty(origin) ? "missing Origin header" : $"untrusted origin: {origin}")}");
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        // Echo the "binary" subprotocol when the client offers it: Chromium
        // fails the WebSocket when a requested subprotocol is not echoed back
        // (RFC 6455 §4.1; novnc/websockify#266).
        var offeredProtocols = context.Request.Headers["Sec-WebSocket-Protocol"];
        var acceptedProtocol = SelectSubProtocol(offeredProtocols);
        Core.Logging.FileLogger.Info(
            $"VNC proxy WebSocket accept: origin [{origin}], offered subprotocols [{offeredProtocols ?? "<none>"}], accepting [{acceptedProtocol ?? "<none>"}]");

        var wsContext = await context.AcceptWebSocketAsync(subProtocol: acceptedProtocol)
            .ConfigureAwait(false);

        // Handle one connection at a time (VNC is single-client)
        await HandleConnectionAsync(wsContext.WebSocket, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <c>true</c> when the request path is exactly the proxy's token. The comparison
    /// takes the same time wherever the two differ.
    /// </summary>
    internal bool IsAuthorizedPath(string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(absolutePath), _expectedPathBytes);
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
    /// Returns <c>true</c> when <paramref name="origin"/> is the origin of the VNC page itself
    /// (scheme, host and port). Another loopback origin is another page, or another program with
    /// a web view, and is refused; so is any subdomain of the page's host.
    /// </summary>
    internal static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
            return false;

        return PagePolicy.IsTrustedOrigin(origin);
    }

    private async Task HandleConnectionAsync(WebSocket ws, CancellationToken ct)
    {
        Stream? targetStream = null;

        try
        {
            try
            {
                targetStream = await ConnectTargetAsync(ct).ConfigureAwait(false);
            }
            catch (VncTargetConnectException ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"VNC proxy could not reach {_vncHost}:{_vncPort} ({ex.Failure}): {ex.InnerException?.Message}");
                RaiseTargetConnectFailed(ex.Failure);
                return;
            }

            _targetStream = targetStream;

            var wsToTcp = RelayWebSocketToTcpAsync(ws, targetStream, ct);
            var tcpToWs = RelayTcpToWebSocketAsync(targetStream, ws, ct);

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
            _targetStream = null;
            targetStream?.Dispose();

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
    /// Opens the stream to the VNC server within the connect timeout, and names the reason when
    /// it cannot. Shutting the proxy down is not a failure of the server and passes through.
    /// </summary>
    private async Task<Stream> ConnectTargetAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_connectTimeout);

        try
        {
            return await _connectTarget(_vncHost, _vncPort, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new VncTargetConnectException(VncTargetFailure.TimedOut, ex);
        }
        catch (SocketException ex)
        {
            throw new VncTargetConnectException(Classify(ex), ex);
        }
    }

    /// <summary>Maps a failed connect to the reason shown to the user.</summary>
    internal static VncTargetFailure Classify(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => VncTargetFailure.Refused,
        SocketError.TimedOut => VncTargetFailure.TimedOut,
        _ => VncTargetFailure.Unreachable,
    };

    private static async Task<Stream> ConnectTcpAsync(string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void RaiseTargetConnectFailed(VncTargetFailure failure)
    {
        try
        {
            TargetConnectFailed?.Invoke(failure);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"VNC proxy failure handler threw: {ex.Message}");
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
        WebSocket ws, Stream tcpStream, CancellationToken ct)
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
        Stream tcpStream, WebSocket ws, CancellationToken ct)
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

        _targetStream?.Dispose();
        _cts.Dispose();
    }
}
