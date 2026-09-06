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

namespace Heimdall.Core.Utilities;

/// <summary>
/// Removes editor working directories left behind by earlier sessions.
/// </summary>
/// <remarks>
/// The remote file editor stages every file it opens into its own directory under the
/// temporary root. Those directories are removed when an edit ends normally, and
/// deliberately kept when the application is torn down while a save is still in flight -
/// that retention is what stops a teardown deleting a file an upload is still reading,
/// and it is also what preserves the user's typed text when they have to quit.
/// <para>
/// Nothing ever removed the kept ones. Each survives forever, holding the content of a
/// file the user was editing. This sweeps them at startup, once the session that created
/// them is demonstrably over.
/// </para>
/// </remarks>
public static class EditorTempSweeper
{
    /// <summary>
    /// How old a directory must be before it is assumed to belong to a finished session.
    /// </summary>
    /// <remarks>
    /// A margin rather than a guess at process lifetime. Sweeping runs at startup, so a
    /// directory younger than this belongs either to a session that has only just ended
    /// or to another instance running right now: the single-instance guard is scoped to
    /// one data root, and a second instance on another root shares this temporary root.
    /// <para>
    /// The retained text is not offered back to the user at the next launch; it is kept
    /// so a teardown mid-save cannot delete a file an upload is still reading, and it is
    /// swept once that session is demonstrably over. Recovering it by hand from this
    /// root within the margin is the only way it comes back.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Deletes editor working directories older than <see cref="MinimumAge"/>.
    /// </summary>
    /// <param name="root">The editor's temporary root.</param>
    /// <param name="now">Current time, injected so the age rule can be tested.</param>
    /// <returns>How many directories were removed.</returns>
    /// <remarks>
    /// Never throws. This runs on a startup path, and failing to tidy up is not a reason
    /// to fail a launch: a directory that resists deletion is one still in use, which is
    /// exactly the case that must be left alone.
    /// </remarks>
    public static int Sweep(string root, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            return 0;
        }

        int removed = 0;
        string[] candidates;
        try
        {
            candidates = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (string candidate in candidates)
        {
            try
            {
                // The write time rather than the creation time: an edit that was saved
                // repeatedly over a long session should be judged by its last activity,
                // not by when its first file was staged.
                DateTimeOffset lastWrite = Directory.GetLastWriteTimeUtc(candidate);
                if (now - lastWrite < MinimumAge)
                {
                    continue;
                }

                Directory.Delete(candidate, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held by a running instance, or by an upload that has not finished.
                // Leaving it is the correct outcome, not a failure to report.
            }
        }

        return removed;
    }
}
