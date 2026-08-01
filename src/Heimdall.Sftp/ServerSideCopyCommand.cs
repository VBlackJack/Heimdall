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
/// Builds a non-clobbering server-side SFTP copy command run over an SSH exec channel.
/// Pure string construction with no I/O, so the shell-escaping contract is unit-testable in isolation.
/// </summary>
/// <remarks>
/// Both paths are single-quoted through <see cref="InputValidator.EscapeShellArg(string)"/> (CWE-78),
/// and a <c>--</c> end-of-options guard prevents a path that begins with <c>-</c> from being parsed as
/// a flag. File copies use a sibling temp followed by <c>ln</c>, so the final path appears complete and
/// link creation fails atomically on collision. Directory copies reserve the root with <c>mkdir</c> before
/// filling it. Metadata remains preserved through <c>-p</c> for files and <c>-a</c> for directory trees.
/// </remarks>
internal static class ServerSideCopyCommand
{
    /// <summary>
    /// Returns a sibling-temp and hard-link chain for a file copy, or an exclusive root reservation
    /// followed by an archive copy when <paramref name="recursive"/> is true.
    /// </summary>
    /// <param name="sourcePath">Remote source path (escaped before use).</param>
    /// <param name="destinationPath">Remote destination path (escaped before use).</param>
    /// <param name="recursive">True for a directory copy (<c>-a</c>); false for a single file (<c>-p</c>).</param>
    public static string Build(string sourcePath, string destinationPath, bool recursive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string source = InputValidator.EscapeShellArg(sourcePath);
        string destination = InputValidator.EscapeShellArg(destinationPath);

        if (recursive)
        {
            return $"mkdir -- {destination} && cp -a -- {source}/. {destination}";
        }

        string tempDestination = $"{destination}.$$.part";
        return $"cp -p -- {source} {tempDestination} && ln -- {tempDestination} {destination}; "
            + $"status=$?; rm -f -- {tempDestination}; exit $status";
    }
}
