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
/// Guards how the catalogues word a count: by a key pair chosen from the number, not by a
/// "(s)" hack, and with a singular that names its number.
/// </summary>
/// <remarks>
/// <para>
/// A count is worded with two keys, "X" and "XOne", and LocalizationManager.FormatCount picks
/// one by the language's plural rule. The French singular also covers zero, so a "One" variant
/// that said "une session" instead of "{0} session" would read "une session" for none. Every
/// "One" variant therefore carries the same placeholders as its plural.
/// </para>
/// <para>
/// No value may word a count with a "(s)" hack ("13 session(s)", "servidor(es)", "reseau(x)").
/// The baseline that listed the hacks left after the first sweep is gone: it reached zero. A
/// sentence with several counts is composed from counted fragments, each a key pair of its own,
/// rather than pardoned.
/// </para>
/// </remarks>
public sealed class LocaleCountWordingGuardTests
{
    private const string LocalesDirectoryName = "locales";
    private const string OneSuffix = "One";

    /// <summary>Lower bound on the catalogues a healthy enumeration returns.</summary>
    private const int MinimumCataloguesEnumerated = 3;

    /// <summary>Lower bound on the values a healthy catalogue read returns.</summary>
    private const int MinimumValuesPerCatalogue = 5000;

    /// <summary>
    /// A letter followed at once by a parenthesised plural ending: "session(s)", "servidor(es)",
    /// "reseau(x)", "local(aux)", "importe(es)", "Falta(n)".
    /// </summary>
    private static readonly Regex s_countHack = new(
        @"\p{L}\((?:s|es|x|aux|e|n)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_placeholder = new(
        @"\{(\d+)[^}]*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void TheSweepReadsEveryShippedCatalogue()
    {
        Dictionary<string, Dictionary<string, string>> catalogues = ReadCatalogues();

        Assert.True(
            catalogues.Count >= MinimumCataloguesEnumerated,
            $"only {catalogues.Count} catalogue(s) were read from {LocalesDirectoryName}/");
        foreach (string name in new[] { "en.json", "fr.json", "es.json" })
        {
            Assert.True(catalogues.ContainsKey(name), $"{name} was not read");
            Assert.True(
                catalogues[name].Count >= MinimumValuesPerCatalogue,
                $"{name} yielded {catalogues[name].Count} values; the read failed");
        }
    }

    /// <summary>Positive control for the hack pattern: every ending the catalogues used, and no word.</summary>
    [Theory]
    [InlineData("13 session(s)", true)]
    [InlineData("{0} servidor(es)", true)]
    [InlineData("{0} sous-r\u00e9seau(x) d\u00e9tect\u00e9(s)", true)]
    [InlineData("{0} lien(s) local(aux)", true)]
    [InlineData("{0} import\u00e9(es)", true)]
    [InlineData("Falta(n) registros", true)]
    [InlineData("13 sessions", false)]
    [InlineData("Ctrl+Shift+S (screenshot)", false)]
    [InlineData("Port (TCP)", false)]
    public void TheHackPatternRecognisesParenthesisedPluralEndings(string value, bool expected)
    {
        Assert.Equal(expected, s_countHack.IsMatch(value));
    }

    [Fact]
    public void NoLocaleValueWordsACountWithAParenthesisedPlural()
    {
        List<string> offenders = FindCountHacks(ReadCatalogues());

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} value(s) word a count with a parenthesised plural. Give the key a "
            + "\"One\" sibling and format it with LocalizationManager.FormatCount; a sentence with "
            + "several counts is composed from counted fragments:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Positive control for the sweep: a hack planted in one catalogue of a healthy set is
    /// reported with its catalogue and key, and a clean catalogue reports nothing.
    /// </summary>
    [Fact]
    public void TheSweepReportsAHackPlantedInACatalogue()
    {
        Dictionary<string, Dictionary<string, string>> catalogues = new(StringComparer.Ordinal)
        {
            ["en.json"] = new(StringComparer.Ordinal) { ["Clean"] = "{0} sessions" },
            ["fr.json"] = new(StringComparer.Ordinal)
            {
                ["Clean"] = "{0} sessions",
                ["Planted"] = "{0} session(s) ouverte(s)",
            },
        };

        Assert.Equal("fr.json::Planted", Assert.Single(FindCountHacks(catalogues)));

        catalogues["fr.json"].Remove("Planted");
        Assert.Empty(FindCountHacks(catalogues));
    }

    /// <summary>
    /// Positive control for the pair check: a singular that drops its number is reported, one
    /// that keeps it is not, and a "One" key without a plural sibling is not a pair.
    /// </summary>
    [Fact]
    public void ThePairCheckReportsASingularThatDropsItsNumber()
    {
        Dictionary<string, string> catalogue = new(StringComparer.Ordinal)
        {
            ["Sessions"] = "{0} sessions",
            ["SessionsOne"] = "une session",
            ["Tunnels"] = "{0} tunnels",
            ["TunnelsOne"] = "{0} tunnel",
            ["StandaloneOne"] = "no sibling",
        };

        List<string> violations = FindPairViolations("test.json", catalogue);

        Assert.Equal("test.json::SessionsOne", Assert.Single(violations).Split(' ')[0]);
    }

    [Fact]
    public void EverySingularNamesTheNumbersItsPluralNames()
    {
        List<string> violations = [];
        int pairCount = 0;
        foreach ((string name, Dictionary<string, string> catalogue) in ReadCatalogues())
        {
            pairCount += catalogue.Keys.Count(key => IsPairedSingular(catalogue, key));
            violations.AddRange(FindPairViolations(name, catalogue));
        }

        Assert.True(pairCount > 0, "no X / XOne pair was found, so nothing was checked");
        Assert.True(
            violations.Count == 0,
            $"{violations.Count} singular variant(s) do not name the numbers their plural names. "
            + "French uses the singular for zero too, so it must say the number:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static List<string> FindPairViolations(string name, Dictionary<string, string> catalogue)
    {
        List<string> violations = [];
        foreach (string key in catalogue.Keys.Where(key => IsPairedSingular(catalogue, key)))
        {
            string plural = catalogue[key[..^OneSuffix.Length]];
            SortedSet<string> pluralSlots = Placeholders(plural);
            SortedSet<string> singularSlots = Placeholders(catalogue[key]);
            if (!pluralSlots.SetEquals(singularSlots))
            {
                violations.Add(
                    $"{name}::{key} names {{{string.Join(",", singularSlots)}}}, "
                    + $"its plural names {{{string.Join(",", pluralSlots)}}}");
            }
        }

        return violations;
    }

    private static bool IsPairedSingular(Dictionary<string, string> catalogue, string key) =>
        key.Length > OneSuffix.Length
        && key.EndsWith(OneSuffix, StringComparison.Ordinal)
        && catalogue.ContainsKey(key[..^OneSuffix.Length]);

    private static SortedSet<string> Placeholders(string value) =>
        new(s_placeholder.Matches(value).Select(match => match.Groups[1].Value), StringComparer.Ordinal);

    private static List<string> FindCountHacks(Dictionary<string, Dictionary<string, string>> catalogues) =>
        catalogues
            .SelectMany(catalogue => catalogue.Value
                .Where(entry => s_countHack.IsMatch(entry.Value))
                .Select(entry => $"{catalogue.Key}::{entry.Key}"))
            .OrderBy(offender => offender, StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, Dictionary<string, string>> ReadCatalogues()
    {
        Dictionary<string, Dictionary<string, string>> catalogues = new(StringComparer.Ordinal);
        foreach (string path in Directory
            .GetFiles(Path.Combine(FindRepoRoot(), LocalesDirectoryName), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    values[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            catalogues[Path.GetFileName(path)] = values;
        }

        return catalogues;
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
}
