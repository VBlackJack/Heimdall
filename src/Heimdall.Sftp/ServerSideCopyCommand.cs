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

using Heimdall.Core.Security;

namespace Heimdall.Sftp;

/// <summary>
/// Builds the <c>cp</c> command line for a server-side SFTP copy run over an SSH exec channel.
/// Pure string construction with no I/O, so the shell-escaping contract is unit-testable in isolation.
/// </summary>
/// <remarks>
/// Both paths are single-quoted through <see cref="InputValidator.EscapeShellArg(string)"/> (CWE-78),
/// and a <c>--</c> end-of-options guard prevents a path that begins with <c>-</c> from being parsed as
/// a flag. Flag choice preserves metadata, fixing the permission/timestamp loss of the download +
/// re-upload roundtrip: <c>-p</c> preserves mode, ownership and timestamps for a single file, and
/// <c>-a</c> (archive) does the same recursively for a directory tree. No <c>-n</c>/<c>-f</c> is added:
/// overwrite protection is enforced by the caller's destination-exists check before this command runs.
/// </remarks>
internal static class ServerSideCopyCommand
{
    /// <summary>
    /// Returns <c>cp -p -- '&lt;src&gt;' '&lt;dst&gt;'</c> for a file copy, or
    /// <c>cp -a -- '&lt;src&gt;' '&lt;dst&gt;'</c> when <paramref name="recursive"/> is true.
    /// </summary>
    /// <param name="sourcePath">Remote source path (escaped before use).</param>
    /// <param name="destinationPath">Remote destination path (escaped before use).</param>
    /// <param name="recursive">True for a directory copy (<c>-a</c>); false for a single file (<c>-p</c>).</param>
    public static string Build(string sourcePath, string destinationPath, bool recursive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var flags = recursive ? "-a" : "-p";
        var source = InputValidator.EscapeShellArg(sourcePath);
        var destination = InputValidator.EscapeShellArg(destinationPath);

        return $"cp {flags} -- {source} {destination}";
    }
}
