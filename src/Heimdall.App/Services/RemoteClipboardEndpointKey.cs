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
/// Carries a browser's clipboard endpoint identity across decorators.
/// </summary>
/// <remarks>
/// A decorator hides the concrete browser type, so a type test on the browser answers about the
/// wrapper and yields no identity at all. That is not cosmetic: two endpoints that both resolve to an
/// empty key look like the same endpoint, and a paste between two different servers is then routed to
/// the same-endpoint path, which never consults the no-clobber gate.
/// <para>
/// Deliberately its own interface rather than a member of <see cref="IRemoteBrowser"/>: only the
/// clipboard needs this notion, and no transport should have to answer for it. A decorator implements
/// it by asking its inner browser, so a wrapper around a wrapper still reaches the raw browser.
/// </para>
/// </remarks>
internal interface IRemoteClipboardEndpointIdentity
{
    /// <summary>
    /// Gets the already-normalized endpoint key, or <c>null</c> when no identity is available.
    /// </summary>
    string? ClipboardEndpointKey { get; }
}

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

        // SSH first, so the existing logical-host identity of an SFTP session keeps priority over
        // anything a browser could report about the socket it happens to be using.
        if (sshParams is not null)
        {
            return FromSsh(sshParams);
        }

        // Then the seam, before the concrete type. The browser reaching this method is the one the view
        // hands to the view model, which is the operations-log decorator whenever logging is wired: a
        // type test alone answers about the wrapper and loses the identity entirely.
        if (browser is IRemoteClipboardEndpointIdentity identity)
        {
            // Read once. The property resolves through the inner browser on every call, so testing
            // one read and returning another could return null from a method that promises a string.
            string? carriedKey = identity.ClipboardEndpointKey;
            if (!string.IsNullOrWhiteSpace(carriedKey))
            {
                return carriedKey;
            }
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
