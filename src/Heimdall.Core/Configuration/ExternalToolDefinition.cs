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

namespace Heimdall.Core.Configuration;

/// <summary>
/// Defines an external tool that can be launched from the server context menu.
/// Supports variable placeholders in <see cref="Arguments"/>:
/// <c>{Host}</c>, <c>{Port}</c>, <c>{User}</c>, <c>{ServerName}</c>,
/// <c>{Protocol}</c>, <c>{KeyFile}</c>, <c>{Project}</c>, <c>{Gateway}</c>.
/// </summary>
public sealed class ExternalToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string? IconGlyph { get; set; }

    /// <summary>Working directory for the launched process. Empty = inherit from parent.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Launch with elevated privileges (UAC prompt on Windows).</summary>
    public bool RunAsAdministrator { get; set; }

    /// <summary>Run the process hidden (no console window flash for scripts).</summary>
    public bool RunHidden { get; set; }

    /// <summary>
    /// Supported placeholder variables and their descriptions.
    /// </summary>
    public static readonly (string Variable, string DescriptionKey)[] SupportedPlaceholders =
    [
        ("{Host}",       "ExtToolVarHost"),
        ("{Port}",       "ExtToolVarPort"),
        ("{User}",       "ExtToolVarUser"),
        ("{ServerName}", "ExtToolVarServerName"),
        ("{Protocol}",   "ExtToolVarProtocol"),
        ("{KeyFile}",    "ExtToolVarKeyFile"),
        ("{Project}",    "ExtToolVarProject"),
        ("{Gateway}",    "ExtToolVarGateway"),
    ];

    /// <summary>
    /// Replaces variable placeholders in <see cref="Arguments"/> with actual server values, in
    /// one pass over the template (<see cref="ArgumentTemplate.Expand"/>): a value is never
    /// expanded again. Sanitization is context-aware: for shell targets (.bat, .cmd, cmd.exe,
    /// powershell) all cmd metacharacters are stripped. For regular executables, only characters
    /// that affect MSVC CRT argument parsing are removed, preserving legitimate values like
    /// <c>Web (prod)</c> or paths with single quotes; a value right before a closing double quote
    /// has its trailing backslashes doubled. Quotes are never added. See CWE-78 and CWE-88.
    /// </summary>
    /// <remarks>
    /// Values are not judged here; this is the form the settings preview shows. A launch goes
    /// through <see cref="TryResolveArguments"/>, which refuses a value that would not stay one
    /// plain argument.
    /// </remarks>
    public string ResolveArguments(
        string host,
        int port,
        string user,
        string? serverName = null,
        string? protocol = null,
        string? keyFile = null,
        string? project = null,
        string? gateway = null)
    {
        var placeholders = BuildPlaceholders(host, port, user, serverName, protocol, keyFile, project, gateway);
        return ExpandWith(Arguments, ExecutablePath, placeholders);
    }

    /// <summary>
    /// <see cref="ResolveArguments"/> for a launch: false, with the placeholder at fault, when a
    /// substituted value would not stay one plain argument (<see cref="ArgumentTemplate.FindUnsafeValue"/>).
    /// </summary>
    /// <remarks>
    /// A user name of <c>-oProxyCommand=\\host\s\p.exe a</c> handed to OpenSSH through
    /// <c>{User}@{Host}</c> runs a program of the profile author's choosing; with a space it also
    /// splits into several arguments. Neither is altered into something that might still work:
    /// the launch is refused and the user told why.
    /// </remarks>
    public bool TryResolveArguments(
        string host,
        int port,
        string user,
        out string arguments,
        out ArgumentTemplateRejection? rejection,
        string? serverName = null,
        string? protocol = null,
        string? keyFile = null,
        string? project = null,
        string? gateway = null)
    {
        var placeholders = BuildPlaceholders(host, port, user, serverName, protocol, keyFile, project, gateway);
        rejection = ArgumentTemplate.FindUnsafeValue(Arguments, placeholders);
        if (rejection is not null)
        {
            arguments = string.Empty;
            return false;
        }

        arguments = ExpandWith(Arguments, ExecutablePath, placeholders);
        return true;
    }

    /// <summary>
    /// Expands <paramref name="template"/> for <paramref name="executablePath"/> the way an
    /// external tool's arguments are expanded. The auto-detected tools share it.
    /// </summary>
    /// <remarks>
    /// The values have already been sanitized by the caller; the double quotes are stripped once
    /// more here, so that no value can close a quote of the template whatever sanitizer it went
    /// through.
    /// </remarks>
    public static string ExpandWith(
        string template,
        string executablePath,
        IReadOnlyList<(string Token, string Value)> placeholders)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(placeholders);

        var unquoted = placeholders
            .Select(static placeholder => (placeholder.Token, placeholder.Value.Replace("\"", string.Empty, StringComparison.Ordinal)))
            .ToArray();
        return ArgumentTemplate.Expand(
            template,
            unquoted,
            protectClosingQuote: !InputValidator.IsShellTarget(executablePath));
    }

    /// <summary>
    /// <see cref="TryResolveArguments"/> for an auto-detected tool (Sysinternals, NirSoft), whose
    /// template knows only <c>{Host}</c>, <c>{Port}</c> and <c>{User}</c>. Every value is stripped
    /// of shell metacharacters, as a detected tool's always were.
    /// </summary>
    /// <param name="template">The tool's argument template.</param>
    /// <param name="executablePath">The tool's executable.</param>
    /// <param name="host">The target host, or empty.</param>
    /// <param name="port">The target port as text, or empty.</param>
    /// <param name="user">The user name, or empty.</param>
    /// <param name="arguments">The expanded arguments, or empty when refused.</param>
    /// <param name="rejection">Why a value was refused, or null.</param>
    public static bool TryResolveDetectedArguments(
        string template,
        string executablePath,
        string? host,
        string? port,
        string? user,
        out string arguments,
        out ArgumentTemplateRejection? rejection)
    {
        ArgumentNullException.ThrowIfNull(template);

        (string Token, string Value)[] placeholders =
        [
            ("{Host}", SanitizeStrict(host ?? string.Empty)),
            ("{Port}", port ?? string.Empty),
            ("{User}", SanitizeStrict(user ?? string.Empty)),
        ];
        rejection = ArgumentTemplate.FindUnsafeValue(template, placeholders);
        if (rejection is not null)
        {
            arguments = string.Empty;
            return false;
        }

        arguments = ExpandWith(template, executablePath, placeholders);
        return true;
    }

    private (string Token, string Value)[] BuildPlaceholders(
        string host,
        int port,
        string user,
        string? serverName,
        string? protocol,
        string? keyFile,
        string? project,
        string? gateway)
    {
        Func<string, string> sanitize = InputValidator.IsShellTarget(ExecutablePath)
            ? SanitizeStrict
            : SanitizeRelaxed;

        return
        [
            ("{Host}", sanitize(host)),
            ("{Port}", port.ToString(CultureInfo.InvariantCulture)),
            ("{User}", sanitize(user)),
            ("{ServerName}", sanitize(serverName ?? "")),
            ("{Protocol}", sanitize(protocol ?? "")),
            ("{KeyFile}", sanitize(keyFile ?? "")),
            ("{Project}", sanitize(project ?? "")),
            ("{Gateway}", sanitize(gateway ?? "")),
        ];
    }

    /// <summary>
    /// Strips all shell metacharacters - used when the target is a shell interpreter
    /// (cmd.exe, .bat, .cmd, PowerShell) or when the target is unknown.
    /// </summary>
    private static string SanitizeStrict(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(value, @"[;&|`$<>()!""'\r\n%^]", "");
    }

    /// <summary>
    /// Strips characters that affect MSVC CRT argument parsing (double quotes)
    /// or could chain/redirect process execution. Preserves parentheses,
    /// single quotes, percent signs and other characters that are safe when
    /// the target is a regular executable (not a shell interpreter).
    /// </summary>
    private static string SanitizeRelaxed(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(value, @"[;&|`$<>""\r\n]", "");
    }
}
