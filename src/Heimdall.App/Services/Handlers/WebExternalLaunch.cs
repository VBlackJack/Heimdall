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
using System.IO;
using System.Text.RegularExpressions;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;
using Heimdall.Core.Utilities;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// How a web profile set to open externally is started: the system browser, or the program the
/// profile names (a legacy browser for a console no current engine accepts).
/// </summary>
internal static class WebExternalLaunch
{
    /// <summary>The arguments a profile gets when it names a program and no arguments.</summary>
    public const string DefaultArguments = "{Url}";

    /// <summary>The longest program path the confirmation shows before it is cut.</summary>
    public const int MaxDescribedProgramLength = 260;

    /// <summary>The longest argument list the confirmation shows before it is cut.</summary>
    public const int MaxDescribedArgumentsLength = 1024;

    /// <summary>The localization key of the refusal of a program or argument list too long to show whole.</summary>
    public const string CommandTooLongErrorKey = "ErrorWebExternalCommandTooLong";

    /// <summary>The localization key of the refusal of a program the shell would resolve to a share or a device.</summary>
    public const string NetworkPathErrorKey = "ErrorWebExternalNetworkPathRefused";

    /// <summary>A URI scheme: two or more characters before the colon, since a single letter is a drive designator.</summary>
    private const string SchemePattern = "^[A-Za-z][A-Za-z0-9+.-]+:";

    private static readonly char[] PathSeparators = ['/', '\\'];

    private static readonly Regex UriScheme = new(SchemePattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The placeholder replaced with the whole address.</summary>
    public const string UrlPlaceholder = "{Url}";

    /// <summary>The outcome of preparing the external launch of a console already open in a tab.</summary>
    /// <param name="StartInfo">What to start; null when nothing may be started.</param>
    /// <param name="ErrorKey">The localization key of why it cannot be built, when it cannot.</param>
    /// <param name="Declined">True when the user was asked and refused.</param>
    public readonly record struct SessionLaunch(ProcessStartInfo? StartInfo, string? ErrorKey, bool Declined);

    /// <summary>
    /// Whether starting the session's external program needs the user's confirmation first: it
    /// names a program, and its profile has not been confirmed. The default browser runs nothing
    /// the profile chose, so it never asks.
    /// </summary>
    public static bool RequiresConfirmation(WebSessionResult session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return !string.IsNullOrWhiteSpace(session.ExternalProgram) && !session.ExternalExecutionConfirmed;
    }

    /// <summary>The program and arguments the confirmation shows, as the connect path shows them.</summary>
    public static string DescribeCommand(WebSessionResult session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return DescribeCommand(session.ExternalProgram, session.ExternalArguments);
    }

    /// <summary>
    /// The program and arguments as a confirmation shows them. Both come from a profile that may
    /// have been imported, so control, format and line-separator characters are written out
    /// visibly instead of starting a new line or hiding part of the command.
    /// </summary>
    public static string DescribeCommand(string? program, string? arguments)
    {
        string shownProgram = UntrustedText.MakeVisible(program, MaxDescribedProgramLength);
        string shownArguments = UntrustedText.MakeVisible(arguments, MaxDescribedArgumentsLength);
        return $"{shownProgram} {shownArguments}".Trim();
    }

    /// <summary>
    /// Whether the program and arguments fit the confirmation whole. A longer value would be cut
    /// with a marker, and the end of an argument list could then hold a flag nobody saw, so such a
    /// launch is refused instead of shown cut.
    /// </summary>
    public static bool FitsDescription(string? program, string? arguments) =>
        UntrustedText.MakeVisible(program, int.MaxValue).Length <= MaxDescribedProgramLength
        && UntrustedText.MakeVisible(arguments, int.MaxValue).Length <= MaxDescribedArgumentsLength;

    /// <summary>
    /// Whether <paramref name="program"/> would be resolved by the shell to something other than a
    /// local file: a value wrapped in quotes, a URI with a scheme (<c>file://host/share/x.exe</c>),
    /// or a path that is, or resolves to, a share or a device path. A bare program name and a
    /// drive path are not.
    /// </summary>
    public static bool IsNetworkOrIndirectProgram(string program)
    {
        if (program.Contains('"', StringComparison.Ordinal) || UriScheme.IsMatch(program))
        {
            return true;
        }

        if (ImportedPathPolicy.IsNetworkOrDevicePath(program))
        {
            return true;
        }

        if (program.IndexOfAny(PathSeparators) < 0)
        {
            return false;
        }

        try
        {
            return ImportedPathPolicy.IsNetworkOrDevicePath(Path.GetFullPath(program));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    /// <summary>
    /// Prepares the external launch, asking <paramref name="confirmAsync"/> (given the command to
    /// show) first when <see cref="RequiresConfirmation"/> says so. A refusal starts nothing; an
    /// acceptance is remembered on the session so the tab does not ask twice.
    /// </summary>
    public static async Task<SessionLaunch> PrepareForSessionAsync(
        WebSessionResult session,
        string? currentPage,
        Func<string, Task<bool>> confirmAsync)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(confirmAsync);

        if (!FitsDescription(session.ExternalProgram, session.ExternalArguments))
        {
            return new SessionLaunch(null, CommandTooLongErrorKey, Declined: false);
        }

        if (RequiresConfirmation(session))
        {
            if (!await confirmAsync(DescribeCommand(session)).ConfigureAwait(true))
            {
                return new SessionLaunch(null, null, Declined: true);
            }

            session.ExternalExecutionConfirmed = true;
        }

        ProcessStartInfo? startInfo = TryCreateForSession(session, currentPage, out string? errorKey);
        return new SessionLaunch(startInfo, errorKey, Declined: false);
    }

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

    /// <summary>
    /// Builds the start information, or returns the localization key of the reason it cannot.
    /// </summary>
    /// <remarks>
    /// A shell interpreter is refused as the program, as it is for external tools: the host and
    /// the path come from a profile that may have been imported, and cmd.exe or PowerShell would
    /// read their metacharacters. The address itself is a <see cref="Uri"/> built by
    /// <see cref="WebAddress"/>, whose escaped form carries no quote or space to split on.
    /// </remarks>
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

        if (IsNetworkOrIndirectProgram(program))
        {
            errorKey = NetworkPathErrorKey;
            return null;
        }

        if (InputValidator.IsShellTarget(program))
        {
            errorKey = "ErrorWebExternalShellRefused";
            return null;
        }

        if (!FitsDescription(program, profile.WebExternalArguments))
        {
            errorKey = CommandTooLongErrorKey;
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
