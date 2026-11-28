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

namespace Heimdall.Core.Ssh;

/// <summary>
/// Validates remote POSIX directory paths received from untrusted terminal escape
/// sequences (such as OSC 7) before using them as drag-and-drop SFTP upload targets.
/// </summary>
public static class TerminalDropPathValidator
{
    /// <summary>Fallback remote directory when no trusted OSC 7 directory is available.</summary>
    public const string DefaultRemoteDirectory = ".";

    /// <summary>Maximum accepted remote directory path length.</summary>
    public const int MaxRemotePathLength = 4096;

    /// <summary>
    /// Validates a remote directory path from OSC 7. Returns a normalized POSIX directory
    /// path, or <see cref="DefaultRemoteDirectory"/> if the input is null, empty, contains
    /// control characters, traversal segments (<c>..</c>), or exceeds length limits.
    /// </summary>
    public static string SanitizeRemoteDirectory(string? remoteCwd)
    {
        if (string.IsNullOrWhiteSpace(remoteCwd))
        {
            return DefaultRemoteDirectory;
        }

        var trimmed = remoteCwd.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxRemotePathLength)
        {
            return DefaultRemoteDirectory;
        }

        foreach (char c in trimmed)
        {
            if (char.IsControl(c) || c == '\0' || c == '\\')
            {
                return DefaultRemoteDirectory;
            }
        }

        if (!trimmed.StartsWith('/'))
        {
            return DefaultRemoteDirectory;
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (segment is "." or "..")
            {
                return DefaultRemoteDirectory;
            }
        }

        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    /// <summary>
    /// Combines a sanitized remote directory and a local filename into a safe POSIX remote path.
    /// Returns <see langword="false"/> if the filename is invalid or contains path separators.
    /// </summary>
    public static bool TryCombineRemoteFilePath(string remoteDirectory, string fileName, out string remoteFilePath)
    {
        remoteFilePath = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var trimmedName = fileName.Trim();
        if (trimmedName is "." or ".."
            || trimmedName.IndexOfAny(['/', '\\', '\0']) >= 0
            || trimmedName.Any(char.IsControl))
        {
            return false;
        }

        var dir = string.Equals(remoteDirectory, DefaultRemoteDirectory, StringComparison.Ordinal)
            ? DefaultRemoteDirectory
            : SanitizeRemoteDirectory(remoteDirectory);

        remoteFilePath = dir switch
        {
            DefaultRemoteDirectory => trimmedName,
            "/" => "/" + trimmedName,
            _ => dir + "/" + trimmedName
        };
        return true;
    }
}
