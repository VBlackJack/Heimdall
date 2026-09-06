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
/// Provides same-directory temp-file writes for atomic local file replacement.
/// </summary>
public static class AtomicLocalFile
{
    /// <summary>
    /// Creates a unique temporary path next to the final destination path.
    /// </summary>
    /// <param name="finalPath">The final file path that will be replaced after a successful write.</param>
    /// <returns>A unique <c>.part</c> path in the same directory as <paramref name="finalPath"/>.</returns>
    public static string CreateTempPath(string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);

        string fileName = Path.GetFileName(finalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Final path must include a file name.", nameof(finalPath));
        }

        string? directory = Path.GetDirectoryName(finalPath);
        string tempFileName = $"{fileName}.{Guid.NewGuid():N}.part";
        return string.IsNullOrEmpty(directory)
            ? tempFileName
            : Path.Combine(directory, tempFileName);
    }

    /// <summary>
    /// Atomically replaces the final file with the completed temporary file.
    /// </summary>
    /// <remarks>
    /// Retried on a sharing violation. On Windows an antivirus scanner, the search indexer
    /// or a preview handler holds a freshly written file, or the one about to be replaced,
    /// for a moment; a single unretried move turned that moment into a discarded download.
    /// A replaced file held open surfaces as access denied rather than as a sharing
    /// violation, so both are retried; a real permission failure still propagates after the
    /// last attempt.
    /// </remarks>
    public static void Commit(string tempPath, string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tempPath, finalPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransientMoveFailure(ex) && attempt < CommitRetryDelays.Length)
            {
                Thread.Sleep(CommitRetryDelays[attempt]);
            }
        }
    }

    private static bool IsTransientMoveFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    /// <summary>Waits between commit attempts; the last failure propagates.</summary>
    private static readonly TimeSpan[] CommitRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(450),
    ];

    /// <summary>
    /// Removes an abandoned temporary file without touching the final path.
    /// </summary>
    public static void Rollback(string tempPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"AtomicLocalFile rollback failed for '{tempPath}': {ex.Message}");
        }
    }
}
