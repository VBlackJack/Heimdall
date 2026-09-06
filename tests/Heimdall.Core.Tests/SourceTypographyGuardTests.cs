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
using System.Text.Json;

namespace Heimdall.Core.Tests;

/// <summary>
/// Freezes which characters product sources and the shipped locale catalogues may use.
/// </summary>
/// <remarks>
/// <para>This is the same rule <see cref="DocumentationTypographyGuardTests"/> applies to public
/// documentation, applied to the other two places a character reaches a user: the C# and XAML
/// sources under <c>src/</c>, and the localized strings in <c>locales/en.json</c> and
/// <c>locales/fr.json</c>. A plain ASCII character says the same thing and survives a Windows
/// terminal, a diff, a console code page and a CI log; a typographic substitute does not.</para>
/// <para>Arrows, box-drawing characters, ballot boxes, emoji and every French accent are NOT
/// refused. An accent is an accent, not typography. See <see cref="AccentsAndArrowsAreNotRefused"/>,
/// which exists because an allow-list and a remedy table both read as a list of code points, and
/// confusing the two yields a banned set that includes the accents themselves.</para>
/// <para>The refusal is on the character as it appears to a reader, so locale values are compared
/// after JSON unescaping: the catalogues mix literal UTF-8 with <c>\uXXXX</c> escapes, and a raw
/// byte scan of <c>en.json</c> sees 11 of its 121 em dashes. C# sources are scanned raw on purpose:
/// a <c>'×'</c> escape in source is ASCII in the file, survives every pipe, and is the
/// supported way to keep a non-ASCII character in a parser's input alphabet.</para>
/// <para>The scan is recursive, and <see cref="TheScanReachesSubdirectories"/> asserts that it
/// actually reaches one. A sweep that silently stops at the top directory reports success having
/// read almost nothing, which is how a whole typography campaign once passed green.</para>
/// </remarks>
public sealed class SourceTypographyGuardTests
{
    /// <summary>
    /// Every refused character, with the ASCII form to write instead. This is a refusal table:
    /// it grants nothing, and anything absent from it is allowed.
    /// </summary>
    private static readonly Dictionary<char, string> Refused = new()
    {
        ['—'] = "em dash, use the ASCII hyphen -",
        ['–'] = "en dash, use the ASCII hyphen -",
        ['‐'] = "unicode hyphen, use the ASCII hyphen -",
        ['‑'] = "non-breaking hyphen, use the ASCII hyphen -",
        ['−'] = "minus sign, use the ASCII hyphen -",
        ['“'] = "left curly quote, use the ASCII double quote",
        ['”'] = "right curly quote, use the ASCII double quote",
        ['„'] = "low double quote, use the ASCII double quote",
        ['‘'] = "left curly apostrophe, use the ASCII apostrophe",
        ['’'] = "right curly apostrophe, use the ASCII apostrophe",
        ['«'] = "left guillemet, not on the AZERTY layout, use the ASCII double quote",
        ['»'] = "right guillemet, not on the AZERTY layout, use the ASCII double quote",
        ['œ'] = "oe ligature, not on the AZERTY layout, write oe",
        ['Œ'] = "OE ligature, not on the AZERTY layout, write OE",
        ['æ'] = "ae ligature, not on the AZERTY layout, write ae",
        ['Æ'] = "AE ligature, not on the AZERTY layout, write AE",
        ['…'] = "single-character ellipsis, write three dots",
        ['×'] = "multiplication sign, write the letter x",
        [' '] = "no-break space, use a plain space",
        [' '] = "narrow no-break space, use a plain space",
        [' '] = "thin space, use a plain space",
        ['​'] = "zero-width space, delete it",
        ['﻿'] = "byte order mark, delete it",
    };

    private const string LocalesDirectoryName = "locales";
    private const string SourceDirectoryName = "src";

    /// <summary>
    /// Lower bound on the number of source files a healthy sweep opens. Guards the guard: a glob
    /// that matched nothing would otherwise report success having read nothing at all.
    /// </summary>
    private const int MinimumSourceFilesScanned = 800;

    /// <summary>Lower bound on the number of locale values a healthy sweep reads.</summary>
    private const int MinimumLocaleValuesScanned = 5000;

    /// <summary>Number of violations quoted in a failure message before it is truncated.</summary>
    private const int MaxViolationsReported = 40;

    [Fact]
    public void ProductSourcesUseNoTypographicSubstitutes()
    {
        List<string> violations = [];
        int scanned = 0;

        foreach (string path in ProductSources())
        {
            scanned++;
            string[] lines = File.ReadAllLines(path);
            for (int index = 0; index < lines.Length; index++)
            {
                foreach (char character in lines[index])
                {
                    if (Refused.TryGetValue(character, out string? remedy))
                    {
                        violations.Add(
                            $"{Relative(path)}:{index + 1} contains U+{(int)character:X4} ({remedy})");
                    }
                }
            }
        }

        Assert.True(
            scanned >= MinimumSourceFilesScanned,
            $"only {scanned} product sources were scanned");
        Assert.True(
            violations.Count == 0,
            $"{violations.Count} typographic substitute(s) in product sources:\n"
            + string.Join("\n", violations.Take(MaxViolationsReported)));
    }

