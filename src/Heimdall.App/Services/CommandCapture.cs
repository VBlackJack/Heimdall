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

using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Turns text selected in a terminal into the command the Command Library editor opens with.
/// </summary>
/// <remarks>
/// <para>The session manager and the library both ask which platform a capture belongs to, and
/// they must agree: the separator a multi-line selection is joined with is the one of the
/// pattern it is written into. The answer is <see cref="RemoteOsResolver"/>'s, narrowed to one
/// platform, because a captured line is a command for one shell, never for both.</para>
/// <para>A multi-line selection becomes one line, its lines chained with the platform's
/// sequence operator. That is rarely the finished command, and it is not meant to be: the
/// operator reads it in the editor before saving, and the editor's consistency warning tells
/// the rest.</para>
/// </remarks>
internal static class CommandCapture
{
    /// <summary>What joins two captured lines in a Windows pattern.</summary>
    public const string WindowsLineSeparator = " ; ";

    /// <summary>What joins two captured lines in a Linux pattern.</summary>
    public const string LinuxLineSeparator = " && ";

    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>
    /// The platform a command captured from a session is written for: Windows when the session
    /// resolves to Windows (a declared Windows far end, or LOCAL, RDP and WINRM), Linux
    /// otherwise, including when nothing can be told.
    /// </summary>
    /// <param name="declared">The operating system the session's profile declares.</param>
    /// <param name="connectionType">The session's connection type, any case.</param>
    public static RemoteOs ResolvePlatform(RemoteOs declared, string? connectionType)
        => RemoteOsResolver.Resolve(declared, connectionType) == RemoteOs.Windows
            ? RemoteOs.Windows
            : RemoteOs.Linux;

    /// <summary>
    /// The selection as one command line: each line trimmed, blank lines dropped, the rest joined
    /// with the separator of <paramref name="platform"/>.
    /// </summary>
    /// <param name="selection">The text as the terminal page posted it.</param>
    /// <param name="platform">The platform from <see cref="ResolvePlatform"/>.</param>
    /// <returns>The command line; empty when the selection holds nothing but whitespace.</returns>
    public static string ToCommandLine(string? selection, RemoteOs platform)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            return string.Empty;
        }

        string[] lines = selection.Split(
            LineBreaks,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(
            platform == RemoteOs.Windows ? WindowsLineSeparator : LinuxLineSeparator,
            lines);
    }

    /// <summary>
    /// The capture a tool context carries, or null when it carries no command text.
    /// </summary>
    /// <param name="context">The context the Command Library was opened with.</param>
    public static CapturedCommand? FromContext(ToolContext? context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.InitialCommandText))
        {
            return null;
        }

        string? tag = string.IsNullOrWhiteSpace(context.ConnectionType)
            ? null
            : context.ConnectionType.Trim().ToLowerInvariant();
        return new CapturedCommand(
            context.InitialCommandText.Trim(),
            ResolvePlatform(context.RemoteOs, context.ConnectionType),
            tag);
    }
}
