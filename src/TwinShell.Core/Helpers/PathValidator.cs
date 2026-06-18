/*
 * Copyright 2025 Julien Bombled
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

using System.Diagnostics;
using System.IO;

namespace TwinShell.Core.Helpers;

/// <summary>
/// Centralized path validation helper for secure file operations.
/// Prevents path traversal, symlink attacks, and unauthorized file access.
/// </summary>
public static class PathValidator
{
    // Note: We use Debug.WriteLine for security-related validation failures
    // to avoid dependency on ILogger while still providing diagnostic info in debug builds
    /// <summary>
    /// Validates an export file path (allows user-chosen paths with security checks).
    /// </summary>
    /// <param name="filePath">The file path to validate</param>
    /// <returns>True if the path is valid for export operations</returns>
    public static bool IsExportPathValid(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            // SECURITY: Check for path traversal in input
            if (filePath.Contains(".."))
                return false;

            // SECURITY: Reject UNC paths (network paths) for exports
            if (filePath.StartsWith(@"\\") || filePath.StartsWith("//"))
                return false;

            // Get the absolute path and verify it's valid
            var fullPath = Path.GetFullPath(filePath);

            // Verify the directory exists or can be created
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
                return false;

            // Must have a valid filename
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(fileName))
                return false;

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PathValidator] Export path validation failed for '{filePath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Validates an import file path (allows user-selected paths via file dialog).
    /// </summary>
    /// <param name="filePath">The file path to validate</param>
    /// <param name="requiredExtension">Optional required file extension (e.g., ".json")</param>
    /// <returns>True if the path is valid for import operations</returns>
    public static bool IsImportPathValid(string filePath, string? requiredExtension = ".json")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            // SECURITY: Check for path traversal in input
            if (filePath.Contains(".."))
                return false;

            // SECURITY: Reject UNC paths (network paths)
            if (filePath.StartsWith(@"\\") || filePath.StartsWith("//"))
                return false;

            // Get the absolute path and verify it's valid
            var fullPath = Path.GetFullPath(filePath);

            // Check required extension if specified
            if (!string.IsNullOrEmpty(requiredExtension) &&
                !fullPath.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
                return false;

            // Must have a valid filename
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(fileName))
                return false;

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PathValidator] Import path validation failed for '{filePath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Validates that a file path is within the allowed base directory (sandboxed operations).
    /// Prevents path traversal and symlink attacks.
    /// </summary>
    /// <param name="filePath">The file path to validate</param>
    /// <param name="baseDirectory">The allowed base directory</param>
    /// <returns>True if the path is secure within the base directory</returns>
    public static bool IsPathWithinDirectory(string filePath, string baseDirectory)
    {
        try
        {
            // SECURITY: Check for path traversal in input before normalization
            if (filePath.Contains("..") || filePath.Contains("~"))
                return false;

            // SECURITY: Reject UNC paths (network paths)
            if (filePath.StartsWith(@"\\") || filePath.StartsWith("//"))
                return false;

            // Get the absolute paths
            var fullPath = Path.GetFullPath(filePath);
            var baseDir = Path.GetFullPath(baseDirectory);

            // SECURITY: Check for symbolic links
            if (File.Exists(fullPath))
            {
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false; // Reject symbolic links and junctions
            }
            else if (Directory.Exists(fullPath))
            {
                var dirInfo = new DirectoryInfo(fullPath);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false; // Reject symbolic links and junctions
            }

            // SECURITY: Improved path traversal validation
            // Check that the normalized path starts with the base directory
            if (!fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return false;

            // Ensure the path is within the base directory (not just starts with it)
            // e.g., prevent /base/exports/../etc/passwd
            if (fullPath.Length > baseDir.Length)
            {
                var nextChar = fullPath[baseDir.Length];
                if (nextChar != Path.DirectorySeparatorChar && nextChar != Path.AltDirectorySeparatorChar)
                    return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PathValidator] Directory boundary validation failed for '{filePath}' in '{baseDirectory}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Validates a file extension against allowed extensions.
    /// </summary>
    /// <param name="filePath">The file path to check</param>
    /// <param name="allowedExtensions">List of allowed extensions (e.g., ".json", ".xml")</param>
    /// <returns>True if the file has an allowed extension</returns>
    public static bool HasValidExtension(string filePath, params string[] allowedExtensions)
    {
        if (string.IsNullOrWhiteSpace(filePath) || allowedExtensions.Length == 0)
            return false;

        var extension = Path.GetExtension(filePath);
        return allowedExtensions.Any(ext =>
            ext.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }
}