    /// <summary>
    /// The raw scan above deliberately leaves <c>\uXXXX</c> escapes alone, because a parser
    /// alphabet may need one. A dash is never a parser alphabet: twenty-five escaped em dashes
    /// reached users through fallback strings and placeholders while every literal guard stayed
    /// green.
    /// </summary>
    [Fact]
    public void ProductSourcesUseNoEscapedDashes()
    {
        string[] escapes = ["\\u2013", "\\u2014", "\\u2212", "\\u2010", "\\u2011"];
        List<string> violations = [];
        int scanned = 0;

        foreach (string path in ProductSources())
        {
            scanned++;
            string[] lines = File.ReadAllLines(path);
            for (int index = 0; index < lines.Length; index++)
            {
                foreach (string escape in escapes)
                {
                    if (lines[index].Contains(escape, System.StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"{Relative(path)}:{index + 1} contains the escape {escape}");
                    }
                }
            }
        }

        Assert.True(scanned >= MinimumSourceFilesScanned, $"only {scanned} product sources were scanned");
        Assert.True(
            violations.Count == 0,
            $"{violations.Count} escaped dash(es) in product sources:\n"
            + string.Join("\n", violations.Take(MaxViolationsReported)));
    }

    [Theory]
    [InlineData("en.json")]
    [InlineData("fr.json")]
    public void LocaleValuesUseNoTypographicSubstitutes(string fileName)
    {
        string localePath = Path.Combine(FindRepoRoot(), LocalesDirectoryName, fileName);
        Assert.True(File.Exists(localePath), $"Locale file not found: {localePath}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(localePath));

        List<string> violations = [];
        int scanned = 0;

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            string value = property.Value.GetString() ?? string.Empty;
            scanned++;

            for (int index = 0; index < value.Length; index++)
            {
                if (Refused.TryGetValue(value[index], out string? remedy))
                {
                    violations.Add(
                        $"{fileName}::{property.Name} contains U+{(int)value[index]:X4} "
                        + $"at position {index} ({remedy})");
                }
            }
        }

        Assert.True(
            scanned >= MinimumLocaleValuesScanned,
            $"only {scanned} values were read from {fileName}");
        Assert.True(
            violations.Count == 0,
            $"{violations.Count} typographic substitute(s) in {fileName}:\n"
            + string.Join("\n", violations.Take(MaxViolationsReported)));
    }

    /// <summary>
    /// The sweep must reach sources nested below a project directory, not just the few files that
    /// sit at the top of <c>src/</c>.
    /// </summary>
    [Fact]
    public void TheScanReachesSubdirectories()
    {
        IReadOnlyList<string> sources = ProductSources();

        bool anyNested = sources.Any(path =>
        {
            string relative = Relative(path);
            return relative.StartsWith(SourceDirectoryName, System.StringComparison.Ordinal)
                && relative.Count(c => c is '/' or '\\') >= 3;
        });

        Assert.True(
            anyNested,
            "No source below a second-level directory of src/ was scanned, so the sweep is not recursive.");
    }

    /// <summary>
    /// Accents are accents, and arrows carry something no pair of ASCII characters carries. Both
    /// stay. Asserted rather than left to a comment, because the refusal table above and an
    /// allow-list read alike, and reading one as the other bans the French language.
    /// </summary>
    [Fact]
    public void AccentsAndArrowsAreNotRefused()
    {
        char[] welcome =
        [
            'é', 'è', 'ê', 'ë', 'à', 'â', 'ç', 'î', 'ï', 'ô', 'ù', 'û', 'É', 'À', 'Ç',
            '→', '←', '↑', '↓', '↔', '├', '│', '└', '─', '☐', '☑', '•',
        ];

        foreach (char character in welcome)
        {
            Assert.False(
                Refused.ContainsKey(character),
                $"U+{(int)character:X4} is not a typographic substitute for an ASCII character and is allowed.");
        }
    }

    private static IReadOnlyList<string> ProductSources()
    {
        string sourceRoot = Path.Combine(FindRepoRoot(), SourceDirectoryName);

        return
        [
            .. Directory
                .EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
                .Where(IsProductSource)
                .Where(path => !IsUnderBuildOutput(path, sourceRoot))
                .OrderBy(path => path, System.StringComparer.Ordinal),
        ];
    }

    private static bool IsProductSource(string path)
        => path.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xaml", System.StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderBuildOutput(string path, string sourceRoot)
        => Path.GetRelativePath(sourceRoot, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                string.Equals(segment, "bin", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "obj", System.StringComparison.OrdinalIgnoreCase));

    private static string Relative(string path)
        => Path.GetRelativePath(FindRepoRoot(), path);

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
