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
using Heimdall.Core.Security;

namespace Heimdall.Core.Ssh;

/// <summary>
/// Validated OpenSSH <c>LocalForward</c> specification (<c>[bind_address:]local_port remote_host:remote_port</c>
/// or compact <c>local_port:remote_host:remote_port</c>).
/// </summary>
public sealed record SshLocalForwardSpec(
    string BindAddress,
    int LocalPort,
    string RemoteHost,
    int RemotePort)
{
    private const string DefaultLoopback = "127.0.0.1";

    /// <summary>Formats as OpenSSH <c>LocalForward</c> directive arguments.</summary>
    public string ToOpenSshDirectiveValue()
        => string.Create(CultureInfo.InvariantCulture, $"{FormatHost(BindAddress)}:{LocalPort} {FormatHost(RemoteHost)}:{RemotePort}");

    /// <summary>Formats as compact <c>localPort:remoteHost:remotePort</c> (or with bind address).</summary>
    public string ToCanonicalString()
        => string.Equals(BindAddress, DefaultLoopback, StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"{LocalPort}:{FormatHost(RemoteHost)}:{RemotePort}")
            : string.Create(CultureInfo.InvariantCulture, $"{FormatHost(BindAddress)}:{LocalPort}:{FormatHost(RemoteHost)}:{RemotePort}");

    private static string FormatHost(string host)
        => host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;

    /// <summary>
    /// Parses an OpenSSH <c>LocalForward</c> value such as <c>8080 127.0.0.1:80</c>,
    /// <c>127.0.0.1:8080 db.internal:5432</c>, or <c>8080:db.internal:5432</c>.
    /// </summary>
    public static bool TryParse(string? raw, out SshLocalForwardSpec? spec)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        var whitespaceTokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        string localPart;
        string remotePart;

        if (whitespaceTokens.Length == 2)
        {
            localPart = whitespaceTokens[0];
            remotePart = whitespaceTokens[1];
        }
        else if (whitespaceTokens.Length == 1)
        {
            // Compact form: localPort:remoteHost:remotePort or bindAddr:localPort:remoteHost:remotePort
            // Respects bracketed IPv6 literals such as [::1]:8443:app.local:443.
            var parts = SplitColonOutsideBrackets(trimmed);
            if (parts is null)
            {
                return false;
            }

            if (parts.Count == 3)
            {
                localPart = parts[0];
                remotePart = $"{parts[1]}:{parts[2]}";
            }
            else if (parts.Count == 4)
            {
                localPart = $"{parts[0]}:{parts[1]}";
                remotePart = $"{parts[2]}:{parts[3]}";
            }
            else
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (!TryParseBindAndPort(localPart, out var bindAddress, out var localPort))
        {
            return false;
        }

        if (!TryParseHostAndPort(remotePart, out var remoteHost, out var remotePort))
        {
            return false;
        }

        spec = new SshLocalForwardSpec(bindAddress, localPort, remoteHost, remotePort);
        return true;
    }

    private static List<string>? SplitColonOutsideBrackets(string input)
    {
        var result = new List<string>(4);
        var start = 0;
        var inBrackets = false;

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '[')
            {
                if (inBrackets)
                {
                    return null;
                }

                inBrackets = true;
            }
            else if (ch == ']')
            {
                if (!inBrackets)
                {
                    return null;
                }

                inBrackets = false;
            }
            else if (ch == ':' && !inBrackets)
            {
                result.Add(input[start..i]);
                start = i + 1;
            }
        }

        if (inBrackets)
        {
            return null;
        }

        result.Add(input[start..]);
        return result;
    }

    private static bool TryParseBindAndPort(string value, out string bindAddress, out int port)
    {
        bindAddress = DefaultLoopback;
        port = 0;

        var lastColon = value.LastIndexOf(':');
        if (lastColon < 0)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                && InputValidator.ValidatePortRange(port);
        }

        var hostPart = value[..lastColon].Trim('[', ']');
        var portPart = value[(lastColon + 1)..];
        if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || !InputValidator.ValidatePortRange(port))
        {
            return false;
        }

        // Restrict local binds to loopback for safety; wildcard/external binds fall back to 127.0.0.1.
        if (string.IsNullOrWhiteSpace(hostPart)
            || string.Equals(hostPart, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostPart, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(hostPart, "::1", StringComparison.Ordinal))
        {
            bindAddress = string.Equals(hostPart, "::1", StringComparison.Ordinal) ? "::1" : DefaultLoopback;
            return true;
        }

        if (!InputValidator.IsValidSshHost(hostPart))
        {
            return false;
        }

        bindAddress = DefaultLoopback;
        return true;
    }

    private static bool TryParseHostAndPort(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var lastColon = value.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == value.Length - 1)
        {
            return false;
        }

        var rawHost = value[..lastColon].Trim('[', ']');
        var portPart = value[(lastColon + 1)..];

        if (!InputValidator.IsValidSshHost(rawHost))
        {
            return false;
        }

        if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || !InputValidator.ValidatePortRange(port))
        {
            return false;
        }

        host = rawHost;
        return true;
    }
}
