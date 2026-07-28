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

namespace Heimdall.App.Tests;

internal static class SourceFileEnumeration
{
    public static IEnumerable<string> EnumerateFiles(string root, string searchPattern)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);

        while (pendingDirectories.TryPop(out string? directory))
        {
            foreach (string file in Directory.EnumerateFiles(
                directory,
                searchPattern,
                SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }

            foreach (string childDirectory in Directory
                .EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsBuildOutputDirectory(childDirectory))
                {
                    pendingDirectories.Push(childDirectory);
                }
            }
        }
    }

    private static bool IsBuildOutputDirectory(string path)
    {
        string name = Path.GetFileName(path);
        return string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SourceFileEnumerationTests
{
    [Fact]
    public void EnumerateFiles_NeverReturnsPathsUnderBinOrObj()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"{nameof(SourceFileEnumerationTests)}-{Guid.NewGuid():N}");

        try
        {
            string sourceDirectory = Directory.CreateDirectory(
                Path.Combine(root, "Feature", "Views")).FullName;
            string binDirectory = Directory.CreateDirectory(
                Path.Combine(root, "Feature", "bin", "Debug")).FullName;
            string objDirectory = Directory.CreateDirectory(
                Path.Combine(root, "Feature", "OBJ", "Debug")).FullName;

            string sourcePath = Path.Combine(sourceDirectory, "View.xaml");
            File.WriteAllText(sourcePath, "<Grid />");
            File.WriteAllText(Path.Combine(binDirectory, "Generated.xaml"), "<Grid />");
            File.WriteAllText(Path.Combine(objDirectory, "Generated.xaml"), "<Grid />");

            string[] files = SourceFileEnumeration.EnumerateFiles(root, "*.xaml").ToArray();

            Assert.Equal([sourcePath], files);
            Assert.DoesNotContain(files, path => HasBuildOutputDirectorySegment(root, path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool HasBuildOutputDirectorySegment(string root, string path)
    {
        string relativePath = Path.GetRelativePath(root, path);
        string[] segments = relativePath.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }
}
