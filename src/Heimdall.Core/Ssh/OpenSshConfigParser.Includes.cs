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

using System.Text;

namespace Heimdall.Core.Ssh;

public static partial class OpenSshConfigParser
{
    internal const int MaxIncludeDepth = 4;
    internal const int MaxIncludedFiles = 32;
    internal const int MaxTotalExpandedBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Reads and parses an OpenSSH config file from disk, safely expanding <c>Include</c> directives
    /// (including glob patterns such as <c>config.d/*.conf</c>) with depth, file-count, size, symlink,
    /// traversal, and cycle guards.
    /// </summary>
    public static OpenSshParseResult ParseFile(string configFilePath, string? userProfileOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

        var fullRootFile = Path.GetFullPath(configFilePath);
        var rootDir = Path.GetDirectoryName(fullRootFile) ?? Directory.GetCurrentDirectory();
        var userProfile = string.IsNullOrWhiteSpace(userProfileOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfileOverride;

        var expanded = new StringBuilder();
        var state = new IncludeExpansionState(rootDir, userProfile);
        ExpandFileRecursive(fullRootFile, depth: 0, state, expanded, isRoot: true);

        return Parse(expanded.ToString());
    }

    private static void ExpandFileRecursive(
        string filePath,
        int depth,
        IncludeExpansionState state,
        StringBuilder output,
        bool isRoot)
    {
        var canonical = Path.GetFullPath(filePath);
        if (!state.ActiveStack.Add(canonical))
        {
            return;
        }

        try
        {
            string rawText;
            if (isRoot)
            {
                rawText = File.ReadAllText(canonical, Encoding.UTF8);
            }
            else
            {
                if (!File.Exists(canonical) || IsReparsePoint(canonical))
                {
                    return;
                }

                var info = new FileInfo(canonical);
                if (state.TotalBytes + info.Length > MaxTotalExpandedBytes)
                {
                    return;
                }

                rawText = File.ReadAllText(canonical, Encoding.UTF8);
            }

            state.TotalBytes += Encoding.UTF8.GetByteCount(rawText);
            if (state.TotalBytes > MaxTotalExpandedBytes)
            {
                return;
            }

            var currentDir = Path.GetDirectoryName(canonical) ?? state.RootDirectory;
            var lines = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

            foreach (var line in lines)
            {
                var trimmed = StripComments(line).Trim();
                if (trimmed.Length > 0
                    && TrySplitDirective(trimmed, out var directive, out var value, out _)
                    && directive.Equals("include", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth < MaxIncludeDepth)
                    {
                        foreach (var pattern in SplitTokens(value))
                        {
                            foreach (var includedFile in ResolveIncludePattern(pattern, currentDir, state))
                            {
                                if (state.IncludedFileCount >= MaxIncludedFiles)
                                {
                                    break;
                                }

                                state.IncludedFileCount++;
                                ExpandFileRecursive(includedFile, depth + 1, state, output, isRoot: false);
                            }
                        }
                    }

                    continue;
                }

                output.Append(line).Append('\n');
            }
        }
        catch (IOException) when (!isRoot)
        {
            // Per OpenSSH ssh_config(5), unreadable or missing optional includes are ignored.
        }
        catch (UnauthorizedAccessException) when (!isRoot)
        {
            // Per OpenSSH ssh_config(5), unreadable optional includes are ignored.
        }
        finally
        {
            state.ActiveStack.Remove(canonical);
        }
    }

    private static IReadOnlyList<string> ResolveIncludePattern(
        string rawPattern,
        string currentDir,
        IncludeExpansionState state)
    {
        if (string.IsNullOrWhiteSpace(rawPattern))
        {
            return [];
        }

        var pattern = rawPattern.Trim().Trim('"');
        if (pattern.Length == 0 || pattern.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return [];
        }

        bool isExplicitHome = pattern.StartsWith("~/", StringComparison.Ordinal)
            || pattern.StartsWith(@"~\", StringComparison.Ordinal);

        string expandedPattern;
        if (isExplicitHome)
        {
            if (string.IsNullOrWhiteSpace(state.UserProfile))
            {
                return [];
            }

            expandedPattern = Path.Combine(state.UserProfile, pattern[2..]);
        }
        else if (Path.IsPathRooted(pattern))
        {
            expandedPattern = pattern;
        }
        else
        {
            expandedPattern = Path.Combine(currentDir, pattern);
        }

        // Split into parent directory + leaf glob pattern (supports * and ? on file name).
        var normalized = expandedPattern.Replace('/', Path.DirectorySeparatorChar);
        var lastSep = normalized.LastIndexOf(Path.DirectorySeparatorChar);
        var dirPart = lastSep >= 0 ? normalized[..lastSep] : currentDir;
        var filePattern = lastSep >= 0 ? normalized[(lastSep + 1)..] : normalized;

        if (string.IsNullOrWhiteSpace(dirPart)
            || string.IsNullOrWhiteSpace(filePattern)
            || dirPart.Contains('*', StringComparison.Ordinal)
            || dirPart.Contains('?', StringComparison.Ordinal))
        {
            return [];
        }

        string fullDir;
        try
        {
            fullDir = Path.GetFullPath(dirPart);
        }
        catch (Exception)
        {
            return [];
        }

        // Relative patterns must stay within the root config directory; ~/ patterns stay under UserProfile.
        if (!Path.IsPathRooted(pattern) && !isExplicitHome && !IsUnderDirectory(fullDir, state.RootDirectory))
        {
            return [];
        }

        if (isExplicitHome && !IsUnderDirectory(fullDir, state.UserProfile))
        {
            return [];
        }

        if (!Directory.Exists(fullDir) || IsReparsePoint(fullDir))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(fullDir, filePattern, SearchOption.TopDirectoryOnly)
                .Where(static candidate => !IsReparsePoint(candidate))
                .OrderBy(static candidate => candidate, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsUnderDirectory(string candidateDir, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot))
        {
            return false;
        }

        var root = Path.GetFullPath(allowedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(candidateDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private sealed class IncludeExpansionState(string rootDirectory, string userProfile)
    {
        public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);
        public string UserProfile { get; } = userProfile;
        public HashSet<string> ActiveStack { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int IncludedFileCount { get; set; }
        public long TotalBytes { get; set; }
    }
}
