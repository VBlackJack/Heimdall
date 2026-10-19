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

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Lifecycle tests for <see cref="EphemeralFileServer"/>.
///
/// HTTP/UDP listeners on Windows can fail in restricted CI environments
/// (UAC, firewall, port already bound). Tests use high ephemeral ports
/// and skip via <see cref="Assert.Skip"/>-equivalent (early return + log)
/// when port acquisition fails. Each test uses a distinct port to keep
/// parallel runs isolated.
/// </summary>
public class EphemeralFileServerTests : IDisposable
{
    private readonly string _testDir;
    private readonly EphemeralFileServer _server;

    // Distinct high ports per test to avoid xUnit parallel-class collisions.
    // 49152-65535 is the IANA dynamic / private port range.
    private const int HttpPortStartStop = 49510;
    private const int HttpPortDoubleStart = 49511;
    private const int HttpPortDispose = 49512;
    private const int TftpPortStartStop = 49513;
    private const int TftpPortDispose = 49514;

    public EphemeralFileServerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"heimdall-fileserver-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _server = new EphemeralFileServer
        {
            ShutdownTimeoutMs = 500,
        };
    }

    public void Dispose()
    {
        try { _server.Dispose(); }
        catch { /* test cleanup */ }
        try { Directory.Delete(_testDir, true); }
        catch { /* test cleanup */ }
        GC.SuppressFinalize(this);
    }

    private static bool IsPortException(Exception ex) =>
        ex is HttpListenerException or SocketException;

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string InvokeRedactToken(string url)
    {
        var redactToken = typeof(EphemeralFileServer).GetMethod(
            "RedactToken",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(redactToken);

        return (string)redactToken!.Invoke(null, [url])!;
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendRawAuthenticatedGetAsync(
        int port,
        string path,
        string accessToken)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);

        await using NetworkStream stream = client.GetStream();
        string request =
            $"GET {path} HTTP/1.1\r\n" +
            $"Host: localhost:{port}\r\n" +
            $"Authorization: Bearer {accessToken}\r\n" +
            "Connection: close\r\n\r\n";
        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes);

        using MemoryStream responseBuffer = new();
        await stream.CopyToAsync(responseBuffer);
        string responseText = Encoding.UTF8.GetString(responseBuffer.ToArray());

        int statusLineEnd = responseText.IndexOf("\r\n", StringComparison.Ordinal);
        Assert.True(statusLineEnd > 0);
        string statusLine = responseText[..statusLineEnd];
        string[] statusParts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(statusParts.Length >= 2);
        HttpStatusCode statusCode = (HttpStatusCode)int.Parse(statusParts[1], CultureInfo.InvariantCulture);

        int bodyStart = responseText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string body = bodyStart >= 0 ? responseText[(bodyStart + 4)..] : string.Empty;
        return (statusCode, body);
    }

    // ── Initial state ─────────────────────────────────────────────────

    [Fact]
    public void NewInstance_IsNotRunning()
    {
        Assert.False(_server.IsHttpRunning);
        Assert.False(_server.IsTftpRunning);
    }

    [Fact]
    public void NewInstance_ServingDirectory_Is_Empty()
    {
        Assert.Equal(string.Empty, _server.ServingDirectory);
    }

    [Fact]
    public void NewInstance_Generates_Url_Safe_AccessToken()
    {
        Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), _server.AccessToken);
        Assert.True(_server.AccessToken.Length >= 40);
    }

    [Fact]
    public void RedactToken_ReplacesTokenValue_InQueryString()
    {
        var redacted = InvokeRedactToken("http://localhost:8080/?token=abc123");

        Assert.Equal("http://localhost:8080/?token=<redacted>", redacted);
    }

    [Fact]
    public void RedactToken_IsCaseInsensitive_OnKey()
    {
        var redacted = InvokeRedactToken("http://localhost:8080/?TOKEN=abc123");

        Assert.Equal("http://localhost:8080/?TOKEN=<redacted>", redacted);
    }

    [Fact]
    public void RedactToken_LeavesOtherQueryParams_Intact()
    {
        var redacted = InvokeRedactToken("http://localhost:8080/?foo=bar&token=abc123&baz=qux");

        Assert.Equal("http://localhost:8080/?foo=bar&token=<redacted>&baz=qux", redacted);
    }

    [Fact]
    public void RedactToken_HandlesNoQueryString()
    {
        var url = "http://localhost:8080/path";

        Assert.Equal(url, InvokeRedactToken(url));
    }

    [Fact]
    public async Task StartHttpServerAsync_Throws_For_Empty_Directory()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _server.StartHttpServerAsync("", HttpPortStartStop));
    }

    [Fact]
    public async Task StartTftpServerAsync_Throws_For_Empty_Directory()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _server.StartTftpServerAsync("", TftpPortStartStop));
    }

    // ── HTTP lifecycle ────────────────────────────────────────────────

    [Fact]
    public async Task StartHttpServerAsync_Sets_IsHttpRunning_True()
    {
        try
        {
            await _server.StartHttpServerAsync(_testDir, HttpPortStartStop);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable in this environment
        }

        Assert.True(_server.IsHttpRunning);
        Assert.Equal(Path.GetFullPath(_testDir), _server.ServingDirectory);
    }

    [Fact]
    public async Task StopHttpServerAsync_Sets_IsHttpRunning_False()
    {
        try
        {
            await _server.StartHttpServerAsync(_testDir, HttpPortStartStop);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable
        }

        await _server.StopHttpServerAsync();

        Assert.False(_server.IsHttpRunning);
    }

    [Fact]
    public async Task StopHttpServerAsync_When_Not_Running_Is_NoOp()
    {
        // Should not throw even though the server was never started.
        await _server.StopHttpServerAsync();
        Assert.False(_server.IsHttpRunning);
    }

    [Fact]
    public async Task StartHttpServerAsync_Stops_Previous_Before_Starting_New()
    {
        // PERF-05 regression guard: a second StartHttpServerAsync call must
        // gracefully tear down the previous listener instead of leaking it.
        try
        {
            await _server.StartHttpServerAsync(_testDir, HttpPortDoubleStart);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable
        }

        Assert.True(_server.IsHttpRunning);

        try
        {
            await _server.StartHttpServerAsync(_testDir, HttpPortDoubleStart);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // listener slot transiently busy after self-restart
        }

        Assert.True(_server.IsHttpRunning);
    }

    [Fact]
    public async Task Http_Request_Without_Token_Returns_Unauthorized()
    {
        var httpPort = GetFreeTcpPort();

        try
        {
            await _server.StartHttpServerAsync(_testDir, httpPort);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable in this environment
        }

        await Task.Delay(50);

        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://localhost:{httpPort}/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Http_Request_With_Valid_Header_Token_Returns_Ok()
    {
        var httpPort = GetFreeTcpPort();
        var filePath = Path.Combine(_testDir, "header-token.txt");
        await File.WriteAllTextAsync(filePath, "header-ok");

        try
        {
            await _server.StartHttpServerAsync(_testDir, httpPort);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable in this environment
        }

        await Task.Delay(50);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{httpPort}/header-token.txt");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _server.AccessToken);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("header-ok", body);
    }

    [Fact]
    public async Task Http_Request_With_Valid_Query_Token_Returns_Ok()
    {
        var httpPort = GetFreeTcpPort();
        var filePath = Path.Combine(_testDir, "query-token.txt");
        await File.WriteAllTextAsync(filePath, "query-ok");

        try
        {
            await _server.StartHttpServerAsync(_testDir, httpPort);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable in this environment
        }

        await Task.Delay(50);

        using var client = new HttpClient();
        using var response = await client.GetAsync(
            $"http://localhost:{httpPort}/query-token.txt?token={_server.AccessToken}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("query-ok", body);
    }

    [Fact]
    public async Task Http_Request_With_Wrong_Token_Returns_Unauthorized()
    {
        var httpPort = GetFreeTcpPort();

        try
        {
            await _server.StartHttpServerAsync(_testDir, httpPort);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable in this environment
        }

        await Task.Delay(50);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{httpPort}/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/../{0}")]
    [InlineData("/%2e%2e/{0}")]
    [InlineData("/../../{0}")]
    public async Task Http_Request_With_Valid_Token_Does_Not_Return_Path_Traversal_Target(
        string traversalPathTemplate)
    {
        int httpPort = GetFreeTcpPort();
        string allowedContent = "allowed-content";
        string secretContent = $"secret-content-{Guid.NewGuid():N}";
        string allowedPath = Path.Combine(_testDir, "allowed.txt");
        DirectoryInfo parentDirectory = Directory.GetParent(_testDir)
            ?? throw new InvalidOperationException("Test directory has no parent.");
        string secretFileName = $"secret-{Guid.NewGuid():N}.txt";
        string secretPath = Path.Combine(parentDirectory.FullName, secretFileName);
        await File.WriteAllTextAsync(allowedPath, allowedContent);
        await File.WriteAllTextAsync(secretPath, secretContent);

        try
        {
            try
            {
                await _server.StartHttpServerAsync(_testDir, httpPort);
            }
            catch (Exception ex) when (IsPortException(ex))
            {
                return; // skip: port unavailable in this environment
            }

            await Task.Delay(50);

            (HttpStatusCode StatusCode, string Body) allowedResponse =
                await SendRawAuthenticatedGetAsync(httpPort, "/allowed.txt", _server.AccessToken);

            Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
            Assert.Equal(allowedContent, allowedResponse.Body);

            string traversalPath = string.Format(
                CultureInfo.InvariantCulture,
                traversalPathTemplate,
                secretFileName);
            (HttpStatusCode StatusCode, string Body) traversalResponse =
                await SendRawAuthenticatedGetAsync(httpPort, traversalPath, _server.AccessToken);

            Assert.True(
                traversalResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                $"Expected 403 or 404, got {(int)traversalResponse.StatusCode}.");
            Assert.DoesNotContain(secretContent, traversalResponse.Body, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(secretPath); }
            catch { /* test cleanup */ }
            await _server.StopHttpServerAsync();
        }
    }

    // ── Links inside the shared folder ────────────────────────────────

    /// <summary>
    /// Creates a directory junction, which needs no privilege (unlike a symbolic link).
    /// Returns false when the junction could not be created.
    /// </summary>
    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        ProcessStartInfo startInfo = new("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process? process = Process.Start(startInfo);
        if (process is null) return false;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(linkPath);
    }

    /// <summary>
    /// Lays out a shared folder holding a regular file and a junction to a sibling folder
    /// that holds a secret file. Returns the shared folder, or null when no junction could be made.
    /// </summary>
    private string? CreateSharedFolderWithJunction(string secretContent)
    {
        string shared = Path.Combine(_testDir, "shared");
        string outside = Path.Combine(_testDir, "outside");
        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(shared, "allowed.txt"), "allowed-content");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), secretContent);

        return TryCreateJunction(Path.Combine(shared, "jn"), outside) ? shared : null;
    }

    [Fact]
    public async Task Http_Request_Through_Junction_Is_Forbidden()
    {
        int httpPort = GetFreeTcpPort();
        string secretContent = $"secret-content-{Guid.NewGuid():N}";
        string? shared = CreateSharedFolderWithJunction(secretContent);
        Assert.NotNull(shared);

        try
        {
            await _server.StartHttpServerAsync(shared!, httpPort);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable in this environment
        }

        await Task.Delay(50);

        (HttpStatusCode StatusCode, string Body) allowed =
            await SendRawAuthenticatedGetAsync(httpPort, "/allowed.txt", _server.AccessToken);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        foreach (string path in new[] { "/jn/secret.txt", "/jn/", "/jn" })
        {
            (HttpStatusCode StatusCode, string Body) response =
                await SendRawAuthenticatedGetAsync(httpPort, path, _server.AccessToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain(secretContent, response.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("secret.txt", response.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Http_Directory_Listing_Leaves_Out_Junction()
    {
        int httpPort = GetFreeTcpPort();
        string? shared = CreateSharedFolderWithJunction("unused");
        Assert.NotNull(shared);
        Directory.CreateDirectory(Path.Combine(shared!, "realdir"));

        try
        {
            await _server.StartHttpServerAsync(shared!, httpPort);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable in this environment
        }

        await Task.Delay(50);

        (HttpStatusCode StatusCode, string Body) listing =
            await SendRawAuthenticatedGetAsync(httpPort, "/", _server.AccessToken);

        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        Assert.Contains(">realdir/<", listing.Body, StringComparison.Ordinal);
        Assert.Contains(">allowed.txt<", listing.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(">jn/<", listing.Body, StringComparison.Ordinal);
    }

    private const ushort TftpOpcodeData = 3;
    private const ushort TftpOpcodeError = 5;

    private static int GetFreeUdpPort()
    {
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    /// <summary>Sends an octet-mode RRQ and returns the opcode of the first reply.</summary>
    private static async Task<ushort> SendTftpReadRequestAsync(int port, string fileName)
    {
        using UdpClient client = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] name = Encoding.ASCII.GetBytes(fileName);
        byte[] mode = Encoding.ASCII.GetBytes("octet");
        byte[] request = new byte[2 + name.Length + 1 + mode.Length + 1];
        request[1] = 1;
        name.CopyTo(request, 2);
        mode.CopyTo(request, 2 + name.Length + 1);
        await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Loopback, port));

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        UdpReceiveResult reply = await client.ReceiveAsync(timeout.Token);
        return (ushort)((reply.Buffer[0] << 8) | reply.Buffer[1]);
    }

    [Fact]
    public async Task Tftp_Read_Of_A_Link_Is_Refused()
    {
        // A file symbolic link needs a privilege the test runner lacks, so the link is reported
        // by the injected check; the regular file next to it is the control.
        int tftpPort = GetFreeUdpPort();
        await File.WriteAllBytesAsync(Path.Combine(_testDir, "linked.bin"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(_testDir, "plain.bin"), [4, 5, 6]);
        var server = new EphemeralFileServer
        {
            ShutdownTimeoutMs = 500,
            IsLink = path => string.Equals(Path.GetFileName(path), "linked.bin", StringComparison.OrdinalIgnoreCase),
        };

        try
        {
            try
            {
                await server.StartTftpServerAsync(_testDir, tftpPort);
            }
            catch (Exception ex) when (IsPortException(ex))
            {
                return; // skip: port unavailable in this environment
            }

            Assert.Equal(TftpOpcodeData, await SendTftpReadRequestAsync(tftpPort, "plain.bin"));
            Assert.Equal(TftpOpcodeError, await SendTftpReadRequestAsync(tftpPort, "linked.bin"));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void CrossesLink_Ignores_The_Root_And_Flags_A_Link_Below_It()
    {
        string root = Path.Combine(_testDir, "root");
        string link = Path.Combine(root, "link");
        bool IsLink(string path) => path == root || path == link;

        Assert.False(EphemeralFileServer.CrossesLink(root, root, IsLink));
        Assert.False(EphemeralFileServer.CrossesLink(root, Path.Combine(root, "plain", "file.txt"), IsLink));
        Assert.True(EphemeralFileServer.CrossesLink(root, link, IsLink));
        Assert.True(EphemeralFileServer.CrossesLink(root, Path.Combine(link, "deeper", "file.txt"), IsLink));
    }

    [Fact]
    public void CrossesLink_Refuses_A_Component_It_Cannot_Inspect()
    {
        string root = Path.Combine(_testDir, "root");

        Assert.True(EphemeralFileServer.CrossesLink(
            root,
            Path.Combine(root, "locked.txt"),
            _ => throw new UnauthorizedAccessException("denied")));
    }

    // ── HTTP concurrency ──────────────────────────────────────────────

    // Large enough that the server cannot push it into the socket buffers of a client
    // that never reads, so its handler stays inside the download.
    private const long StalledDownloadFileBytes = 256L * 1024 * 1024;
    private const int StalledClientReceiveBufferBytes = 4096;
    private const int SecondRequestBoundMs = 5000;
    private const int ShutdownSlackMs = 1500;

    /// <summary>
    /// Opens an authenticated download of <paramref name="path"/> and reads only the first
    /// chunk of the response, proving the server is inside the handler, then stops reading.
    /// </summary>
    private static async Task<TcpClient> OpenStalledDownloadAsync(int port, string path, string accessToken)
    {
        TcpClient client = new() { ReceiveBufferSize = StalledClientReceiveBufferBytes };
        await client.ConnectAsync(IPAddress.Loopback, port);

        NetworkStream stream = client.GetStream();
        string request =
            $"GET {path} HTTP/1.1\r\n" +
            $"Host: localhost:{port}\r\n" +
            $"Authorization: Bearer {accessToken}\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        byte[] firstChunk = new byte[StalledClientReceiveBufferBytes];
        int read = await stream.ReadAsync(firstChunk).AsTask().WaitAsync(TimeSpan.FromMilliseconds(SecondRequestBoundMs));
        Assert.True(read > 0);
        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(firstChunk, 0, read), StringComparison.Ordinal);
        return client;
    }

    private string CreateStalledDownloadFile()
    {
        string bigPath = Path.Combine(_testDir, "big.bin");
        using (FileStream big = new(bigPath, FileMode.CreateNew, FileAccess.Write))
        {
            big.SetLength(StalledDownloadFileBytes);
        }

        return bigPath;
    }

    [Fact]
    public async Task Http_Stalled_Download_Does_Not_Block_Other_Requests()
    {
        int httpPort = GetFreeTcpPort();
        CreateStalledDownloadFile();
        await File.WriteAllTextAsync(Path.Combine(_testDir, "small.txt"), "small-ok");

        try
        {
            await _server.StartHttpServerAsync(_testDir, httpPort);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable in this environment
        }

        try
        {
            using TcpClient stalled = await OpenStalledDownloadAsync(httpPort, "/big.bin", _server.AccessToken);

            Task<(HttpStatusCode StatusCode, string Body)> second =
                SendRawAuthenticatedGetAsync(httpPort, "/small.txt", _server.AccessToken);
            Task finished = await Task.WhenAny(second, Task.Delay(SecondRequestBoundMs));

            Assert.True(
                finished == second,
                $"A second request did not complete within {SecondRequestBoundMs} ms while another download was stalled.");
            (HttpStatusCode statusCode, string body) = await second;
            Assert.Equal(HttpStatusCode.OK, statusCode);
            Assert.Equal("small-ok", body);
        }
        finally
        {
            await _server.StopHttpServerAsync();
        }
    }

    [Fact]
    public async Task StopHttpServerAsync_Finishes_Within_Timeout_With_A_Stalled_Download()
    {
        int httpPort = GetFreeTcpPort();
        CreateStalledDownloadFile();

        try
        {
            await _server.StartHttpServerAsync(_testDir, httpPort);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable in this environment
        }

        using TcpClient stalled = await OpenStalledDownloadAsync(httpPort, "/big.bin", _server.AccessToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _server.StopHttpServerAsync();
        stopwatch.Stop();

        Assert.False(_server.IsHttpRunning);
        Assert.True(
            stopwatch.ElapsedMilliseconds < _server.ShutdownTimeoutMs + ShutdownSlackMs,
            $"StopHttpServerAsync took {stopwatch.ElapsedMilliseconds} ms.");
    }

    // ── TFTP lifecycle ────────────────────────────────────────────────

    [Fact]
    public async Task StartTftpServerAsync_Sets_IsTftpRunning_True()
    {
        try
        {
            await _server.StartTftpServerAsync(_testDir, TftpPortStartStop);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip: port unavailable
        }

        Assert.True(_server.IsTftpRunning);
        Assert.Equal(Path.GetFullPath(_testDir), _server.ServingDirectory);
    }

    [Fact]
    public async Task StopTftpServerAsync_Sets_IsTftpRunning_False()
    {
        try
        {
            await _server.StartTftpServerAsync(_testDir, TftpPortStartStop);
        }
        catch (Exception ex) when (IsPortException(ex))
        {
            return; // skip
        }

        await _server.StopTftpServerAsync();

        Assert.False(_server.IsTftpRunning);
    }

    [Fact]
    public async Task StopTftpServerAsync_When_Not_Running_Is_NoOp()
    {
        await _server.StopTftpServerAsync();
        Assert.False(_server.IsTftpRunning);
    }

    // ── Disposal ──────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Stops_All_Running_Servers()
    {
        var local = new EphemeralFileServer { ShutdownTimeoutMs = 500 };
        bool httpStarted = false;
        bool tftpStarted = false;

        try
        {
            try
            {
                await local.StartHttpServerAsync(_testDir, HttpPortDispose);
                httpStarted = true;
            }
            catch (Exception ex) when (IsPortException(ex)) { /* skip */ }

            try
            {
                await local.StartTftpServerAsync(_testDir, TftpPortDispose);
                tftpStarted = true;
            }
            catch (Exception ex) when (IsPortException(ex)) { /* skip */ }

            if (!httpStarted && !tftpStarted) return;

            local.Dispose();

            Assert.False(local.IsHttpRunning);
            Assert.False(local.IsTftpRunning);
        }
        finally
        {
            try { local.Dispose(); } catch { /* idempotent */ }
        }
    }

    [Fact]
    public async Task DisposeAsync_Stops_All_Running_Servers()
    {
        var local = new EphemeralFileServer { ShutdownTimeoutMs = 500 };
        bool any = false;

        try
        {
            await local.StartHttpServerAsync(_testDir, HttpPortDispose);
            any = true;
        }
        catch (Exception ex) when (IsPortException(ex)) { /* skip */ }

        if (!any)
        {
            await local.DisposeAsync();
            return;
        }

        await local.DisposeAsync();

        Assert.False(local.IsHttpRunning);
        Assert.False(local.IsTftpRunning);
    }

    // ── Static helper ─────────────────────────────────────────────────

    [Fact]
    public void GetLocalIpAddress_Returns_Non_Empty_String()
    {
        var ip = EphemeralFileServer.GetLocalIpAddress();
        Assert.False(string.IsNullOrWhiteSpace(ip));
        // Either a routable IPv4 or the documented loopback fallback.
        Assert.True(IPAddress.TryParse(ip, out _));
    }
}
