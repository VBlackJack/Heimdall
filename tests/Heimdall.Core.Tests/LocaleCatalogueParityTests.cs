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

namespace Heimdall.Core.Tests;

/// <summary>
/// Every catalogue under <c>locales/</c> holds exactly the keys English holds.
/// </summary>
/// <remarks>
/// <para>Until Spanish arrived there was no general parity guard in this repository: every locale
/// test enumerated its own keys by hand, and a key added to English alone would ship silently.
/// The localizer returns the key verbatim when it cannot resolve it, so the failure reaches the
/// user as <c>TreeUxRemoveFilter</c> where the sentence should be. With two catalogues that was a
/// slow leak. With three it is the ordinary case: a lot of work adds keys to the two languages
/// its author speaks.</para>
/// <para>English is the reference because it is the fallback the application ships with and the
/// catalogue every other one is translated from. A key present in a translation and absent from
/// English is caught by the same assertion: it is dead weight no code path can reach.</para>
/// </remarks>
public sealed class LocaleCatalogueParityTests
{
    private const string LocalesDirectoryName = "locales";
    private const string ReferenceFileName = "en.json";

    /// <summary>Lower bound on the catalogues a healthy enumeration returns.</summary>
    private const int MinimumCataloguesEnumerated = 3;

    /// <summary>Lower bound on the keys a healthy read of the reference catalogue returns.</summary>
    private const int MinimumReferenceKeys = 5000;

    /// <summary>Number of key names quoted in a failure message before it is truncated.</summary>
    private const int MaxKeysReported = 40;

    /// <summary>The documents quote the key count rounded down to a multiple of this.</summary>
    private const int DocumentedKeyCountStep = 100;

    /// <summary>Every catalogue that is not the reference one.</summary>
    public static TheoryData<string> TranslatedCatalogues()
    {
        TheoryData<string> data = new();
        foreach (string name in EnumerateCatalogueNames())
        {
            if (!string.Equals(name, ReferenceFileName, StringComparison.Ordinal))
                data.Add(name);
        }

        return data;
    }

    /// <summary>
    /// A member-data source that returns nothing produces no theory rows, and a class with no
    /// rows is reported as passing. The enumeration is asserted here, where a failure is visible.
    /// </summary>
    [Fact]
    public void EveryShippedCatalogueIsCompared()
    {
        List<string> names = EnumerateCatalogueNames().ToList();

        Assert.True(
            names.Count >= MinimumCataloguesEnumerated,
            $"only {names.Count} catalogue(s) were enumerated from {LocalesDirectoryName}/, "
            + "so parity was never compared");
        Assert.Contains(ReferenceFileName, names);
        Assert.Contains("fr.json", names);
        Assert.Contains("es.json", names);
    }

