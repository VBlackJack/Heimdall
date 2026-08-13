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

    private const int MinExpectedScopedKeys = 100;

    private static readonly Regex s_translateKeyRegex = new(
        @"loc:Translate\s+(?:Key\s*=\s*)?([A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    private static readonly Regex s_identifierRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*",
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

    /// <summary>
    /// Reverse direction of <see cref="AllXamlTranslateKeys_ExistInEnLocale"/>, scoped to the
    /// Sftp/FileBrowser prefixes: every key in that perimeter must be referenced somewhere
    /// outside <c>locales/</c>. Deliberately NOT repository-wide - the same measurement over the
    /// whole catalogue reports well over a thousand unreferenced keys, so a generic guard would
    /// be red on its first run and would turn a cleanup into a multi-lot programme.
    /// </summary>
    [Fact]
    public void SftpAndFileBrowserLocaleKeys_AreReferencedOutsideTheLocaleFiles()
    {
        var repoRoot = FindRepoRoot();
        var scopedKeys = LoadLocale(repoRoot).Keys
            .Where(IsScopedKey)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(scopedKeys.Count >= MinExpectedScopedKeys,
            $"Expected at least {MinExpectedScopedKeys} Sftp/FileBrowser keys in en.json, found " +
            $"{scopedKeys.Count}. Locale discovery likely failed.");

        var referencedTokens = CollectSourceTokens(repoRoot);
        var orphans = scopedKeys
            .Where(key => !referencedTokens.Contains(key))
            .ToList();

        Assert.True(orphans.Count == 0,
            "Sftp/FileBrowser locale keys with no reference outside locales/:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, orphans));
    }

    /// <summary>
    /// The two catalogues must agree on this perimeter, so a deletion can never be applied to one
    /// file and forgotten in the other. Nothing else in the repository guards this scope.
    /// </summary>
    [Fact]
    public void SftpAndFileBrowserLocaleKeys_AreIdenticalInBothLocales()
    {
        var repoRoot = FindRepoRoot();
        var english = LoadLocaleFile(repoRoot, "en.json").Keys.Where(IsScopedKey).ToHashSet(StringComparer.Ordinal);
        var french = LoadLocaleFile(repoRoot, "fr.json").Keys.Where(IsScopedKey).ToHashSet(StringComparer.Ordinal);

        var missingInFrench = english.Except(french, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList();
        var missingInEnglish = french.Except(english, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList();

        Assert.True(
            missingInFrench.Count == 0 && missingInEnglish.Count == 0,
            "Sftp/FileBrowser locale keys must exist in both catalogues." +
            Environment.NewLine + "Missing from fr.json: " + string.Join(", ", missingInFrench) +
            Environment.NewLine + "Missing from en.json: " + string.Join(", ", missingInEnglish));
    }

    private static bool IsScopedKey(string key)
        => key.StartsWith("Sftp", StringComparison.Ordinal)
            || key.StartsWith("FileBrowser", StringComparison.Ordinal);

    /// <summary>
    /// Whole-word tokens of every C# and XAML source under src/ and tests/. Word-bounded by
    /// construction, so a longer identifier that merely begins with a shorter key's name is never
    /// counted as a reference to it - which a substring scan would get wrong. Keys are also
    /// consumed from C# through the localizer indexer, so restricting this sweep to XAML would
    /// report live keys as orphans.
    /// </summary>
    /// <remarks>
    /// Never name a locale key literally in this file: the sweep reads its own source, so a key
    /// quoted in a comment here would count as a reference to itself and hide a real orphan.
    /// </remarks>
    private static HashSet<string> CollectSourceTokens(string repoRoot)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in new[] { Path.Combine(repoRoot, "src"), Path.Combine(repoRoot, "tests") })
        {
            foreach (var pattern in new[] { "*.cs", "*.xaml" })
            {
                foreach (var filePath in SourceFileEnumeration.EnumerateFiles(root, pattern))
                {
                    foreach (Match match in s_identifierRegex.Matches(File.ReadAllText(filePath)))
                    {
                        tokens.Add(match.Value);
                    }
                }
            }
        }

        return tokens;
    }

    private static Dictionary<string, string> LoadLocaleFile(string repoRoot, string fileName)
    {
        var filePath = Path.Combine(repoRoot, "locales", fileName);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Locale file not found: {filePath}");

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath))
            ?? throw new InvalidOperationException($"Failed to deserialize {fileName}");
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
