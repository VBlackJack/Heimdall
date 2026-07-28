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
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// Validates that XAML localization references resolve to keys present in en.json.
/// </summary>
public class XamlLocalizationKeyCoverageTests
{
    private const int MinExpectedKeyReferences = 500;

    private static readonly Regex s_translateKeyRegex = new(
        @"loc:Translate\s+(?:Key\s*=\s*)?([A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    [Fact]
    public void AllXamlTranslateKeys_ExistInEnLocale()
    {
        var repoRoot = FindRepoRoot();
        var enLocale = LoadLocale(repoRoot);
        var references = FindXamlLocalizationReferences(repoRoot);
        var distinctReferencedKeyCount = references
            .Select(reference => reference.Key)
            .Distinct(StringComparer.Ordinal)
            .Count();

        Assert.True(distinctReferencedKeyCount >= MinExpectedKeyReferences,
            $"Expected at least {MinExpectedKeyReferences} distinct loc:Translate key references, " +
            $"but found {distinctReferencedKeyCount}. Repo-root or src discovery likely failed.");

        var missing = references
            .Where(reference => !enLocale.ContainsKey(reference.Key))
            .GroupBy(reference => reference.Key, StringComparer.Ordinal)
            .Select(group => group.OrderBy(reference => reference.RelativePath, StringComparer.Ordinal)
                .ThenBy(reference => reference.LineNumber)
                .First())
            .OrderBy(reference => reference.Key, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Missing XAML localization keys in en.json:" + Environment.NewLine +
            string.Join(Environment.NewLine,
                missing.Select(reference => $"{reference.Key}  <-  {reference.RelativePath}:{reference.LineNumber}")));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
                return dir;

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }

    private static Dictionary<string, string> LoadLocale(string repoRoot)
    {
        var filePath = Path.Combine(repoRoot, "locales", "en.json");

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Locale file not found: {filePath}");

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize en.json");
    }

    private static List<XamlLocalizationReference> FindXamlLocalizationReferences(string repoRoot)
    {
        var srcPath = Path.Combine(repoRoot, "src");
        var references = new List<XamlLocalizationReference>();
        var xamlFiles = SourceFileEnumeration.EnumerateFiles(srcPath, "*.xaml")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in xamlFiles)
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(repoRoot, filePath));
            var lines = File.ReadAllLines(filePath);

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in s_translateKeyRegex.Matches(lines[i]))
                {
                    references.Add(new XamlLocalizationReference(
                        match.Groups[1].Value,
                        relativePath,
                        i + 1));
                }
            }
        }

        return references;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private sealed record XamlLocalizationReference(string Key, string RelativePath, int LineNumber);
}
