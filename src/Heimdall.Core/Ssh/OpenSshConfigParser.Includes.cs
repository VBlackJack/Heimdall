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
    internal const string IncludeScopeResetMarker = "\x1E__HEIMDALL_INCLUDE_SCOPE_RESET__";

    /// <summary>
    /// Reads and parses an OpenSSH config file from disk, safely expanding top-level and
    /// <c>Host *</c> <c>Include</c> directives (including glob patterns such as <c>config.d/*.conf</c>)
    /// with depth, file-count, size, ancestor-symlink, UNC/traversal, and cycle guards.
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
            if (isRoot)
            {
                var rootInfo = new FileInfo(canonical);
                if (!rootInfo.Exists)
                {
                    throw new FileNotFoundException("OpenSSH config file not found.", canonical);
                }

                if (rootInfo.Length > MaxTotalExpandedBytes)
                {
                    throw new IOException($"OpenSSH config file exceeds maximum size of {MaxTotalExpandedBytes} bytes.");
                }
            }
            else
            {
                if (!File.Exists(canonical) || IsReparsePoint(canonical))
                {
                    return;
                }

                var info = new FileInfo(canonical);
                if (info.Length > MaxTotalExpandedBytes || state.TotalBytes + info.Length > MaxTotalExpandedBytes)
                {
                    return;
                }

                if (state.IncludedFileCount >= MaxIncludedFiles)
                {
                    return;
                }

                state.IncludedFileCount++;
            }

            var rawText = File.ReadAllText(canonical, Encoding.UTF8);
            var byteCount = Encoding.UTF8.GetByteCount(rawText);
            if (state.TotalBytes + byteCount > MaxTotalExpandedBytes)
            {
                if (isRoot)
                {
                    throw new IOException($"OpenSSH config file exceeds maximum size of {MaxTotalExpandedBytes} bytes.");
                }

                return;
            }

            state.TotalBytes += byteCount;

            var currentDir = Path.GetDirectoryName(canonical) ?? state.RootDirectory;
            var lines = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            var inMatchBlock = false;
            var inSpecificHostBlock = false;
            var inGlobalWildcardBlock = false;

            foreach (var line in lines)
            {
                var trimmed = StripComments(line).Trim();
                if (trimmed.Length > 0 && TrySplitDirective(trimmed, out var directive, out var value, out _))
                {
                    if (directive.Equals("match", StringComparison.OrdinalIgnoreCase))
                    {
                        inMatchBlock = true;
                        inSpecificHostBlock = false;
                        inGlobalWildcardBlock = false;
                        output.Append(line).Append('\n');
                        continue;
                    }

                    if (directive.Equals("host", StringComparison.OrdinalIgnoreCase))
                    {
                        inMatchBlock = false;
                        var hostTokens = SplitTokens(value);
                        var isGlobalWildcard = hostTokens.Count == 1 && hostTokens[0] == "*";
                        inSpecificHostBlock = !isGlobalWildcard;
                        inGlobalWildcardBlock = isGlobalWildcard;
                        output.Append(line).Append('\n');
                        continue;
                    }

                    if (directive.Equals("include", StringComparison.OrdinalIgnoreCase))
                    {
                        // Conditional Include inside Match or specific Host blocks (or beyond max depth)
                        // is passed through untouched so the parser emits IncludeDirectiveIgnored cleanly
                        // without corrupting Host/Match block scoping.
                        if (inMatchBlock || inSpecificHostBlock || depth >= MaxIncludeDepth)
                        {
                            output.Append(line).Append('\n');
                            continue;
                        }

                        var capReached = false;
                        foreach (var pattern in SplitTokens(value))
                        {
                            if (state.IncludedFileCount >= MaxIncludedFiles)
                            {
                                capReached = true;
                                break;
                            }

                            foreach (var includedFile in ResolveIncludePattern(pattern, currentDir, state))
                            {
                                if (state.IncludedFileCount >= MaxIncludedFiles)
                                {
                                    capReached = true;
                                    break;
                                }

                                ExpandFileRecursive(includedFile, depth + 1, state, output, isRoot: false);

                                // Isolate trailing Host/Match blocks after EACH included file so neither
                                // sibling files in a glob/multi-pattern Include nor subsequent lines in
                                // the parent file inherit a leaked Host/Match block.
                                output.Append(inGlobalWildcardBlock ? "Host *" : IncludeScopeResetMarker).Append('\n');
                            }

                            if (capReached)
                            {
                                break;
                            }
                        }

                        continue;
                    }
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
        if (pattern.Length == 0)
        {
            return [];
        }

        // Normalize slashes before any UNC/device/root checks so //server/share cannot bypass \\ checks.
        var normalizedInput = pattern.Replace('/', Path.DirectorySeparatorChar);
        if (normalizedInput.StartsWith(@"\\", StringComparison.Ordinal)
            || normalizedInput.StartsWith(@"//", StringComparison.Ordinal))
        {
            return [];
        }

        bool isExplicitHome = normalizedInput.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        string expandedPattern;
        string allowedRoot;

        if (isExplicitHome)
        {
            if (string.IsNullOrWhiteSpace(state.UserProfile))
            {
                return [];
            }

            expandedPattern = Path.Combine(state.UserProfile, normalizedInput[2..]);
            allowedRoot = state.UserProfile;
        }
        else if (Path.IsPathRooted(normalizedInput))
        {
            if (!Path.IsPathFullyQualified(normalizedInput))
            {
                return [];
            }

            expandedPattern = normalizedInput;
            allowedRoot = IsUnderDirectory(normalizedInput, state.RootDirectory)
                ? state.RootDirectory
                : !string.IsNullOrWhiteSpace(state.UserProfile) && IsUnderDirectory(normalizedInput, state.UserProfile)
                    ? state.UserProfile
                    : string.Empty;

            if (string.IsNullOrEmpty(allowedRoot))
            {
                return [];
            }
        }
        else
        {
            expandedPattern = Path.Combine(currentDir, normalizedInput);
            allowedRoot = state.RootDirectory;
        }

        // Split into parent directory + leaf glob pattern (supports * and ? on file name).
        string? dirPart;
        string filePattern;
        try
        {
            dirPart = Path.GetDirectoryName(expandedPattern);
            filePattern = Path.GetFileName(expandedPattern);
        }
        catch (ArgumentException)
        {
            return [];
        }

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

        if (!IsUnderDirectory(fullDir, allowedRoot))
        {
            return [];
        }

        if (!Directory.Exists(fullDir) || HasReparsePointAncestor(fullDir, allowedRoot))
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

    private static bool HasReparsePointAncestor(string directoryPath, string allowedRoot)
    {
        var canonicalRoot = Path.GetFullPath(allowedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        while (!string.IsNullOrEmpty(current))
        {
            if (IsReparsePoint(current))
            {
                return true;
            }

            if (string.Equals(current, canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return false;
    }

    private static bool IsUnderDirectory(string candidatePath, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot))
        {
            return false;
        }

        string root;
        string target;
        try
        {
            root = Path.GetFullPath(allowedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            target = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return false;
        }

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
