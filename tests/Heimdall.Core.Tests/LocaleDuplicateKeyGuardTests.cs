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
using System.Text;
using System.Text.Json;

namespace Heimdall.Core.Tests;

/// <summary>
/// Guards locale source files against duplicate JSON property names.
/// Duplicate properties are accepted by the runtime parser, but only the last
/// value remains reachable after deserialization to a dictionary.
/// </summary>
public sealed class LocaleDuplicateKeyGuardTests
{
    [Fact]
    public void JsonDocument_EnumerateObject_PreservesDuplicateProperties()
    {
        const string json = """{"Probe":"first","Probe":"last"}""";
        using JsonDocument document = JsonDocument.Parse(json);

        JsonProperty[] properties = document.RootElement.EnumerateObject().ToArray();

        Assert.Equal(2, properties.Length);
        Assert.Equal(new[] { "Probe", "Probe" }, properties.Select(property => property.Name));
        Assert.Equal(new[] { "first", "last" }, properties.Select(property => property.Value.GetString()));
    }

    [Fact]
    public void LocaleFiles_DoNotContainDuplicateKeys()
    {
        string repoRoot = FindRepoRoot();
        string localesPath = Path.Combine(repoRoot, "locales");
        string[] localeFiles = Directory.GetFiles(
            localesPath,
            "*.json",
            SearchOption.AllDirectories);

        List<string> violations = new();
        foreach (string localeFile in localeFiles.OrderBy(path => path, StringComparer.Ordinal))
        {
            string relativePath = Path.GetRelativePath(repoRoot, localeFile)
                .Replace(Path.DirectorySeparatorChar, '/');
            byte[] utf8Json = File.ReadAllBytes(localeFile);
            IReadOnlyList<PropertyOccurrence> occurrences = EnumerateRootProperties(utf8Json);

            foreach (IGrouping<string, PropertyOccurrence> duplicate in occurrences
                         .GroupBy(occurrence => occurrence.Name, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                string lineNumbers = string.Join(
                    ", ",
                    duplicate.Select(occurrence => occurrence.LineNumber));
                violations.Add(
                    $"  {relativePath}: duplicate key '{duplicate.Key}' at lines {lineNumbers}");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Found {violations.Count} duplicated locale key(s):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static IReadOnlyList<PropertyOccurrence> EnumerateRootProperties(
        ReadOnlySpan<byte> utf8Json)
    {
        Utf8JsonReader reader = new(utf8Json);
        List<PropertyOccurrence> occurrences = new();
        int currentLine = 1;
        int scannedOffset = 0;

        while (reader.Read())
        {
            int tokenStart = checked((int)reader.TokenStartIndex);
            while (scannedOffset < tokenStart)
            {
                if (utf8Json[scannedOffset] == (byte)'\n')
                    currentLine++;

                scannedOffset++;
            }

            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                occurrences.Add(new PropertyOccurrence(
                    reader.GetString()
                        ?? throw new JsonException("Locale property name cannot be null."),
                    currentLine));
            }
        }

        return occurrences;
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
                return dir;

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }

    private sealed record PropertyOccurrence(string Name, int LineNumber);
}
