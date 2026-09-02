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
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Heimdall.Core.Models;
using Heimdall.Core.Utilities;

namespace Heimdall.App.Services;

/// <summary>
/// Ephemeral HTTP and TFTP file servers for quick file sharing with network devices.
/// Both servers are read-only and run on background tasks while the application is open.
/// HTTP serves files via GET with directory listing and requires a per-instance
/// bearer token on every request. TFTP implements minimal RFC 1350 (RRQ only)
/// with no authentication and serves the directory to any host that can reach
/// the UDP port because the protocol provides no authentication mechanism.
/// </summary>
public sealed class EphemeralFileServer : IDisposable, IAsyncDisposable
{
    private const string OutboundProbeAddress = "8.8.8.8";
    private const int OutboundProbePort = 80;
    private const int TftpBlockSize = 512;
    private const int TftpTimeout = 5000;
    private const int TftpMaxRetries = 3;
    private const int MaxConcurrentTftpTransfers = 8;

    // TFTP opcodes (RFC 1350)
    private const ushort OpcodeRrq = 1;
    private const ushort OpcodeData = 3;
    private const ushort OpcodeAck = 4;
    private const ushort OpcodeError = 5;

    private HttpListener? _httpListener;
    private CancellationTokenSource? _httpCts;
    private Task? _httpTask;

    private UdpClient? _tftpListener;
    private CancellationTokenSource? _tftpCts;
    private Task? _tftpTask;
    private int _activeTftpTransfers;

    private readonly byte[] _accessTokenComparisonBytes;
    private string _servingDirectory = string.Empty;

    /// <summary>Per-instance HTTP access token required for every request.</summary>
    public string AccessToken { get; }

    /// <summary>Whether the HTTP server is currently running.</summary>
    public bool IsHttpRunning { get; private set; }

    /// <summary>Whether the HTTP listener had to fall back to a localhost-only prefix.</summary>
    public bool IsHttpLocalOnly { get; private set; }

    /// <summary>Whether the TFTP server is currently running.</summary>
    public bool IsTftpRunning { get; private set; }

    /// <summary>The directory currently being served.</summary>
    public string ServingDirectory => _servingDirectory;

    /// <summary>Graceful shutdown timeout for HTTP/TFTP tasks.</summary>
    public int ShutdownTimeoutMs { get; set; } = 2000;

    /// <summary>Raised when a file is served (downloaded) by either server.</summary>
    public event Action<string>? FileServed;

    public EphemeralFileServer()
    {
        AccessToken = GenerateAccessToken();
        _accessTokenComparisonBytes = Encoding.UTF8.GetBytes(AccessToken);
    }

    /// <summary>
    /// Starts the HTTP file server on the specified directory and port.
    /// Serves files via GET and provides a simple HTML directory listing at root.
    /// </summary>
    public async Task StartHttpServerAsync(string directory, int port = DefaultPorts.Http)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (IsHttpRunning) await StopHttpServerAsync();

        _servingDirectory = Path.GetFullPath(directory);
        _httpCts = new CancellationTokenSource();
        _httpListener = new HttpListener();
        // Intentional wildcard bind: tightening to a specific IP (for example the detected LAN address)
        // would require a Windows URL ACL (`netsh http add urlacl`) or admin elevation, breaking the
        // File Share toggle UX for non-admin users. The bearer token enforced on every request is the
        // actual security boundary here.
        _httpListener.Prefixes.Add($"http://+:{port}/");
        IsHttpLocalOnly = false;

        try
        {
            _httpListener.Start();
            Core.Logging.FileLogger.Info(
                $"HTTP file server listening on ALL interfaces (port {port}) with bearer token authentication.");
        }
        catch (HttpListenerException)
        {
            // Fallback to localhost-only if elevated prefix registration fails
            _httpListener.Close();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://localhost:{port}/");
            _httpListener.Start();
            IsHttpLocalOnly = true;
            Core.Logging.FileLogger.Warn(
                $"HTTP file server fell back to localhost-only prefix on port {port}.");
        }

        IsHttpRunning = true;
        Core.Logging.FileLogger.Info($"HTTP file server started on port {port}, serving: {_servingDirectory}");

