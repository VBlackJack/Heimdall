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

namespace Heimdall.App.Services;

/// <summary>
/// Builds the shell commands the network tools send through an SSH tunnel to probe a TCP port.
/// </summary>
/// <remarks>
/// The script is a fixed single-quoted string that reads the host and the port as positional
/// parameters (<c>$1</c>, <c>$2</c>). The values follow it as separately quoted arguments, so no
/// value is part of the script text and nothing in it can be expanded by the outer shell.
/// </remarks>
internal static class TunnelProbeCommands
{
    private const string AddressPatternName = "Address";
    private const string ProbeTimeoutSeconds = "2";
    private const string ScriptPlaceholderName = "_";
    private const string ConnectScript = "echo >/dev/tcp/\"$1\"/\"$2\"";
    private const string BannerScript = "cat < /dev/tcp/\"$1\"/\"$2\"";
    private const string ConnectSuffix = "2>/dev/null && echo OPEN || echo CLOSED";
    private const string BannerSuffix = "2>/dev/null | head -c";

    /// <summary>True when the host is a hostname or an IPv4 address and the port is in range.</summary>
    internal static bool IsProbeTargetValid(string? host, int port)
        => InputValidator.Validate(host, AddressPatternName) && InputValidator.ValidatePortRange(port);

    /// <summary>Builds the command that prints OPEN when the TCP connection succeeds and CLOSED otherwise.</summary>
    internal static string BuildConnectCommand(string host, int port)
        => $"timeout {ProbeTimeoutSeconds} bash -c {InputValidator.EscapeShellArg(ConnectScript)} {ScriptPlaceholderName} "
           + $"{BuildArguments(host, port)} {ConnectSuffix}";

    /// <summary>Builds the command that prints up to <paramref name="maxBytes"/> bytes the service sends on connect.</summary>
    internal static string BuildBannerCommand(string host, int port, int maxBytes)
        => $"timeout {ProbeTimeoutSeconds} bash -c {InputValidator.EscapeShellArg(BannerScript)} {ScriptPlaceholderName} "
           + $"{BuildArguments(host, port)} {BannerSuffix} {maxBytes.ToString(CultureInfo.InvariantCulture)}";

    private static string BuildArguments(string host, int port)
        => $"{InputValidator.EscapeShellArg(host)} {InputValidator.EscapeShellArg(port.ToString(CultureInfo.InvariantCulture))}";
}
