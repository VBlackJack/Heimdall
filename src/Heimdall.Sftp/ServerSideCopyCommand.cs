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
/// The staging file is also created exclusively, and the destination is checked after the
/// link. Both were once deferred as "unmeasurable without a live server"; they were then
/// measured, on 2026-09-07, against three shells and three <c>cp</c> implementations
/// (BusyBox 1.36.1, GNU coreutils 9.1 and 8.25), and the deferral did not survive:
/// </para>
/// <list type="bullet">
///   <item><description><c>cp -p</c> chmods a destination that already exists - a file
///   pre-created at 644 came out 600 after copying a 600 source, on all three - so
///   reserving the staging file first does NOT cost the mode preservation this path
///   exists to guarantee, which was the whole reason for deferring it.</description></item>
///   <item><description><c>set -C</c> with a redirection refuses a name already taken,
///   including by a symlink, and writes nothing through it.</description></item>
///   <item><description><c>ln</c> given a symlink publishes the SYMLINK, not the file it
///   points at, and exits 0 while doing it. That is why the destination is tested with
///   <c>[ -L ]</c> afterwards: a staging file swapped between the copy and the link would
///   otherwise leave the destination pointing wherever the attacker chose, reported as a
///   successful copy.</description></item>
/// </list>
/// <para>
/// What is still not closed: the measurements are from containers, not from every server
/// Heimdall may talk to, and the <c>[ -L ]</c> test is itself a check-then-act - it refuses
/// the symlink this chain published, not every hostile rearrangement of a directory the
/// attacker can write to. A refused copy exits non-zero, which the caller reports as a copy
/// the server could not make safely, so the failure mode is availability, never a silent
/// wrong result.
/// </para>
/// </remarks>
internal static class ServerSideCopyCommand
{
    /// <summary>
    /// The status the remote chain exits with when its own link published a symlink.
    /// </summary>
    /// <remarks>
    /// The caller collapses every non-zero status into one refusal, so the value only has
    /// to be non-zero and out of the way of the exit codes cp, ln and the shell use. It is
    /// named rather than spelled inline so the chain and its test agree by construction.
    /// </remarks>
    internal const int PublishedASymlinkStatus = 99;

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

        // Three steps, each measured rather than argued (the measurements are in the type
        // remark): the staging file is created exclusively, so cp writes only a file this
        // command made; the copy and the link are unchanged; and a destination that came
        // out as a symlink is removed and reported, which is what a staging file swapped
        // between the copy and the link produces.
        // The staging cleanup sits after the reservation on purpose. A refused reservation
        // means the name was already taken by something this command did not create, and
        // deleting that is not its business.
        return $"set -C; : > {tempDestination} || exit $?; set +C; "
            + $"cp -p -- {source} {tempDestination} && ln -- {tempDestination} {destination}; "
            + $"status=$?; if [ $status -eq 0 ] && [ -L {destination} ]; then "
            + $"rm -f -- {destination}; status={PublishedASymlinkStatus}; fi; "
            + $"rm -f -- {tempDestination}; exit $status";
    }
}
