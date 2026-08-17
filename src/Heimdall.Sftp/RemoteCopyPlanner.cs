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
/// Path containment rule for a same-server remote copy.
/// </summary>
/// <remarks>
/// This is the surviving half of the former client-side copy planner. That planner walked a tree and
/// copied it child by child through a download-and-re-upload roundtrip; both transports have since
/// stopped using it, because neither can publish a file without risking an existing destination
/// (FTP refuses outright, SFTP requires its server-side exclusive reservation).
/// <para>
/// The containment rule outlived the planner on purpose. It was enforced only inside the walk, which
/// a successful server-side copy already bypassed, so deleting the planner wholesale would have
/// removed the check entirely and left "copy a directory into its own subtree" to whatever the
/// server's <c>cp</c> happens to do. It is now applied by the caller before any command runs.
/// </para>
/// </remarks>
internal static class RemoteCopyPlanner
{
    // Joins a remote directory path and a child leaf name using forward slashes, matching the
    // separator convention already used by the remote listings. Retained with its original name
    // because the upload and cross-endpoint transfer planners both call it; only the copy walk it
    // once served has gone away.
    internal static string JoinRemote(string directory, string childName)
        => $"{directory.TrimEnd('/')}/{childName}";
}

internal static class RemoteCopyPathGuard
{
    /// <summary>
    /// Returns whether the destination is the source itself or lies inside it.
    /// </summary>
    /// <remarks>
    /// Comparison is ordinal on slash-normalized paths, matching the separator convention of the
    /// remote listings. A sibling that merely shares a textual prefix, such as <c>/a/bc</c> against
    /// <c>/a/b</c>, is not a descendant: the trailing separator in the comparison prevents that.
    /// </remarks>
    /// <param name="sourcePath">Remote copy source.</param>
    /// <param name="destinationPath">Remote copy destination.</param>
    internal static bool IsSameOrDescendantPath(string sourcePath, string destinationPath)
    {
        string normalizedSource = sourcePath.TrimEnd('/');
        string normalizedDestination = destinationPath.TrimEnd('/');

        // Trimming the remote root produces an empty string. Reject it explicitly because every
        // remote destination is within the server root.
        if (normalizedSource.Length == 0)
        {
            return true;
        }

        return string.Equals(normalizedDestination, normalizedSource, StringComparison.Ordinal)
            || normalizedDestination.StartsWith(
                $"{normalizedSource}/",
                StringComparison.Ordinal);
    }
}
