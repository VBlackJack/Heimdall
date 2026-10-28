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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Heimdall.Core.Tests;

/// <summary>
/// The SSH.NET version the documentation names is the version the projects reference.
/// </summary>
/// <remarks>
/// Four guides named SSH.NET 2025.1.0 for months after the projects moved to 2026.0.0, and two of
/// them hung an explanation on it ("has no built-in Pageant agent support") that nobody had
/// re-checked against the newer package. A version in prose has no gate of its own, so this is
/// one. The changelog and the release notes are history and keep the versions they were written
/// against; they are not scanned.
/// </remarks>
public sealed class DocumentedDependencyVersionTests
{
    private static readonly Regex DocumentedVersion = new(
        @"SSH\.NET (\d+\.\d+\.\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReferencedVersion = new(
        @"<PackageReference\s+Include=""SSH\.NET""\s+Version=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void EverySshNetVersionTheDocumentationNamesIsTheOneTheProjectsReference()
    {
        string root = FindRepoRoot();
        HashSet<string> referenced = ReferencedVersions(root);
        Assert.True(referenced.Count == 1, $"the projects reference SSH.NET {string.Join(", ", referenced)}");

        List<string> stale = [];
        int quoted = 0;
        int quotedInFrench = 0;
        foreach (string path in DocumentsThatQuoteCurrentVersions(root))
        {
            foreach (Match match in DocumentedVersion.Matches(File.ReadAllText(path)))
            {
                quoted++;
                if (Path.GetRelativePath(root, path).Replace('\\', '/').StartsWith("docs/fr/", System.StringComparison.Ordinal))
                {
                    quotedInFrench++;
                }

                if (!referenced.Contains(match.Groups[1].Value))
                {
                    stale.Add($"{Path.GetRelativePath(root, path)} names SSH.NET {match.Groups[1].Value}");
                }
            }
        }

        // Guarding the guard: a sweep that read nothing, or never reached the French mirror,
        // would report success having checked nothing.
        Assert.True(quoted >= 4, $"only {quoted} SSH.NET version(s) were found in the documentation");
        Assert.True(quotedInFrench >= 2, $"only {quotedInFrench} SSH.NET version(s) were found under docs/fr");
        Assert.True(
            stale.Count == 0,
            $"the projects reference SSH.NET {referenced.Single()}:\n" + string.Join("\n", stale));
    }

    [Theory]
    [InlineData("<PackageReference Include=\"SSH.NET\" Version=\"2026.0.0\" />", "2026.0.0")]
    [InlineData("<PackageReference Include=\"SSH.NET\"   Version=\"2099.1.2\"/>", "2099.1.2")]
    public void TheReferencedVersionIsReadFromTheProjectFile(string projectFile, string expected)
    {
        Assert.Equal(expected, ReferencedVersion.Match(projectFile).Groups[1].Value);
    }

    private static HashSet<string> ReferencedVersions(string root)
        => [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project => ReferencedVersion.Matches(File.ReadAllText(project))
                .Select(match => match.Groups[1].Value))];

    private static IEnumerable<string> DocumentsThatQuoteCurrentVersions(string root)
    {
        string docs = Path.Combine(root, "docs");
        List<string> paths =
        [
            .. Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly),
            .. Directory.EnumerateFiles(docs, "*.md", SearchOption.TopDirectoryOnly),
            .. Directory.EnumerateFiles(Path.Combine(docs, "fr"), "*.md", SearchOption.TopDirectoryOnly),
        ];

        // The changelog is history: each entry names the version it was written against.
        return [.. paths.Where(p => !Path.GetFileName(p).StartsWith("CHANGELOG", System.StringComparison.OrdinalIgnoreCase))];
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root from test binary directory: {AppContext.BaseDirectory}");
    }
}
