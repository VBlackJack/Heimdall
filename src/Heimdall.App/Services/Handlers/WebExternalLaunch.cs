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
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// How a web profile set to open externally is started: the system browser, or the program the
/// profile names (a legacy browser for a console no current engine accepts).
/// </summary>
internal static class WebExternalLaunch
{
    /// <summary>The arguments a profile gets when it names a program and no arguments.</summary>
    public const string DefaultArguments = "{Url}";

    /// <summary>The placeholder replaced with the whole address.</summary>
    public const string UrlPlaceholder = "{Url}";

    /// <summary>
    /// Builds the start information, or returns the localization key of the reason it cannot.
    /// </summary>
    /// <remarks>
    /// A shell interpreter is refused as the program, as it is for external tools: the host and
    /// the path come from a profile that may have been imported, and cmd.exe or PowerShell would
    /// read their metacharacters. The address itself is a <see cref="Uri"/> built by
    /// <see cref="WebAddress"/>, whose escaped form carries no quote or space to split on.
    /// </remarks>
    /// <summary>
    /// The start information for a console already open in a tab: the program its profile names,
    /// or the default browser, on the page the tab shows when it is one of the console's own.
    /// </summary>
    /// <remarks>
    /// The current page only replaces the profile's address when it is on the same scheme, host
    /// and port: a page the console redirected elsewhere is not handed to a program that the
    /// profile's arguments may have been written for.
    /// </remarks>
    public static ProcessStartInfo? TryCreateForSession(WebSessionResult session, string? currentPage, out string? errorKey)
    {
        ArgumentNullException.ThrowIfNull(session);
        Uri address = session.Address;
        if (Uri.TryCreate(currentPage, UriKind.Absolute, out Uri? current)
            && string.Equals(current.Scheme, address.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.Host, address.Host, StringComparison.OrdinalIgnoreCase)
            && current.Port == address.Port)
        {
            address = current;
        }

        var profile = new ServerProfileDto
        {
            DisplayName = session.DisplayName ?? address.Host,
            RemoteServer = address.Host,
            WebUsername = session.Username,
            WebExternalProgram = session.ExternalProgram,
            WebExternalArguments = session.ExternalArguments,
        };
        return TryCreate(profile, address, out errorKey);
    }

    public static ProcessStartInfo? TryCreate(ServerProfileDto profile, Uri address, out string? errorKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(address);
        errorKey = null;

        string program = profile.WebExternalProgram?.Trim() ?? string.Empty;
        if (program.Length == 0)
        {
            return new ProcessStartInfo
            {
                FileName = address.AbsoluteUri,
                UseShellExecute = true,
            };
        }

        if (InputValidator.IsShellTarget(program))
        {
            errorKey = "ErrorWebExternalShellRefused";
            return null;
        }

        string template = string.IsNullOrWhiteSpace(profile.WebExternalArguments)
            ? DefaultArguments
            : profile.WebExternalArguments;
        var expander = new ExternalToolDefinition
        {
            ExecutablePath = program,
            Arguments = template,
        };
        // The browser is started on a command line built from the profile, so a user name that
        // starts with a dash would reach it as an option, like an external tool's would.
        if (!expander.TryResolveArguments(
                profile.RemoteServer ?? string.Empty,
                address.Port,
                profile.WebUsername ?? string.Empty,
                out string resolved,
                out _,
                serverName: profile.DisplayName,
                protocol: "WEB"))
        {
            errorKey = "ErrorWebExternalArgumentRejected";
            return null;
        }

        string arguments = resolved.Replace(UrlPlaceholder, address.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

        return new ProcessStartInfo
        {
            FileName = program,
            Arguments = arguments,
            UseShellExecute = true,
        };
    }
}
