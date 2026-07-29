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
using System.Net;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Services.WinRm;

/// <summary>
/// Local PowerShell process launch shape expected by Heimdall terminal sessions.
/// </summary>
internal sealed record WinRmPowerShellLaunchSpec(string Executable, string Arguments);

/// <summary>
/// Builds local PowerShell command lines that enter a remote WinRM session.
/// </summary>
internal sealed class WinRmPowerShellLaunchBuilder
{
    private readonly Func<string, string?> _findExecutable;

    public WinRmPowerShellLaunchBuilder(Func<string, string?>? findExecutable = null)
    {
        _findExecutable = findExecutable ?? ConnectionHelpers.FindInPath;
    }

    public WinRmPowerShellLaunchSpec Build(
        ServerProfileDto server,
        string? bootstrapScriptPath = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Build(
            server,
            server.RemoteServer,
            ResolvePort(server),
            bootstrapScriptPath);
    }

    public WinRmPowerShellLaunchSpec Build(
        ServerProfileDto server,
        string computerName,
        int port,
        string? bootstrapScriptPath = null)
    {
        ValidateWinRmProfile(server);
        ValidateWinRmEndpoint(computerName, port);

        string executable = ResolvePowerShellExecutable();
        if (server.WinRmIdentityMode == WinRmIdentityMode.Credential)
        {
            if (string.IsNullOrWhiteSpace(bootstrapScriptPath))
            {
                throw new WinRmConfigurationException(
                    "ErrorWinRmBootstrapPathMissing",
                    [],
                    "A bootstrap script path is required for WinRM stored-credential sessions.");
            }

            return new WinRmPowerShellLaunchSpec(
                executable,
                "-NoLogo -NoExit -NoProfile -ExecutionPolicy Bypass -File "
                + QuoteCommandLineArgument(bootstrapScriptPath));
        }

        string command = BuildEnterPSSessionCommand(
            server,
            computerName,
            port,
            credentialExpression: null);
        return new WinRmPowerShellLaunchSpec(
            executable,
            "-NoLogo -NoExit -NoProfile -Command "
            + QuoteCommandLineArgument(command));
    }

    internal static string BuildEnterPSSessionCommand(
        ServerProfileDto server,
        string? credentialExpression)
    {
        ArgumentNullException.ThrowIfNull(server);
        return BuildEnterPSSessionCommand(
            server,
            server.RemoteServer,
            ResolvePort(server),
            credentialExpression);
    }

    internal static string BuildEnterPSSessionCommand(
        ServerProfileDto server,
        string computerName,
        int port,
        string? credentialExpression)
    {
        ValidateWinRmProfile(server);
        ValidateWinRmEndpoint(computerName, port);

        List<string> parts =
        [
            "Enter-PSSession",
            "-ComputerName",
            QuotePowerShellLiteral(computerName),
            "-Port",
            port.ToString(CultureInfo.InvariantCulture),
            "-Authentication",
            "Negotiate"
        ];

        if (server.WinRmUseSsl)
        {
            parts.Add("-UseSSL");
        }

        if (server.WinRmUseSsl && server.WinRmSkipCertificateCheck)
        {
            parts.Add("-SessionOption");
            parts.Add("(New-PSSessionOption -SkipCACheck -SkipCNCheck -SkipRevocationCheck)");
        }

        if (!string.IsNullOrWhiteSpace(credentialExpression))
        {
            parts.Add("-Credential");
            parts.Add(credentialExpression);
        }

        return string.Join(" ", parts);
    }

    internal static string QuotePowerShellLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    internal static string QuoteCommandLineArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder builder = new StringBuilder();
        builder.Append('"');
        int index = 0;
        while (index < value.Length)
        {
            int backslashes = 0;
            while (index < value.Length && value[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index == value.Length)
            {
                builder.Append('\\', backslashes * 2);
            }
            else if (value[index] == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                index++;
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(value[index]);
                index++;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    internal static int ResolvePort(ServerProfileDto server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.WinRmPort > 0
            ? server.WinRmPort
            : server.WinRmUseSsl ? DefaultPorts.WinRmHttps : DefaultPorts.WinRmHttp;
    }

    internal static void ValidateProfile(ServerProfileDto server)
    {
        ValidateWinRmProfile(server);
    }

    private string ResolvePowerShellExecutable()
    {
        string? pwshPath = _findExecutable("pwsh.exe");
        if (!string.IsNullOrWhiteSpace(pwshPath))
        {
            return pwshPath;
        }

        string? windowsPowerShellPath = _findExecutable("powershell.exe");
        if (!string.IsNullOrWhiteSpace(windowsPowerShellPath))
        {
            return windowsPowerShellPath;
        }

        throw new WinRmConfigurationException(
            "ErrorWinRmNoPowerShellHost",
            [],
            "No PowerShell host executable was found. Install PowerShell 7 (pwsh.exe) or Windows PowerShell (powershell.exe), or add one to PATH.");
    }

    private static void ValidateWinRmProfile(ServerProfileDto server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (!string.Equals(server.ConnectionType, "WINRM", StringComparison.OrdinalIgnoreCase))
        {
            throw new WinRmConfigurationException(
                "ErrorWinRmProfileInvalid",
                [],
                "Server profile is not a WinRM profile.");
        }

        if (!IsValidHost(server.RemoteServer))
        {
            throw new WinRmConfigurationException(
                "ErrorWinRmInvalidHost",
                [],
                "Invalid WinRM host.");
        }

        if (!InputValidator.ValidatePortRange(ResolvePort(server)))
        {
            throw new WinRmConfigurationException(
                "ErrorWinRmInvalidPort",
                [],
                "Invalid WinRM port.");
        }
    }

    private static void ValidateWinRmEndpoint(string computerName, int port)
    {
        if (!IsValidHost(computerName))
        {
            throw new WinRmConfigurationException(
                "ErrorWinRmInvalidHost",
                [],
                "Invalid WinRM computer name.");
        }

        if (!InputValidator.ValidatePortRange(port))
        {
            throw new WinRmConfigurationException(
                "ErrorWinRmInvalidPort",
                [],
                "Invalid WinRM port.");
        }
    }

    private static bool IsValidHost(string? host)
    {
        return !string.IsNullOrWhiteSpace(host)
            && (InputValidator.ValidateDomain(host) || IPAddress.TryParse(host, out _));
    }
}
