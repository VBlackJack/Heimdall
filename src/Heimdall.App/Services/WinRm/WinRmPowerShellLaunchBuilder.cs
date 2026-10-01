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
using System.IO;
using System.Net;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Services.WinRm;

/// <summary>
/// Local PowerShell process launch shape expected by Heimdall terminal sessions.
/// </summary>
/// <param name="Executable">The PowerShell host to start.</param>
/// <param name="Arguments">The host's command line arguments.</param>
/// <param name="EnvironmentVariables">
/// Variables that override the inherited environment of the host, or null to inherit it unchanged.
/// </param>
internal sealed record WinRmPowerShellLaunchSpec(
    string Executable,
    string Arguments,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

/// <summary>
/// Builds local PowerShell command lines that enter a remote WinRM session.
/// </summary>
internal sealed class WinRmPowerShellLaunchBuilder
{
    /// <summary>
    /// Exit code of the local PowerShell host when the remote session was entered and has ended.
    /// </summary>
    internal const int RemoteSessionEndedExitCode = 0;

    /// <summary>
    /// Exit code of the local PowerShell host when the remote session was never entered.
    /// </summary>
    internal const int RemoteSessionNotEnteredExitCode = 1;

    /// <summary>
    /// Global PowerShell variable set once Enter-PSSession has returned without error.
    /// </summary>
    internal const string RemoteSessionEnteredVariable = "$global:HeimdallWinRmEntered";

    /// <summary>Marks the remote session as entered; runs right after Enter-PSSession.</summary>
    internal const string RemoteSessionEnteredAssignment = RemoteSessionEnteredVariable + " = $true";

    /// <summary>
    /// Ends the local PowerShell host the first time it would show a LOCAL prompt.
    /// </summary>
    /// <remarks>
    /// The tab is a remote session, so a local prompt is never something to hand the user:
    /// broadcast, the Command Library and macros would run on this machine under a tab titled
    /// with the remote host. Enter-PSSession pushes the remote runspace and returns; the rest of
    /// the command still runs locally, and the host evaluates the prompt in the pushed runspace
    /// once the command completes. So this local prompt function runs only when no remote
    /// runspace is pushed: Enter-PSSession failed (exit 1), or the remote session ended through
    /// a remote <c>exit</c> or a dropped connection (exit 0). A plain <c>; exit</c> after
    /// Enter-PSSession would end the process before the user ever reached the remote prompt,
    /// and <c>exit</c> inside the prompt function is not honoured by the host (PSReadLine
    /// reports an ExitException and the local prompt stays), hence Environment.Exit.
    /// </remarks>
    internal static readonly string LocalPromptExitGuard = string.Format(
        CultureInfo.InvariantCulture,
        "{0} = $false; function global:prompt {{ if ({0}) {{ [Environment]::Exit({1}) }} [Environment]::Exit({2}) }}",
        RemoteSessionEnteredVariable,
        RemoteSessionEndedExitCode,
        RemoteSessionNotEnteredExitCode);

    private readonly Func<string, string?> _findExecutable;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly PowerShellModuleRoots _moduleRoots;

    public WinRmPowerShellLaunchBuilder(
        Func<string, string?>? findExecutable = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? fileExists = null,
        PowerShellModuleRoots? moduleRoots = null)
    {
        _findExecutable = findExecutable ?? ConnectionHelpers.FindInPath;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _fileExists = fileExists ?? File.Exists;
        _moduleRoots = moduleRoots ?? PowerShellModuleRoots.ForCurrentUser();
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
        IReadOnlyDictionary<string, string>? environment = BuildEnvironment(executable);
        if (server.WinRmIdentityMode == WinRmIdentityMode.Credential)
        {
            if (string.IsNullOrWhiteSpace(bootstrapScriptPath))
            {
                throw new WinRmConfigurationException(
                    "ErrorWinRmBootstrapPathMissing",
                    [],
                    "A bootstrap script path is required for WinRM stored-credential sessions.");
            }

            // The guard is defined by the command, not by the script: a script the execution
            // policy refuses (a Group Policy AllSigned overrides -ExecutionPolicy Bypass) never
            // runs its first line, and with -File the host then sat at a local prompt.
            string bootstrapCommand = LocalPromptExitGuard
                + "; & "
                + QuotePowerShellLiteral(bootstrapScriptPath);
            return new WinRmPowerShellLaunchSpec(
                executable,
                "-NoLogo -NoExit -NoProfile -ExecutionPolicy Bypass -Command "
                + QuoteCommandLineArgument(bootstrapCommand),
                environment);
        }

        string command = LocalPromptExitGuard
            + "; "
            + BuildEnterPSSessionCommand(
                server,
                computerName,
                port,
                credentialExpression: null)
            + " -ErrorAction Stop; "
            + RemoteSessionEnteredAssignment;
        return new WinRmPowerShellLaunchSpec(
            executable,
            "-NoLogo -NoExit -NoProfile -Command "
            + QuoteCommandLineArgument(command),
            environment);
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
        return PowerShellSingleQuotedString.Quote(value);
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
        return server.HasWinRmPortField && server.WinRmPort > 0
            ? server.WinRmPort
            : server.WinRmUseSsl ? DefaultPorts.WinRmHttps : DefaultPorts.WinRmHttp;
    }

    internal static void ValidateProfile(ServerProfileDto server)
    {
        ValidateWinRmProfile(server);
    }

    private IReadOnlyDictionary<string, string>? BuildEnvironment(string executable)
    {
        if (!WindowsPowerShellModulePath.IsWindowsPowerShell(executable))
        {
            return null;
        }

        string? modulePath = WindowsPowerShellModulePath.FromInherited(
            _getEnvironmentVariable(WindowsPowerShellModulePath.VariableName),
            _moduleRoots,
            _fileExists);
        return modulePath is null
            ? null
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [WindowsPowerShellModulePath.VariableName] = modulePath
            };
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

        if (!Enum.IsDefined(server.WinRmIdentityMode))
        {
            throw new WinRmConfigurationException(
                "ErrorWinRmProfileInvalid",
                [],
                "WinRM identity mode is invalid.");
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
