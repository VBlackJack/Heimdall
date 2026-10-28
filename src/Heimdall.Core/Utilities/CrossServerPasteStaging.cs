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

namespace Heimdall.Core.Utilities;

/// <summary>
/// The local staging area a cross-server paste copies files through, and the startup sweep that
/// removes what a crashed or killed session left in it.
/// </summary>
/// <remarks>
/// Each staged file is deleted by the paste that created it. A process killed between the copy
/// and that delete leaves the file behind, holding the content of a remote file, so the sweep
/// removes anything old enough to belong to a finished session. The folder is created with the
/// restricted ACL (current user, Administrators, SYSTEM) rather than inheriting the temp
/// folder's.
/// </remarks>
public static class CrossServerPasteStaging
{
    /// <summary>How old a staged entry must be before it is assumed to be abandoned.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private const string ProductFolderName = "Heimdall";
    private const string StagingFolderName = "cross-server-paste";

    /// <summary>The staging folder under the current temporary root.</summary>
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), ProductFolderName, StagingFolderName);

    /// <summary>Returns a fresh, unused staging file path, creating the folder if needed.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string CreateTempPath() => CreateTempPath(Root);

    /// <summary>Returns a fresh, unused path under <paramref name="root"/>, creating it if needed.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string CreateTempPath(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        EnsureRestrictedDirectory(root);
        return Path.Combine(root, Guid.NewGuid().ToString("N"));
    }

    /// <summary>Removes entries of the production staging folder older than <see cref="MaxAge"/>.</summary>
    public static int SweepStale() => Sweep(Root, DateTimeOffset.UtcNow);

    /// <summary>
    /// Deletes entries under <paramref name="root"/> whose last write is older than
    /// <see cref="MaxAge"/>.
    /// </summary>
    /// <param name="root">The staging folder.</param>
    /// <param name="now">Current time, injected so the age rule can be tested.</param>
    /// <returns>How many entries were removed.</returns>
    /// <remarks>
    /// Never throws: this runs on the startup path, and an entry that resists deletion is one still
    /// in use by a running paste, which is the case that must be left alone.
    /// </remarks>
    public static int Sweep(string root, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            return 0;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        int removed = 0;
        foreach (string entry in entries)
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(entry) < MaxAge)
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }

                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held open by a paste in progress: leaving it is correct.
            }
        }

        return removed;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsureRestrictedDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        try
        {
            SecureFileWriter.CreateRestrictedDirectory(directory);
        }
        catch (IOException) when (Directory.Exists(directory))
        {
            // Another paste created it between the check and the create.
        }
    }
}
