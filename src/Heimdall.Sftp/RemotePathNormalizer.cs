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
/// Collapses <c>.</c> and <c>..</c> segments of a slash-separated remote path, so two
/// spellings of one location compare equal.
/// </summary>
/// <remarks>
/// The containment guard of the server-side copy compared raw strings: it refused a
/// legitimate copy whose destination merely spelled a way out (<c>/srv/data</c> to
/// <c>/srv/data/../elsewhere</c>) and let a non-canonical source (<c>/srv/./data</c>)
/// past it. The FTP browser never collapsed <c>..</c> either, so its current directory
/// accumulated segments and every listed path inherited them. Textual only: no link is
/// resolved here, which is the point - the guard must not follow anything.
/// </remarks>
public static class RemotePathNormalizer
{
    /// <summary>
    /// Returns the absolute path with every <c>.</c> removed and every <c>..</c> applied,
    /// never climbing above the root. A relative input is treated as rooted.
    /// </summary>
    public static string Collapse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        List<string> segments = [];
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return "/" + string.Join('/', segments);
    }
}