        var token = _httpCts.Token;
        _httpTask = Task.Run(() => HttpListenLoop(token), token);
    }

    /// <summary>
    /// Builds a tokenized URL for the served HTTP endpoint using the provided base URL.
    /// </summary>
    public string BuildUrl(string baseUrl, string relativePath = "/")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var normalizedPath = string.IsNullOrWhiteSpace(relativePath) || relativePath == "/"
            ? "/"
            : "/" + relativePath.TrimStart('/');

        return AppendTokenToUrl($"{normalizedBaseUrl}{normalizedPath}");
    }

    /// <summary>Stops the HTTP file server.</summary>
    public async Task StopHttpServerAsync()
    {
        if (!IsHttpRunning) return;

        _httpCts?.Cancel();
        try { _httpListener?.Stop(); } catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EphemeralFileServer] HTTP stop: {ex.Message}"); }
        try { _httpListener?.Close(); } catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EphemeralFileServer] HTTP close: {ex.Message}"); }

        if (_httpTask is not null)
        {
            try { await _httpTask.WaitAsync(TimeSpan.FromMilliseconds(ShutdownTimeoutMs)); }
            catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EphemeralFileServer] HTTP task wait: {ex.Message}"); }
        }

        _httpListener = null;
        _httpCts?.Dispose();
        _httpCts = null;
        _httpTask = null;
        IsHttpRunning = false;
        IsHttpLocalOnly = false;

        Core.Logging.FileLogger.Info("HTTP file server stopped");
    }

    /// <summary>
    /// Starts the TFTP server on the specified directory and port.
    /// Implements minimal TFTP (RFC 1350): read requests only, octet mode, 512-byte blocks.
    /// The TFTP server has no authentication and serves the directory to any host that
    /// can reach the UDP port, unlike the HTTP server which requires the bearer token.
    /// </summary>
    public async Task StartTftpServerAsync(string directory, int port = DefaultPorts.Tftp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (IsTftpRunning) await StopTftpServerAsync();

        _servingDirectory = Path.GetFullPath(directory);
        _tftpCts = new CancellationTokenSource();
        _tftpListener = new UdpClient(port);

        IsTftpRunning = true;
        Core.Logging.FileLogger.Info($"TFTP file server started on port {port}, serving: {_servingDirectory}");

        var token = _tftpCts.Token;
        _tftpTask = Task.Run(() => TftpListenLoop(token), token);
    }

    /// <summary>Stops the TFTP file server.</summary>
    public async Task StopTftpServerAsync()
    {
        if (!IsTftpRunning) return;

        _tftpCts?.Cancel();
        try { _tftpListener?.Close(); } catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EphemeralFileServer] TFTP close: {ex.Message}"); }

        if (_tftpTask is not null)
        {
            try { await _tftpTask.WaitAsync(TimeSpan.FromMilliseconds(ShutdownTimeoutMs)); }
            catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EphemeralFileServer] TFTP task wait: {ex.Message}"); }
        }

        _tftpListener = null;
        _tftpCts?.Dispose();
        _tftpCts = null;
        _tftpTask = null;
        IsTftpRunning = false;

        Core.Logging.FileLogger.Info("TFTP file server stopped");
    }

    /// <summary>Stops both servers asynchronously and releases all resources.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopHttpServerAsync();
        await StopTftpServerAsync();
    }

    /// <summary>Stops both servers and releases all resources (synchronous fallback).</summary>
    public void Dispose()
    {
        // Cancellation tokens ensure tasks finish quickly after cancel
        _httpCts?.Cancel();
        _tftpCts?.Cancel();
        try { _httpListener?.Stop(); }
        catch (Exception ex) { Core.Logging.FileLogger.Warn("[EphemeralFileServer] HTTP stop: " + ex.Message); }
        try { _httpListener?.Close(); }
        catch (Exception ex) { Core.Logging.FileLogger.Warn("[EphemeralFileServer] HTTP close: " + ex.Message); }
        try { _tftpListener?.Close(); }
        catch (Exception ex) { Core.Logging.FileLogger.Warn("[EphemeralFileServer] TFTP close: " + ex.Message); }
        _httpListener = null;
        _tftpListener = null;
        _httpCts?.Dispose();
        _tftpCts?.Dispose();
        _httpCts = null;
        _tftpCts = null;
        IsHttpRunning = false;
        IsHttpLocalOnly = false;
        IsTftpRunning = false;
    }

    /// <summary>
    /// Returns the first non-loopback IPv4 address of this machine,
    /// or "127.0.0.1" if none is found.
    /// </summary>
    public static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Connect to a public address to determine the preferred outbound IP (no data is sent)
            socket.Connect(OutboundProbeAddress, OutboundProbePort);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
                return endPoint.Address.ToString();
        }
        catch (Exception ex) { Core.Logging.FileLogger.Warn($"[EphemeralFileServer] local IP detection: {ex.Message}"); }

        return "127.0.0.1";
    }

    // ── HTTP server loop ──────────────────────────────────────────────

    private async Task HttpListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _httpListener is { IsListening: true })
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _httpListener.GetContextAsync().WaitAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }

            if (context is null) continue;

            try
            {
                await HandleHttpRequest(context);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Error($"HTTP request error: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch (Exception closeEx) { Core.Logging.FileLogger.Warn($"[EphemeralFileServer] HTTP error response close: {closeEx.Message}"); }
            }
        }
    }

    private async Task HandleHttpRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (!IsAuthorized(request))
        {
            Core.Logging.FileLogger.Warn(
                $"[EphemeralFileServer] Unauthorized HTTP request: {RedactToken(request.Url?.ToString() ?? request.RawUrl ?? "/")}");

            response.StatusCode = 401;
            var unauthorized = Encoding.UTF8.GetBytes("Unauthorized");
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = unauthorized.Length;
            await response.OutputStream.WriteAsync(unauthorized);
            response.Close();
            return;
        }

        if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 405;
            response.Close();
            return;
        }

        var requestPath = HttpUtility.UrlDecode(request.Url?.AbsolutePath ?? "/");
        // Normalize and prevent directory traversal
        requestPath = requestPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_servingDirectory, requestPath));

        // Ensure trailing separator to prevent sibling-prefix bypass (e.g. /data vs /data-other)
        var safeBase = _servingDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _servingDirectory
            : _servingDirectory + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(safeBase, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, _servingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 403;
            response.Close();
            return;
        }

        if (Directory.Exists(fullPath))
        {
            await ServeDirectoryListing(response, fullPath, requestPath);
        }
        else if (File.Exists(fullPath))
        {
            await ServeFile(response, fullPath);
            FileServed?.Invoke($"HTTP: {Path.GetFileName(fullPath)}");
        }
        else
        {
            response.StatusCode = 404;
            var notFound = Encoding.UTF8.GetBytes("<html><body><h1>404 - Not Found</h1></body></html>");
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = notFound.Length;
            await response.OutputStream.WriteAsync(notFound);
            response.Close();
        }
    }

    private async Task ServeDirectoryListing(HttpListenerResponse response, string dirPath, string relativePath)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.Append("<title>Heimdall File Server</title>");
        sb.Append("<style>body{font-family:monospace;margin:20px}a{text-decoration:none;color:#0066cc}");
        sb.Append("a:hover{text-decoration:underline}table{border-collapse:collapse}");
        sb.Append("td{padding:4px 16px 4px 0}</style></head><body>");
        sb.Append($"<h2>Index of /{HttpUtility.HtmlEncode(relativePath)}</h2><hr/><table>");

        // Parent directory link
        if (!string.IsNullOrEmpty(relativePath))
        {
            var parent = Path.GetDirectoryName(relativePath.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
            sb.Append($"<tr><td><a href=\"{AppendTokenToRelativeUrl($"/{parent}")}\">..</a></td><td></td><td></td></tr>");
        }

        // Directories
        foreach (var dir in Directory.GetDirectories(dirPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            var href = string.IsNullOrEmpty(relativePath)
                ? $"/{HttpUtility.UrlEncode(name)}"
                : $"/{HttpUtility.UrlEncode(relativePath.Replace(Path.DirectorySeparatorChar, '/'))}/{HttpUtility.UrlEncode(name)}";
            sb.Append($"<tr><td><a href=\"{AppendTokenToRelativeUrl(href)}\">{HttpUtility.HtmlEncode(name)}/</a></td><td>-</td><td></td></tr>");
        }

        // Files
        foreach (var file in Directory.GetFiles(dirPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            var info = new FileInfo(file);
            var href = string.IsNullOrEmpty(relativePath)
                ? $"/{HttpUtility.UrlEncode(name)}"
                : $"/{HttpUtility.UrlEncode(relativePath.Replace(Path.DirectorySeparatorChar, '/'))}/{HttpUtility.UrlEncode(name)}";
            sb.Append($"<tr><td><a href=\"{AppendTokenToRelativeUrl(href)}\">{HttpUtility.HtmlEncode(name)}</a></td>");
            sb.Append($"<td>{FormatFileSize(info.Length)}</td>");
            sb.Append($"<td>{info.LastWriteTime:yyyy-MM-dd HH:mm}</td></tr>");
        }

        sb.Append("</table><hr/><em>Heimdall Ephemeral File Server</em></body></html>");

        var html = Encoding.UTF8.GetBytes(sb.ToString());
        response.ContentType = "text/html; charset=utf-8";
        response.AddHeader("Cache-Control", "no-store");
        response.ContentLength64 = html.Length;
        await response.OutputStream.WriteAsync(html);
        response.Close();
    }

    private static async Task ServeFile(HttpListenerResponse response, string filePath)
    {
        response.ContentType = GetMimeType(filePath);
        response.AddHeader("Cache-Control", "no-store");
        FileInfo fileInfo = new(filePath);
        response.ContentLength64 = fileInfo.Length;
        string sanitizedFileName = (Path.GetFileName(filePath) ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\"", string.Empty, StringComparison.Ordinal);
        response.AddHeader("Content-Disposition", $"inline; filename=\"{sanitizedFileName}\"");

        await using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await fileStream.CopyToAsync(response.OutputStream);
        response.Close();
    }

    // ── TFTP server loop (RFC 1350, read-only, octet mode) ────────────

    private async Task TftpListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _tftpListener!.ReceiveAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            byte[] data = result.Buffer;
            if (data.Length < 4) continue;

            ushort opcode = (ushort)((data[0] << 8) | data[1]);

            if (opcode == OpcodeRrq)
            {
                IPEndPoint clientEndpoint = result.RemoteEndPoint;
                if (Interlocked.Increment(ref _activeTftpTransfers) > MaxConcurrentTftpTransfers)
                {
                    Interlocked.Decrement(ref _activeTftpTransfers);
                    Core.Logging.FileLogger.Warn(
                        $"[EphemeralFileServer] TFTP transfer rejected (concurrency limit reached): {clientEndpoint}");
                    continue;
                }

                // Fire and forget the transfer on a separate UDP socket (per RFC 1350, each transfer uses a new TID)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleTftpReadRequest(data, clientEndpoint, token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeTftpTransfers);
                    }
                }, token);
            }
            // Ignore WRQ (opcode 2) and anything else for security - read-only server
        }
    }

    private async Task HandleTftpReadRequest(byte[] data, IPEndPoint clientEndpoint, CancellationToken token)
    {
        try
        {
            // Parse RRQ: opcode (2 bytes) | filename (null-terminated) | mode (null-terminated)
            string filename = ReadNullTerminatedString(data, 2, out int nextOffset);
            string mode = ReadNullTerminatedString(data, nextOffset, out _);

            if (!string.Equals(mode, "octet", StringComparison.OrdinalIgnoreCase))
            {
                // Only octet (binary) mode is supported
                using UdpClient errorClient = new();
                await SendTftpError(errorClient, clientEndpoint, 0, "Only octet mode is supported");
                return;
            }

            // Sanitize filename: prevent directory traversal
            string safeName = Path.GetFileName(filename) ?? string.Empty;
            string filePath = Path.GetFullPath(Path.Combine(_servingDirectory, safeName));

            // Ensure trailing separator to prevent sibling-prefix bypass (same as HTTP handler)
            string tftpSafeBase = _servingDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? _servingDirectory
                : _servingDirectory + Path.DirectorySeparatorChar;

            if ((!filePath.StartsWith(tftpSafeBase, StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(filePath, _servingDirectory, StringComparison.OrdinalIgnoreCase))
                || !File.Exists(filePath))
            {
                using UdpClient errorClient = new();
                await SendTftpError(errorClient, clientEndpoint, 1, "File not found");
                return;
            }

            Core.Logging.FileLogger.Info($"TFTP RRQ from {clientEndpoint}: {safeName}");

            // Transfer the file using a new UDP socket (unique transfer ID per RFC 1350)
            using UdpClient transferClient = new();
            try
            {
                await using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] buffer = new byte[TftpBlockSize];
                ushort blockNumber = 1;

                while (!token.IsCancellationRequested)
                {
                    int bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, TftpBlockSize), token);

                    // Build DATA packet: opcode (2) | block# (2) | data (0-512)
                    byte[] dataPacket = new byte[4 + bytesRead];
                    dataPacket[0] = 0;
                    dataPacket[1] = (byte)OpcodeData;
                    dataPacket[2] = (byte)(blockNumber >> 8);
                    dataPacket[3] = (byte)(blockNumber & 0xFF);
                    Array.Copy(buffer, 0, dataPacket, 4, bytesRead);

                    // Send DATA and wait for ACK with retries
                    bool acked = false;
                    for (int retry = 0; retry < TftpMaxRetries && !token.IsCancellationRequested; retry++)
                    {
                        await transferClient.SendAsync(dataPacket, dataPacket.Length, clientEndpoint);

                        try
                        {
                            using CancellationTokenSource timeoutCts =
                                CancellationTokenSource.CreateLinkedTokenSource(token);
                            timeoutCts.CancelAfter(TftpTimeout);
                            UdpReceiveResult ackResult = await transferClient.ReceiveAsync(timeoutCts.Token);
                            byte[] ack = ackResult.Buffer;

                            if (ack.Length >= 4)
                            {
                                ushort ackOpcode = (ushort)((ack[0] << 8) | ack[1]);
                                ushort ackBlock = (ushort)((ack[2] << 8) | ack[3]);

                                if (ackOpcode == OpcodeAck && ackBlock == blockNumber)
                                {
                                    // Update client endpoint to the actual TID used by client
                                    clientEndpoint = ackResult.RemoteEndPoint;
                                    acked = true;
                                    break;
                                }
                            }
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            // Timeout - retry
                        }
                    }

                    if (!acked)
                    {
                        Core.Logging.FileLogger.Warn($"TFTP transfer timeout for {safeName} at block {blockNumber}");
                        return;
                    }

                    // Last block: data < 512 bytes signals end of transfer
                    if (bytesRead < TftpBlockSize)
                    {
                        FileServed?.Invoke($"TFTP: {safeName}");
                        Core.Logging.FileLogger.Info($"TFTP transfer complete: {safeName}");
                        return;
                    }

                    blockNumber++;
                }
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Error($"TFTP transfer error for {safeName}: {ex.Message}");
                try
                {
                    await SendTftpError(transferClient, clientEndpoint, 0, "Internal server error");
                }
                catch (Exception sendEx)
                {
                    Core.Logging.FileLogger.Warn(
                        $"[EphemeralFileServer] TFTP error send: {sendEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                "[EphemeralFileServer] TFTP read request handling failed: " + ex.Message);
        }
    }

    private static async Task SendTftpError(UdpClient client, IPEndPoint endpoint, ushort errorCode, string message)
    {
        var msgBytes = Encoding.ASCII.GetBytes(message);
        var packet = new byte[5 + msgBytes.Length];
        packet[0] = 0;
        packet[1] = (byte)OpcodeError;
        packet[2] = (byte)(errorCode >> 8);
        packet[3] = (byte)(errorCode & 0xFF);
        Array.Copy(msgBytes, 0, packet, 4, msgBytes.Length);
        packet[^1] = 0; // Null terminator
        await client.SendAsync(packet, packet.Length, endpoint);
    }

    private static string ReadNullTerminatedString(byte[] data, int offset, out int nextOffset)
    {
        if (offset >= data.Length)
        {
            nextOffset = data.Length;
            return string.Empty;
        }

        int end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0) end = data.Length;
        nextOffset = end + 1;
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static string GenerateAccessToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var candidateToken = TryGetBearerToken(request.Headers["Authorization"])
            ?? request.QueryString["token"];

        return FixedTimeEqualsAccessToken(candidateToken);
    }

    private bool FixedTimeEqualsAccessToken(string? candidateToken)
    {
        var candidateBytes = string.IsNullOrEmpty(candidateToken)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(candidateToken);

        if (candidateBytes.Length != _accessTokenComparisonBytes.Length)
        {
            CryptographicOperations.FixedTimeEquals(
                _accessTokenComparisonBytes,
                new byte[_accessTokenComparisonBytes.Length]);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(_accessTokenComparisonBytes, candidateBytes);
    }

    private static string? TryGetBearerToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        const string bearerPrefix = "Bearer ";
        return authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[bearerPrefix.Length..].Trim()
            : null;
    }

    private string AppendTokenToRelativeUrl(string relativeUrl)
    {
        return AppendTokenToUrl(relativeUrl);
    }

    /// <summary>
    /// Appends the per-instance token to browser-clickable URLs. Browser navigation cannot
    /// send an Authorization header, so URL tokens trade convenience for possible exposure
    /// through browser history, Referer headers, and proxy logs. The generated curl helper
    /// uses the Authorization: Bearer header instead, and a fresh server instance regenerates
    /// the token for each share to bound exposure.
    /// </summary>
    private string AppendTokenToUrl(string url)
    {
        string separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{separator}token={AccessToken}";
    }

    private static string RedactToken(string url)
    {
        return Regex.Replace(url, @"(?i)([?&]token=)[^&]*", "$1<redacted>");
    }

    private static string FormatFileSize(long bytes) => FileSize.Format(bytes);

    private static string GetMimeType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".txt" or ".log" or ".cfg" or ".conf" => "text/plain",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".gz" or ".tgz" => "application/gzip",
            ".tar" => "application/x-tar",
            ".bin" or ".img" or ".fw" => "application/octet-stream",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };
    }
}
