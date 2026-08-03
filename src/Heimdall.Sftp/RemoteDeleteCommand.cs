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
/// Builds the fail-closed shell command used for confined recursive remote deletion.
/// </summary>
/// <remarks>
/// The <c>--</c> end-of-options guard protects option-looking names, <c>LC_ALL=C</c> keeps
/// diagnostics stable for typed permission-denied mapping, and POSIX <c>rm -r</c> removes
/// symbolic links as entries instead of traversing their targets.
/// </remarks>
internal static class RemoteDeleteCommand
{
    /// <summary>Builds a recursive removal command for one shell-escaped remote path.</summary>
    /// <param name="path">Remote directory path.</param>
    /// <returns>The command to execute through the pinned SSH channel.</returns>
    public static string Build(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return $"LC_ALL=C rm -r -- {PathEscaper.EscapeForShell(path)}";
    }
}
