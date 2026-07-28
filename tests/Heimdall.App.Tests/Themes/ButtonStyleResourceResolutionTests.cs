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
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Heimdall.App.Tests.Themes;

public sealed partial class ButtonStyleResourceResolutionTests
{
    private static readonly XNamespace PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] FamilyKeySuffixes =
    [
        "Ghost" + "ButtonStyle",
        "Quiet" + "ButtonStyle"
    ];

    [Fact]
    public void GhostAndQuietStyleReferences_ResolveInMergedApplicationResources()
    {
        string repositoryRoot = FindRepoRoot();
        string applicationDirectory = Path.Combine(repositoryRoot, "src", "Heimdall.App");
        string appXamlPath = Path.Combine(applicationDirectory, "App.xaml");
        XDocument appXaml = LoadXaml(appXamlPath);

        HashSet<string> declaredKeys = CollectDeclaredKeys(appXaml);
        foreach (string source in appXaml
            .Descendants(PresentationNamespace + "ResourceDictionary")
            .Select(element => (string?)element.Attribute("Source"))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source!))
        {
            string dictionaryPath = Path.GetFullPath(
                Path.Combine(applicationDirectory, source.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(
                File.Exists(dictionaryPath),
                $"App.xaml merges missing resource dictionary '{source}'.");
            declaredKeys.UnionWith(CollectDeclaredKeys(LoadXaml(dictionaryPath)));
        }

        List<ResourceReference> references = [];
        foreach (string xamlPath in EnumerateSourceFiles(applicationDirectory, "*.xaml"))
        {
            XDocument document = LoadXaml(xamlPath);
            foreach (XAttribute attribute in document.Descendants().Attributes())
            {
                string? key = ExtractResourceKey(attribute.Value);
                if (key is not null && IsFamilyKey(key))
                {
                    references.Add(new ResourceReference(
                        Path.GetRelativePath(repositoryRoot, xamlPath),
                        ((IXmlLineInfo)attribute).LineNumber,
                        key));
                }
            }
        }

        foreach (string sourcePath in EnumerateSourceFiles(applicationDirectory, "*.cs"))
        {
            string[] lines = File.ReadAllLines(sourcePath);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in ResourceLookupRegex().Matches(lines[index]))
                {
                    string key = match.Groups["key"].Value;
                    if (IsFamilyKey(key))
                    {
                        references.Add(new ResourceReference(
                            Path.GetRelativePath(repositoryRoot, sourcePath),
                            index + 1,
                            key));
                    }
                }
            }
        }

        foreach (string suffix in FamilyKeySuffixes)
        {
            Assert.Contains(references, reference => reference.Key.EndsWith(suffix, StringComparison.Ordinal));
        }

        ResourceReference[] unresolved = references
            .Where(reference => !declaredKeys.Contains(reference.Key))
            .ToArray();

        Assert.True(
            unresolved.Length == 0,
            "Unresolved ghost/quiet style resource references:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                unresolved.Select(reference =>
                    $"{reference.File}:{reference.Line}: key '{reference.Key}' is not declared "
                    + "in the resource dictionaries merged by App.xaml.")));
    }

    private static HashSet<string> CollectDeclaredKeys(XDocument document)
    {
        return document
            .Descendants()
            .Select(element => (string?)element.Attribute(XamlNamespace + "Key"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? ExtractResourceKey(string value)
    {
        string trimmed = value.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            return null;
        }

        string[] parts = trimmed[1..^1]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            return null;
        }

        bool isResourceReference =
            string.Equals(parts[0], "DynamicResource", StringComparison.Ordinal)
            || string.Equals(parts[0], "StaticResource", StringComparison.Ordinal);

        return isResourceReference ? parts[1] : null;
    }

    private static bool IsFamilyKey(string key)
    {
        return FamilyKeySuffixes.Any(suffix => key.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root, string pattern)
    {
        return SourceFileEnumeration
            .EnumerateFiles(root, pattern)
            .Order(StringComparer.Ordinal);
    }

    private static XDocument LoadXaml(string path)
    {
        Assert.True(File.Exists(path), $"Missing XAML file: {path}");
        return XDocument.Load(path, LoadOptions.SetLineInfo);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed record ResourceReference(string File, int Line, string Key);

    [GeneratedRegex(@"\b(?:FindResource|TryFindResource)\(\s*""(?<key>[^""]+)""\s*\)")]
    private static partial Regex ResourceLookupRegex();
}
