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

namespace Heimdall.Sftp;

/// <summary>
/// Escapes file paths for safe use in shell commands (CWE-78 prevention).
/// </summary>
public static class PathEscaper
{
    /// <summary>
    /// Escapes a path for use in a POSIX shell command using single-quote wrapping.
    /// Single quotes within the path are escaped as <c>'\''</c> (end quote, escaped
    /// literal quote, start quote).
    /// </summary>
    /// <param name="path">The path to escape.</param>
    /// <returns>A single-quoted, shell-safe path string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains control characters.</exception>
    public static string EscapeForShell(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (char c in path)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException(
                    $"Path contains control character (U+{(int)c:X4}). " +
                    "Control characters are not permitted in shell-escaped paths.",
                    nameof(path));
            }
        }

        // Single-quote the entire path. Any embedded single quotes become: '\''
        // This closes the current single-quoted segment, adds an escaped literal
        // single quote, then reopens a new single-quoted segment.
        string escaped = path.Replace("'", @"'\''");
        return $"'{escaped}'";
    }

    /// <summary>
    /// Escapes a path for a command that the remote account's login shell parses before any
    /// <c>sh -c</c> boundary: <see cref="EscapeForShell(string)"/>, plus the history-expansion
    /// character.
    /// </summary>
    /// <remarks>
    /// csh and tcsh expand <c>!</c> as a history event even inside single quotes, and a bare event
    /// reference fails the whole command; zsh does not expand it under <c>-c</c>. Each <c>!</c>
    /// becomes <c>'\!'</c>: POSIX sh reads it back as a literal <c>!</c>, and csh and tcsh read the
    /// backslash-escaped form without expanding it.
    /// </remarks>
    /// <param name="path">The path to escape.</param>
    /// <returns>A single-quoted path with every <c>!</c> neutralized.</returns>
    public static string EscapeForLoginShell(string path) =>
        EscapeForShell(path).Replace("!", "'\\!'", StringComparison.Ordinal);
}
