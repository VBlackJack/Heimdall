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
/// Builds the server-side commands that publish an already-uploaded staging path, or reserve a
/// directory, without being able to replace anything that already exists.
/// </summary>
/// <remarks>
/// Pure string construction with no I/O, so the shell-escaping contract is unit-testable in isolation.
/// Every path is single-quoted through <see cref="PathEscaper.EscapeForShell(string)"/> (CWE-78) and
/// guarded by <c>--</c> so a path beginning with <c>-</c> cannot be parsed as a flag.
/// <para>
/// Three claims that must not be conflated. The exclusivity of <c>ln</c> and of <c>mkdir</c> without
/// <c>-p</c> is a semantic guarantee of those utilities. Acceptance of <c>--</c> as an end-of-options
/// marker is a compatibility property of the remote implementation, not something universally
/// guaranteed. And neither has been observed against a real server. A remote utility that does not
/// accept these forms makes the command fail, which the caller treats as a refusal: there is no
/// fallback to a primitive that could replace the destination.
/// </para>
/// <para>
/// Distinct from <see cref="ServerSideCopyCommand"/>, which copies a remote source into a remote
/// destination. Here the content already sits at a staging path this client uploaded and owns, so
/// only the publication step remains.
/// </para>
/// <para>
/// <c>ln</c> is the whole guarantee: creating a hard link fails when the target name exists, and it is
/// the kernel that decides, not a client-side probe. The destination therefore appears already holding
/// the complete content, so no reader can observe a partial file at the final path. A rename would
/// replace the destination silently on many servers and is never used.
/// </para>
/// </remarks>
internal static class ServerSideNoClobberPublishCommand
{
    /// <summary>
    /// Returns a command that links <paramref name="stagingPath"/> to
    /// <paramref name="destinationPath"/> and then removes the staging link.
    /// </summary>
    /// <remarks>
    /// The exit status is that of <c>ln</c>, captured before the cleanup runs. Sampling it first is
    /// what keeps the two failure modes separate: a cleanup that succeeds cannot mask a link that
    /// failed, and a cleanup that fails cannot turn a publication that succeeded into a failure. The
    /// staging path is removed either way: after a successful link the content is reachable under the
    /// final name, and after a failed one the staging file is this client's to discard.
    /// </remarks>
    /// <param name="stagingPath">Remote staging path this client uploaded and owns.</param>
    /// <param name="destinationPath">Final remote path, which must not already exist.</param>
    public static string BuildFilePublish(string stagingPath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string staging = PathEscaper.EscapeForShell(stagingPath);
        string destination = PathEscaper.EscapeForShell(destinationPath);

        return $"ln -- {staging} {destination}; status=$?; rm -f -- {staging}; exit $status";
    }

    /// <summary>
    /// Returns a command that creates <paramref name="destinationPath"/> as a new directory.
    /// </summary>
    /// <remarks>
    /// Deliberately without <c>-p</c>: that flag succeeds when the directory already exists, which is
    /// exactly how a stale listing turns into a merge and then into an overwritten subtree. Every node
    /// of a pasted tree is reserved this way, root and descendants alike.
    /// </remarks>
    /// <param name="destinationPath">Directory path that must not already exist.</param>
    public static string BuildDirectoryReservation(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        // The same escaper as the file publish and the copy: two escapers with different
        // contracts side by side is how one of them ends up handling a character the other refuses.
        return $"mkdir -- {PathEscaper.EscapeForShell(destinationPath)}";
    }
}
