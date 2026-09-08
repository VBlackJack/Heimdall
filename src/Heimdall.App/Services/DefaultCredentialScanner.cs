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
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Discovery;
using Heimdall.Core.Security;
using Renci.SshNet;

namespace Heimdall.App.Services;

/// <summary>
/// Abstraction for default credential testing.
/// </summary>
public interface ICredentialScanner
{
    /// <summary>
    /// Probes whether a port is open on the target host.
    /// </summary>
    Task<bool> ProbePortAsync(string host, int port, CancellationToken ct);

    /// <summary>
    /// Tests a single credential pair against a service.
    /// </summary>
    Task<CredTestResultDto> TestCredentialAsync(
        string host,
        int port,
        string service,
        string user,
        string pass,
        CancellationToken ct);

    /// <summary>
    /// Cleans up any shared resources.
    /// </summary>
    void Cleanup();
}

/// <summary>
/// Network scanner for default-credential tests.
/// </summary>
public sealed class DefaultCredentialScanner : ICredentialScanner
{
    private readonly SshGatewayDto? _gateway;
    private SshClient? _tunnelClient;
    private SemaphoreSlim? _commandLock;

    public DefaultCredentialScanner(SshGatewayDto? gateway = null)
    {
        _gateway = gateway;
        if (gateway is not null)
        {
            _commandLock = new SemaphoreSlim(1, 1);
        }
    }

    public async Task<bool> ProbePortAsync(string host, int port, CancellationToken ct)
    {
        if (_gateway is not null)
        {
            await EnsureTunnelAsync(ct).ConfigureAwait(false);
            var safeHost = InputValidator.EscapeShellArg(host);
            var commandText =
                $"timeout 2 bash -c \"echo >/dev/tcp/{safeHost}/{port}\" 2>/dev/null && echo OPEN || echo CLOSED";
            var result = await ExecuteTunnelCommandAsync(commandText, 5000, ct).ConfigureAwait(false);
            return string.Equals(result.Trim(), "OPEN", StringComparison.OrdinalIgnoreCase);
        }

        return await ProbePortDirectAsync(host, port, ct).ConfigureAwait(false);
    }

