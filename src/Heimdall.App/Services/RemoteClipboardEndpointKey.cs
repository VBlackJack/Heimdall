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

using System.Globalization;
using Heimdall.Core.Models;
using Heimdall.Sftp;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Builds stable same-server keys for the shared remote clipboard.
/// </summary>
public static class RemoteClipboardEndpointKey
{
    /// <summary>Builds a normalized endpoint key from raw connection parts.</summary>
    public static string FromParts(string host, int port, string? username)
        => FromParts(null, host, port, username);

    /// <summary>Builds a normalized endpoint key from protocol and raw connection parts.</summary>
    public static string FromParts(string? protocol, string host, int port, string? username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        string normalizedProtocol = string.IsNullOrWhiteSpace(protocol)
            ? string.Empty
            : protocol.Trim().ToLowerInvariant();
        string normalizedHost = host.Trim().ToLowerInvariant();
        string normalizedUser = string.IsNullOrWhiteSpace(username)
            ? string.Empty
            : username.Trim();
        string protocolPrefix = string.IsNullOrEmpty(normalizedProtocol)
            ? string.Empty
            : $"protocol={normalizedProtocol};";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{protocolPrefix}host={normalizedHost};port={port};user={normalizedUser}");
    }

    /// <summary>Builds a key from SSH connection parameters, using logical host identity when present.</summary>
    public static string FromSsh(SshConnectionParams sshParams)
    {
        ArgumentNullException.ThrowIfNull(sshParams);

        return FromParts(
            "sftp",
            sshParams.HostKeyVerificationHost,
            sshParams.HostKeyVerificationPort,
            sshParams.Username);
    }

    /// <summary>Builds a key from an FTP browser's connected endpoint metadata.</summary>
    public static string FromFtp(FtpBrowser ftpBrowser)
    {
        ArgumentNullException.ThrowIfNull(ftpBrowser);
        if (string.IsNullOrWhiteSpace(ftpBrowser.Host))
        {
            return string.Empty;
        }

        int port = ftpBrowser.Port > 0 ? ftpBrowser.Port : DefaultPorts.Ftp;
        return FromParts("ftp", ftpBrowser.Host, port, ftpBrowser.Username);
    }

    /// <summary>Builds a key from the available browser/session metadata.</summary>
    public static string FromConnection(
        IRemoteBrowser browser,
        string? endpoint,
        SshConnectionParams? sshParams)
    {
        ArgumentNullException.ThrowIfNull(browser);

        if (sshParams is not null)
        {
            return FromSsh(sshParams);
        }

        if (browser is FtpBrowser ftpBrowser)
        {
            string ftpKey = FromFtp(ftpBrowser);
            if (!string.IsNullOrEmpty(ftpKey))
            {
                return ftpKey;
            }
        }

        return TryFromEndpointLabel(endpoint, DefaultPorts.Ftp, out string endpointKey)
            ? endpointKey
            : string.Empty;
    }

    /// <summary>Attempts to parse labels shaped like user@host:port or host:port.</summary>
    public static bool TryFromEndpointLabel(string? endpoint, int defaultPort, out string endpointKey)
    {
        endpointKey = string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        string value = endpoint.Trim();
        string? username = null;
        int atIndex = value.LastIndexOf('@');
        if (atIndex > 0 && atIndex < value.Length - 1)
        {
            username = value[..atIndex];
            value = value[(atIndex + 1)..];
        }

        string host = value;
        int port = defaultPort;

        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            int closeBracket = value.IndexOf(']');
            if (closeBracket > 1)
            {
                host = value[1..closeBracket];
                if (closeBracket + 2 < value.Length
                    && value[closeBracket + 1] == ':'
                    && int.TryParse(value[(closeBracket + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort))
                {
                    port = parsedPort;
                }
            }
        }
        else
        {
            int lastColon = value.LastIndexOf(':');
            if (lastColon > 0
                && int.TryParse(value[(lastColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort))
            {
                host = value[..lastColon];
                port = parsedPort;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        endpointKey = FromParts(host, port, username);
        return true;
    }
}
