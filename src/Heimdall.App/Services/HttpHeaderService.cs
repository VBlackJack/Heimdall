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
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Stateless HTTP header fetch service contract.
/// </summary>
public interface IHttpHeaderService
{
    /// <summary>
    /// Sets the optional SSH gateway used for the next fetch.
    /// </summary>
    void SetGateway(SshGatewayDto? gateway);

    /// <summary>
    /// Fetches the raw HTTP response headers for the specified URI.
    /// </summary>
    Task<HttpResponseInfo> FetchAsync(Uri uri, CancellationToken ct);
}

/// <summary>
/// Stateless HTTP header fetch service using direct TCP or SSH tunnel.
/// </summary>
public sealed class HttpHeaderService : IHttpHeaderService
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);
    private SshGatewayDto? _gateway;

    public void SetGateway(SshGatewayDto? gateway)
    {
        _gateway = gateway;
    }

    public async Task<HttpResponseInfo> FetchAsync(Uri uri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConnectionTimeout);
        var token = timeoutCts.Token;

        if (_gateway is not null)
        {
            return await FetchViaTunnelAsync(_gateway, uri, token).ConfigureAwait(false);
        }

        return await FetchDirectAsync(uri, token).ConfigureAwait(false);
    }

    private static async Task<HttpResponseInfo> FetchDirectAsync(Uri uri, CancellationToken ct)
    {
        var headResponse = await SendRequestAsync(uri, "HEAD", ct).ConfigureAwait(false);
        if (headResponse.StatusCode == 405)
        {
            return await SendRequestAsync(uri, "GET", ct).ConfigureAwait(false);
        }

        return headResponse;
    }

    private static async Task<HttpResponseInfo> SendRequestAsync(Uri uri, string method, CancellationToken ct)
    {
        var port = uri.IsDefaultPort
            ? (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? HttpHeaderEvaluationEngine.DefaultHttpsPort
                : HttpHeaderEvaluationEngine.DefaultHttpPort)
            : uri.Port;

        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, port, ct).ConfigureAwait(false);

        Stream stream = client.GetStream();
        SslStream? sslStream = null;
        try
        {
            if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                sslStream = new SslStream(stream, leaveInnerStreamOpen: true, static (_, _, _, _) => true);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = uri.Host,
                }, ct).ConfigureAwait(false);
                stream = sslStream;
            }

            var sanitizedHost = uri.Host.Replace("\r", string.Empty, StringComparison.Ordinal)
                                        .Replace("\n", string.Empty, StringComparison.Ordinal);
            var sanitizedPath = (string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal);

            var request =
                $"{method} {sanitizedPath} HTTP/1.1\r\n" +
                $"Host: {sanitizedHost}\r\n" +
                "Connection: close\r\n" +
                "User-Agent: Heimdall\r\n\r\n";

            var requestBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            var buffer = new byte[HttpHeaderEvaluationEngine.MaxResponseBytes];
            var totalRead = 0;

            while (totalRead < buffer.Length)
            {
                var bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    ct).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                totalRead += bytesRead;

                var currentText = Encoding.ASCII.GetString(buffer, 0, totalRead);
                if (currentText.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    break;
                }
            }

            var rawResponse = Encoding.ASCII.GetString(buffer, 0, totalRead);
            return HttpHeaderEvaluationEngine.ParseHttpResponse(rawResponse);
        }
        finally
        {
            if (sslStream is not null)
            {
                await sslStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<HttpResponseInfo> FetchViaTunnelAsync(
        SshGatewayDto gateway,
        Uri uri,
        CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            using var client = await ToolGatewayConnector.ConnectAsync(gateway, ct).ConfigureAwait(false);
            var curlCommand = $"curl -sI --max-time 10 {InputValidator.EscapeShellArg(uri.ToString())} 2>/dev/null";
            using var cmd = client.CreateCommand(curlCommand);
            cmd.CommandTimeout = ConnectionTimeout;

            var result = cmd.Execute()?.Trim() ?? string.Empty;
            return HttpHeaderEvaluationEngine.ParseHttpResponse(result + "\r\n\r\n");
        }, ct).ConfigureAwait(false);
    }
}
