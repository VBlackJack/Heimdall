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

using System.IO;
using System.Threading;

namespace Heimdall.App.Tests;

/// <summary>
/// Removes a file a test created, without ever failing the test that created it.
/// </summary>
/// <remarks>
/// <para>Windows refuses to delete an image while the process started from it is still exiting, and
/// disposing a session does not make that release synchronous. A cleanup block that deletes such a
/// file therefore throws <see cref="UnauthorizedAccessException"/> from time to time - and throws it
/// after the assertions have already passed, so a test that proved what it set out to prove is
/// reported as a failure. That happened on a loaded runner and not once on the developer machine,
/// which is the signature of this class of defect rather than an argument that it is rare.</para>
/// <para>The short retry is not a synchronisation point and nothing waits on its result: it is there
/// so the normal case still leaves the temporary directory clean. When it gives up it gives up
/// quietly, because a file left in the temporary directory is a smaller problem than a green test
/// reported red.</para>
/// </remarks>
internal static class TemporaryFileCleanup
{
    /// <summary>How long to keep retrying before leaving the file behind.</summary>
    /// <remarks>
    /// Paid only when the handle is genuinely still open, which is the exceptional case. A process
    /// that has been asked to exit releases its image well inside this.
    /// </remarks>
    private static readonly TimeSpan ReleaseBudget = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Deletes <paramref name="path"/> if it exists, retrying briefly while it is still held.
    /// </summary>
    /// <param name="path">The file to remove. A null or blank path is ignored.</param>
    /// <param name="releaseBudget">
    /// How long to keep retrying. Callers leave this unset; it exists so the test covering the
    /// give-up path does not spend the full budget proving it, which would put five seconds on
    /// every run of the suite to demonstrate a branch that never fires in practice.
    /// </param>
    internal static void Delete(string? path, TimeSpan? releaseBudget = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        DateTime deadline = DateTime.UtcNow + (releaseBudget ?? ReleaseBudget);
        while (true)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException or IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return;
                }

                Thread.Sleep(RetryInterval);
            }
        }
    }
}