    public async Task<CredTestResultDto> TestCredentialAsync(
        string host,
        int port,
        string service,
        string user,
        string pass,
        CancellationToken ct)
    {
        try
        {
            if (_gateway is not null)
            {
                await EnsureTunnelAsync(ct).ConfigureAwait(false);
            }

            var accepted = service switch
            {
                "SSH" => await TestSshAsync(host, port, user, pass, ct).ConfigureAwait(false),
                "Telnet" => await TestTelnetAsync(host, port, user, pass, ct).ConfigureAwait(false),
                "HTTP" => await TestHttpBasicAsync(host, port, user, pass, ct).ConfigureAwait(false),
                "FTP" => await TestFtpAsync(host, port, user, pass, ct).ConfigureAwait(false),
                "SNMP" => await TestSnmpCommunityAsync(host, user, ct).ConfigureAwait(false),
                "MySQL" => await TestMySqlAsync(host, port, user, pass, ct).ConfigureAwait(false),
                "PostgreSQL" => await TestPostgreSqlAsync(host, port, user, pass, ct).ConfigureAwait(false),
                "MSSQL" => await TestMsSqlAsync(host, port, user, pass, ct).ConfigureAwait(false),
                "Redis" => await TestRedisAsync(host, port, ct).ConfigureAwait(false),
                "VNC" => await TestVncAsync(host, port, pass, ct).ConfigureAwait(false),
                _ => false,
            };

            return new CredTestResultDto
            {
                Service = service,
                Port = port,
                Username = user,
                Password = pass,
                Status = accepted ? CredTestStatus.Default : CredTestStatus.Changed,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CredTestResultDto
            {
                Service = service,
                Port = port,
                Username = user,
                Password = pass,
                Status = CredTestStatus.Error,
                ErrorDetail = ex.Message,
            };
        }
    }

    public void Cleanup()
    {
        if (_tunnelClient is not null)
        {
            try
            {
                _tunnelClient.Disconnect();
            }
            catch
            {
                // Best effort cleanup.
            }

            _tunnelClient.Dispose();
            _tunnelClient = null;
        }

        _commandLock?.Dispose();
        _commandLock = null;
    }

    private async Task EnsureTunnelAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _commandLock!.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _tunnelClient ??= await ToolGatewayConnector.ConnectAsync(_gateway!, ct).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<string> ExecuteTunnelCommandAsync(
        string commandText,
        int timeoutMs,
        CancellationToken ct)
    {
        if (_tunnelClient is null || _commandLock is null)
        {
            throw new InvalidOperationException("Tunnel command requested without a tunnel.");
        }

        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var cmd = _tunnelClient.CreateCommand(commandText);
                cmd.CommandTimeout = TimeSpan.FromMilliseconds(timeoutMs);
                cmd.Execute();
                return cmd.Result ?? string.Empty;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private static async Task<bool> ProbePortDirectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultCredentialEngine.PortProbeTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests SSH credentials using SSH.NET password authentication.
    /// </summary>
    private static async Task<bool> TestSshAsync(
        string host,
        int port,
        string user,
        string pass,
        CancellationToken ct)
    {
        try
        {
            var connInfo = new PasswordConnectionInfo(host, port, user, pass)
            {
                Timeout = TimeSpan.FromSeconds(5),
            };

            using var client = new SshClient(connInfo);
            await Task.Run(client.Connect, ct).ConfigureAwait(false);
            client.Disconnect();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests HTTP Basic Authentication by sending a request with an Authorization header.
    /// </summary>
    private async Task<bool> TestHttpBasicAsync(
        string host,
        int port,
        string user,
        string pass,
        CancellationToken ct)
    {
        try
        {
            var useTls = port is 443 or 8443;

            if (_tunnelClient is not null)
            {
                var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{pass}"));
                var hostInEcho = InputValidator.EscapeForDoubleQuotedString(host);
                var hostArg = InputValidator.EscapeShellArg(host);
                var httpCmd =
                    $"echo -e \"GET / HTTP/1.0\\r\\nHost: {hostInEcho}\\r\\nAuthorization: Basic {credentials}\\r\\n\\r\\n\" | nc -w 5 {hostArg} {port} 2>/dev/null | head -1";
                var result = await ExecuteTunnelCommandAsync(httpCmd, DefaultCredentialEngine.ConnectTimeoutMs, ct)
                    .ConfigureAwait(false);
                return DefaultCredentialEngine.IsHttpSuccessResponse(result.Trim());
            }

            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultCredentialEngine.ConnectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);

            Stream stream = tcp.GetStream();
            SslStream? ssl = null;

            try
            {
                if (useTls)
                {
                    ssl = new SslStream(stream, leaveInnerStreamOpen: true, (_, _, _, _) => true);
                    await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = host,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        },
                        linked.Token).ConfigureAwait(false);
                    stream = ssl;
                }

                var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{pass}"));
                var request =
                    $"GET / HTTP/1.0\r\nHost: {host}\r\nAuthorization: Basic {credentials}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request), linked.Token).ConfigureAwait(false);

                var buffer = new byte[512];
                var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                var response = Encoding.ASCII.GetString(buffer, 0, read);
                return DefaultCredentialEngine.IsHttpSuccessResponse(response);
            }
            finally
            {
                if (ssl is not null)
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests SNMP community strings using the UDP probe engine.
    /// </summary>
    private static async Task<bool> TestSnmpCommunityAsync(string host, string community, CancellationToken ct)
    {
        try
        {
            var info = await UdpProbeEngine.QuerySnmpAsync(
                host,
                community,
                DefaultCredentialEngine.ConnectTimeoutMs,
                ct).ConfigureAwait(false);
            return info is not null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests FTP credentials using USER/PASS commands.
    /// </summary>
    private async Task<bool> TestFtpAsync(
        string host,
        int port,
        string user,
        string pass,
        CancellationToken ct)
    {
        try
        {
            if (_tunnelClient is not null)
            {
                var ftpScript =
                    $"(echo -e \"USER {InputValidator.EscapeForDoubleQuotedString(user)}\\r\\nPASS {InputValidator.EscapeForDoubleQuotedString(pass)}\\r\\nQUIT\\r\\n\" | nc -w 5 {InputValidator.EscapeShellArg(host)} {port} 2>/dev/null)";
                var result = await ExecuteTunnelCommandAsync(ftpScript, DefaultCredentialEngine.ConnectTimeoutMs, ct)
                    .ConfigureAwait(false);
                return result.Contains("230", StringComparison.Ordinal);
            }

            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultCredentialEngine.ConnectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var buffer = new byte[512];
            _ = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);

            await stream.WriteAsync(Encoding.ASCII.GetBytes($"USER {user}\r\n"), linked.Token).ConfigureAwait(false);
            _ = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);

            await stream.WriteAsync(Encoding.ASCII.GetBytes($"PASS {pass}\r\n"), linked.Token).ConfigureAwait(false);
            var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            var response = Encoding.ASCII.GetString(buffer, 0, read);

            return response.Contains("230", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests Telnet credentials by sending username and password after the initial prompt.
    /// </summary>
    private async Task<bool> TestTelnetAsync(
        string host,
        int port,
        string user,
        string pass,
        CancellationToken ct)
    {
        try
        {
            if (_tunnelClient is not null)
            {
                var telnetScript =
                    $"(echo -e \"{InputValidator.EscapeForDoubleQuotedString(user)}\\n{InputValidator.EscapeForDoubleQuotedString(pass)}\\n\" | nc -w 5 {InputValidator.EscapeShellArg(host)} {port} 2>/dev/null) | tail -1";
                var result = await ExecuteTunnelCommandAsync(
                    telnetScript,
                    DefaultCredentialEngine.ConnectTimeoutMs,
                    ct).ConfigureAwait(false);

                return !result.Contains("incorrect", StringComparison.OrdinalIgnoreCase)
                    && !result.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    && !result.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    && result.Length > 0;
            }

            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultCredentialEngine.ConnectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var buffer = new byte[1024];

            _ = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            await Task.Delay(500, linked.Token).ConfigureAwait(false);
            if (stream.DataAvailable)
            {
                _ = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{user}\r\n"), linked.Token).ConfigureAwait(false);
            await Task.Delay(500, linked.Token).ConfigureAwait(false);
            _ = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);

            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{pass}\r\n"), linked.Token).ConfigureAwait(false);
            await Task.Delay(500, linked.Token).ConfigureAwait(false);
            var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            var response = Encoding.ASCII.GetString(buffer, 0, read);

            return !response.Contains("incorrect", StringComparison.OrdinalIgnoreCase)
                && !response.Contains("failed", StringComparison.OrdinalIgnoreCase)
                && !response.Contains("denied", StringComparison.OrdinalIgnoreCase)
                && (response.Contains('$') || response.Contains('#') || response.Contains('>'));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests MySQL credentials using a basic connection heuristic.
    /// </summary>
    private async Task<bool> TestMySqlAsync(
        string host,
        int port,
        string user,
        string pass,
        CancellationToken ct)
    {
        try
        {
            if (_tunnelClient is not null)
            {
                var mysqlCmd =
                    $"mysql -h {InputValidator.EscapeShellArg(host)} -P {port} -u {InputValidator.EscapeShellArg(user)} --password={InputValidator.EscapeShellArg(pass)} -e \"SELECT 1\" 2>&1 | head -1";
                var result = await ExecuteTunnelCommandAsync(
                    mysqlCmd,
                    DefaultCredentialEngine.ConnectTimeoutMs,
                    ct).ConfigureAwait(false);

                return !result.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                    && !result.Contains("denied", StringComparison.OrdinalIgnoreCase);
            }

            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultCredentialEngine.ConnectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            return read > 4 && buffer[4] == 0x0a;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests PostgreSQL credentials by sending a startup message.
    /// </summary>
    private async Task<bool> TestPostgreSqlAsync(
        string host,
        int port,
        string user,
        string pass,
        CancellationToken ct)
    {
        try
        {
            if (_tunnelClient is not null)
            {
                var pgCmd =
                    $"PGPASSWORD={InputValidator.EscapeShellArg(pass)} psql -h {InputValidator.EscapeShellArg(host)} -p {port} -U {InputValidator.EscapeShellArg(user)} -c \"SELECT 1\" 2>&1 | head -1";
                var result = await ExecuteTunnelCommandAsync(
                    pgCmd,
                    DefaultCredentialEngine.ConnectTimeoutMs,
                    ct).ConfigureAwait(false);

                return !result.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
                    && !result.Contains("error", StringComparison.OrdinalIgnoreCase)
                    && result.Length > 0;
            }

            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultCredentialEngine.ConnectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var startupParams = $"user\0{user}\0database\0{user}\0\0";
            var paramBytes = Encoding.ASCII.GetBytes(startupParams);
            var msgLen = 4 + 4 + paramBytes.Length;
            var msg = new byte[msgLen];
            BitConverter.TryWriteBytes(msg.AsSpan(0, 4), System.Net.IPAddress.HostToNetworkOrder(msgLen));
            msg[4] = 0;
            msg[5] = 3;
            msg[6] = 0;
            msg[7] = 0;
            Array.Copy(paramBytes, 0, msg, 8, paramBytes.Length);

            await stream.WriteAsync(msg, linked.Token).ConfigureAwait(false);
            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);

            if (read > 0 && buffer[0] == (byte)'R' && read >= 9)
            {
                var authType = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buffer, 5));
                return authType == 0;
            }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests MSSQL credentials.
    /// </summary>
    private async Task<bool> TestMsSqlAsync(
        string host,
        int port,
        string user,
        string pass,
        CancellationToken ct)
    {
        try
        {
            if (_tunnelClient is not null)
            {
                var sqlCmd =
                    $"sqlcmd -S {InputValidator.EscapeShellArg(host + "," + port)} -U {InputValidator.EscapeShellArg(user)} -P {InputValidator.EscapeShellArg(pass)} -Q \"SELECT 1\" -t 5 2>&1 | head -1";
                var result = await ExecuteTunnelCommandAsync(
                    sqlCmd,
                    DefaultCredentialEngine.ConnectTimeoutMs,
                    ct).ConfigureAwait(false);

                return !result.Contains("Error", StringComparison.OrdinalIgnoreCase)
                    && !result.Contains("Login failed", StringComparison.OrdinalIgnoreCase)
                    && result.Length > 0;
            }

            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultCredentialEngine.ConnectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests Redis connectivity.
    /// </summary>
    private async Task<bool> TestRedisAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            if (_tunnelClient is not null)
            {
                var redisCmd =
                    $"echo PING | nc -w 3 {InputValidator.EscapeShellArg(host)} {port} 2>/dev/null";
                var result = await ExecuteTunnelCommandAsync(
                    redisCmd,
                    DefaultCredentialEngine.ConnectTimeoutMs,
                    ct).ConfigureAwait(false);
                return result.Contains("+PONG", StringComparison.OrdinalIgnoreCase);
            }

            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultCredentialEngine.ConnectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            await stream.WriteAsync(Encoding.ASCII.GetBytes("PING\r\n"), linked.Token).ConfigureAwait(false);
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            var response = Encoding.ASCII.GetString(buffer, 0, read);

            return response.Contains("+PONG", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests VNC connectivity and no-auth mode.
    /// </summary>
    private async Task<bool> TestVncAsync(string host, int port, string pass, CancellationToken ct)
    {
        _ = pass;

        try
        {
            if (_tunnelClient is not null)
            {
                var safeVncHost = InputValidator.EscapeShellArg(host);
                var vncCheck =
                    $"timeout 2 bash -c \"echo >/dev/tcp/{safeVncHost}/{port}\" 2>/dev/null && echo OPEN || echo CLOSED";
                _ = await ExecuteTunnelCommandAsync(vncCheck, 5000, ct).ConfigureAwait(false);
                return false;
            }

            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(DefaultCredentialEngine.ConnectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            var version = Encoding.ASCII.GetString(buffer, 0, read);
            if (!version.StartsWith("RFB", StringComparison.Ordinal))
            {
                return false;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
            read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            if (read > 0)
            {
                var numTypes = buffer[0];
                for (var i = 1; i <= numTypes && i < read; i++)
                {
                    if (buffer[i] == 1)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
