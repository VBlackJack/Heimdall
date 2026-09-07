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
/// Builds a non-clobbering server-side SFTP copy command run over an SSH exec channel.
/// String construction with no I/O; the file branch draws a fresh staging name per call, so
/// only the directory branch is deterministic and only it can be asserted by equality.
/// </summary>
/// <remarks>
/// <para>
/// Both paths are single-quoted through <see cref="PathEscaper.EscapeForShell(string)"/> (CWE-78),
/// and a <c>--</c> end-of-options guard prevents a path that begins with <c>-</c> from being parsed as
/// a flag. File copies use a sibling temp followed by <c>ln</c>, so the final path appears complete and
/// link creation fails atomically on collision. Directory copies reserve the root with <c>mkdir</c> before
/// filling it. Metadata remains preserved through <c>-p</c> for files and <c>-a</c> for directory trees.
/// </para>
/// <para>
/// What the unpredictable staging name buys, and what it does not. It removes the
/// pre-plantable write-through: the name used to be the remote shell's PID, so a user with
/// write access to the destination directory could plant a symlink at every plausible name
/// in advance and wait for <c>cp</c> to open through one. That is gone.
/// </para>
/// <para>
/// It does NOT make the staging file exclusive. The same attacker can unlink the staging
/// file and put a symlink in its place at any point between <c>cp</c> creating it and
/// <c>ln</c> publishing it - a window as long as the copy itself, needing no advance
/// knowledge - after which <c>ln</c> hard-links that symlink into the destination and the
/// chain exits 0. Closing that needs an exclusive reservation (<c>set -C</c> with a
/// redirection, or <c>mktemp</c>), and it is deliberately not done here: the destination is
/// a hard link to the staging inode, so the staging file's mode IS the published mode, and
/// pre-creating that file moves mode preservation from "cp creates it" to "cp -p must chmod
/// a file that already exists". Whether it does is a property of the remote cp, measurable
/// only against a live server. The finding stays OPEN-NARROWED until that measurement.
/// </para>
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

        string source = PathEscaper.EscapeForShell(sourcePath);
        string destination = PathEscaper.EscapeForShell(destinationPath);

        if (recursive)
        {
            // The reserved root and whatever cp managed to copy are removed when cp fails
            // part way: the caller reports that the copy was not performed, and a partial
            // tree left on the server made that statement false.
            return $"mkdir -- {destination} && cp -a -- {source}/. {destination}; "
                + $"status=$?; if [ $status -ne 0 ]; then rm -rf -- {destination}; fi; exit $status";
        }

        // The staging name is drawn client-side, by the same generator the upload path
        // uses, and never from a remote expansion. It used to be `$$`, the remote shell's
        // own PID: a few thousand possible names in a directory the attacker can write to,
        // so a local user on the server could plant a symlink at every one of them ahead of
        // time and have `cp -p` open through it. No race to win, only patience.
        string tempDestination = PathEscaper.EscapeForShell(
            SftpAtomicUpload.CreateRemoteTempPath(destinationPath));
        return $"cp -p -- {source} {tempDestination} && ln -- {tempDestination} {destination}; "
            + $"status=$?; rm -f -- {tempDestination}; exit $status";
    }
}
