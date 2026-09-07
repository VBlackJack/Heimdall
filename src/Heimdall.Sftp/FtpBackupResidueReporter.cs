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
/// Reports each backup residue once per connection, however often its directory is listed.
/// </summary>
/// <remarks>
/// A file browser lists the same directory constantly - after a refresh, after an upload,
/// after a delete, on every return to it. Warning every time would train the user to dismiss
/// the warning, which is worse than not warning at all. The de-duplication lives here rather
/// than in the browser so it can be tested without a server.
/// </remarks>
public sealed class FtpBackupResidueReporter
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the residues of this listing that have not been reported yet, and records them.
    /// </summary>
    public IReadOnlyList<FtpBackupResidueFinding> Report(
        IReadOnlyList<SftpFileInfo> entries,
        IReadOnlyCollection<string> presentNames)
    {
        IReadOnlyList<FtpBackupResidueFinding> found = FtpBackupResidue.Classify(entries, presentNames);
        if (found.Count == 0)
        {
            return [];
        }

        var fresh = new List<FtpBackupResidueFinding>();
        foreach (FtpBackupResidueFinding finding in found)
        {
            if (_reported.Add(finding.RemotePath))
            {
                fresh.Add(finding);
            }
        }

        return fresh;
    }

    /// <summary>
    /// Forgets what was reported, so a new connection says it again.
    /// </summary>
    /// <remarks>
    /// A reconnect is where the user gets a fresh chance to act on the residue, and where the
    /// server may have changed under them. Silence across a reconnect would make the warning
    /// depend on how long the application had been running.
    /// </remarks>
    public void Reset() => _reported.Clear();
}