    [Theory]
    [MemberData(nameof(TranslatedCatalogues))]
    public void TranslatedCatalogueHoldsExactlyTheEnglishKeys(string fileName)
    {
        IReadOnlyDictionary<string, string> english = ReadCatalogue(ReferenceFileName);
        IReadOnlyDictionary<string, string> translated = ReadCatalogue(fileName);

        Assert.True(
            english.Count >= MinimumReferenceKeys,
            $"only {english.Count} keys were read from {ReferenceFileName}, so the read failed");

        List<string> missing = english.Keys
            .Where(key => !translated.ContainsKey(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        List<string> orphaned = translated.Keys
            .Where(key => !english.ContainsKey(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} key(s) exist in {ReferenceFileName} and not in {fileName}. A user "
            + "of that language is shown the key itself where the sentence should be:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, missing.Take(MaxKeysReported)));

        Assert.True(
            orphaned.Count == 0,
            $"{orphaned.Count} key(s) exist in {fileName} and not in {ReferenceFileName}, so no "
            + "code path can reach them:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, orphaned.Take(MaxKeysReported)));
    }

    /// <summary>
    /// A placeholder the translator dropped, renamed or invented is a format call that throws or
    /// prints the wrong value at the moment the sentence is shown.
    /// </summary>
    [Theory]
    [MemberData(nameof(TranslatedCatalogues))]
    public void TranslatedCatalogueKeepsTheEnglishPlaceholders(string fileName)
    {
        IReadOnlyDictionary<string, string> english = ReadCatalogue(ReferenceFileName);
        IReadOnlyDictionary<string, string> translated = ReadCatalogue(fileName);

        List<string> divergent = new();
        foreach ((string key, string value) in english)
        {
            if (!translated.TryGetValue(key, out string? other))
                continue;

            SortedSet<string> expected = Placeholders(value);
            SortedSet<string> actual = Placeholders(other);

            if (!expected.SetEquals(actual))
            {
                divergent.Add(
                    $"{key}: {ReferenceFileName} has [{string.Join(", ", expected)}] "
                    + $"and {fileName} has [{string.Join(", ", actual)}]");
            }
        }

        Assert.True(
            divergent.Count == 0,
            $"{divergent.Count} value(s) in {fileName} do not carry the English placeholders:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, divergent.Take(MaxKeysReported)));
    }

    /// <summary>
    /// The documents that state how many keys a catalogue holds, and the separator each one
    /// groups thousands with.
    /// </summary>
    public static TheoryData<string, string> DocumentsStatingTheKeyCount() => new()
    {
        { "docs/FEATURES.md", "," },
        { "docs/SECURITY.md", "," },
        { "docs/DEVELOPMENT.md", "," },
        { "docs/fr/FEATURES.md", " " },
        { "docs/fr/SECURITY.md", " " },
        { "docs/fr/DEVELOPMENT.md", " " },
    };

    /// <summary>
    /// A document that quotes a count quotes a true floor of the count the catalogue has: the
    /// count rounded down to the hundred, followed by a plus sign.
    /// </summary>
    /// <remarks>
    /// Both feature pages claimed 6272 keys and a bilingual interface for four releases after
    /// neither was true. A number in prose has no gate of its own: it is written once, read by
    /// someone deciding whether the project is worth their time, and never measured again. This
    /// measures it. The exact figure used to be quoted, which made every pull request that added
    /// a key edit six documents and conflict with every other one that did the same; the floor
    /// stays true across a few added keys and changes only when the count crosses a hundred.
    /// </remarks>
    [Theory]
    [MemberData(nameof(DocumentsStatingTheKeyCount))]
    public void TheDocumentedKeyCountMatchesTheCatalogue(string relativePath, string separator)
    {
        int count = ReadCatalogue(ReferenceFileName).Count;
        Assert.True(count >= MinimumReferenceKeys, $"only {count} keys were read, so the read failed");

        string path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Document not found: {path}");

        string? problem = FigureProblem(File.ReadAllText(path), count, separator);

        Assert.True(
            problem is null,
            $"{relativePath}: {problem}"
            + " Run scripts/Update-DocumentedKeyCount.ps1 to write the figure into every document.");
    }

    [Theory]
    [InlineData(7421, ",", "7,400+")]
    [InlineData(7400, ",", "7,400+")]
    [InlineData(7399, ",", "7,300+")]
    [InlineData(7421, " ", "7 400+")]
    [InlineData(10005, ",", "10,000+")]
    public void TheDocumentedFigureIsTheCountRoundedDownToTheHundred(int count, string separator, string expected)
    {
        Assert.Equal(expected, DocumentedFigure(count, separator));
    }

    /// <summary>
    /// The check refuses a figure one hundred lower than the floor, and a figure that is exact
    /// instead of rounded: either is a document the script would rewrite.
    /// </summary>
    [Theory]
    [InlineData("holds 7,300+ i18n keys per language", ",")]
    [InlineData("holds 7,500+ i18n keys per language", ",")]
    [InlineData("holds 7,421 i18n keys per language", ",")]
    [InlineData("holds 7,400 i18n keys per language", ",")]
    [InlineData("contient 7 300+ cl\u00e9s i18n par langue", " ")]
    [InlineData("holds no figure at all", ",")]
    public void AFigureThatIsNotTheFloorIsRefused(string text, string separator)
    {
        Assert.NotNull(FigureProblem(text, 7421, separator));
    }

    [Theory]
    [InlineData("holds 7,400+ i18n keys per language", ",")]
    [InlineData("holds 7,400+\nkeys each", ",")]
    [InlineData("contient 7 400+ cl\u00e9s i18n par langue", " ")]
    public void TheFloorOfTheCountIsAccepted(string text, string separator)
    {
        Assert.Null(FigureProblem(text, 7421, separator));
    }

    /// <summary>
    /// The figure as the documents write it: the count rounded down to the hundred, grouped by
    /// <paramref name="separator"/>, with a trailing plus sign.
    /// </summary>
    private static string DocumentedFigure(int count, string separator)
    {
        int floor = count / DocumentedKeyCountStep * DocumentedKeyCountStep;
        return floor.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)
            .Replace(",", separator, StringComparison.Ordinal) + "+";
    }

    /// <summary>
    /// Finds the figure quoted before the word "keys" (or its accented French counterpart, with
    /// an optional "i18n" between) and compares it with the expected one. Null when the document
    /// is right.
    /// </summary>
    private static string? FigureProblem(string text, int count, string separator)
    {
        string separatorPattern = Regex.Escape(separator);
        Regex figure = new(
            @"(?<!\d)\d{1,3}(?:" + separatorPattern + @"\d{3})+\+?(?=\s+(?:i18n\s+)?(?:keys|cl\u00e9s))",
            RegexOptions.CultureInvariant);

        MatchCollection found = figure.Matches(text);
        string expected = DocumentedFigure(count, separator);
        if (found.Count != 1)
        {
            return $"{found.Count} key figure(s) found, expected exactly one, {expected}.";
        }

        return string.Equals(found[0].Value, expected, StringComparison.Ordinal)
            ? null
            : $"quotes {found[0].Value} keys, the catalogue's {count} keys round down to {expected}.";
    }

    /// <summary>
    /// Indexed placeholders only. A brace pair holding anything else is example text, such as the
    /// JSON snippet in the formatter's help page, and is none of this guard's business.
    /// </summary>
    private static SortedSet<string> Placeholders(string value)
    {
        SortedSet<string> found = new(StringComparer.Ordinal);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '{')
                continue;

            int close = value.IndexOf('}', index + 1);
            if (close < 0)
                break;

            string inner = value[(index + 1)..close];
            if (inner.Length > 0 && inner.All(char.IsAsciiDigit))
                found.Add(inner);

            index = close;
        }

        return found;
    }

    private static IReadOnlyDictionary<string, string> ReadCatalogue(string fileName)
    {
        string path = Path.Combine(FindRepoRoot(), LocalesDirectoryName, fileName);
        Assert.True(File.Exists(path), $"Locale file not found: {path}");

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                values[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return values;
    }

    private static IEnumerable<string> EnumerateCatalogueNames()
        => Directory
            .GetFiles(Path.Combine(FindRepoRoot(), LocalesDirectoryName), "*.json")
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)!;

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
}
