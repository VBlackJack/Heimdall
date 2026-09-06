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

namespace Heimdall.Core.Updates;

/// <summary>
/// Removes what an update attempt leaves behind when nothing else did.
/// </summary>
/// <remarks>
/// A staging directory is removed on exactly three paths: the download failing, the
/// package being disposed without a hand-off, and the relauncher's own cleanup. Every
/// other ending leaked it with the installer inside, tens to hundreds of megabytes
/// under a restrictive ACL: the relauncher killed, the machine powered off, an
/// antivirus holding the file. The relauncher transcripts accumulate one file per
/// attempt with nothing pruning them either. Both are swept at startup, on the same
/// terms as the editor working directories.
/// </remarks>
public static class UpdateStagingSweeper
{
    /// <summary>What a staging directory is called: the version and a unique suffix.</summary>
    public const string StagingDirectoryPattern = "Heimdall_*_*";

    /// <summary>What a relauncher transcript is called.</summary>
    public const string RelaunchLogPattern = "Heimdall_relaunch_*.log";

    /// <summary>
    /// How old a staging directory must be before it is assumed abandoned. A margin
    /// rather than a guess: a download in progress right now is younger than this.
    /// </summary>
    public static readonly TimeSpan StagingMinimumAge = TimeSpan.FromHours(24);

    /// <summary>How long a relauncher transcript is kept for diagnosis.</summary>
    public static readonly TimeSpan RelaunchLogRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// Deletes staging directories older than <see cref="StagingMinimumAge"/>.
    /// Never throws: a directory that resists deletion is one still in use.
    /// </summary>
    /// <returns>How many directories were removed.</returns>
    public static int SweepStaging(string updatesRoot, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesRoot);
        int removed = 0;
        foreach (string candidate in Enumerate(() => Directory.GetDirectories(updatesRoot, StagingDirectoryPattern)))
        {
            try
            {
                if (now - Directory.GetLastWriteTimeUtc(candidate) < StagingMinimumAge)
                {
                    continue;
                }

                Directory.Delete(candidate, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held: a download in flight, or an installer a relauncher is running.
            }
        }

        return removed;
    }

    /// <summary>
    /// Deletes relauncher transcripts older than <see cref="RelaunchLogRetention"/>.
    /// Never throws.
    /// </summary>
    /// <returns>How many files were removed.</returns>
    public static int SweepRelaunchLogs(string logsDirectory, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        int removed = 0;
        foreach (string candidate in Enumerate(() => Directory.GetFiles(logsDirectory, RelaunchLogPattern)))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(candidate) < RelaunchLogRetention)
                {
                    continue;
                }

                File.Delete(candidate);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A transcript still being written by a relauncher that has not finished.
            }
        }

        return removed;
    }

    private static string[] Enumerate(Func<string[]> list)
    {
        try
        {
            return list();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }
}
