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
/// The "(s)" hacks ("13 session(s)", "servidor(es)", "reseau(x)") that remain sit in the
/// baseline beside this file. It may only shrink: a new hack fails, and a hack that was fixed
/// or deleted must leave the list, or the line would pardon the next key of that name.
/// </para>
/// </remarks>
public sealed class LocaleCountWordingGuardTests
{
    private const string LocalesDirectoryName = "locales";
    private const string BaselineFileName = "locale-count-hacks.baseline.txt";
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
    public void NoLocaleValueGainsACountHackOutsideTheBaseline()
    {
        HashSet<string> baseline = ReadBaseline();
        List<string> unexpected = KeysWithCountHacks()
            .Where(key => !baseline.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"{unexpected.Count} key(s) word a count with a parenthesised plural. Give the key a "
            + "\"One\" sibling and format it with LocalizationManager.FormatCount:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unexpected));
    }

    [Fact]
    public void TheBaselineHoldsNoKeyThatLostItsHack()
    {
        HashSet<string> current = KeysWithCountHacks();
        List<string> stale = ReadBaseline()
            .Where(key => !current.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} baseline entries no longer carry a hack. Delete these lines from "
            + BaselineFileName + ":"
            + Environment.NewLine
            + string.Join(Environment.NewLine, stale));
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

    private static HashSet<string> KeysWithCountHacks() =>
        ReadCatalogues()
            .SelectMany(pair => pair.Value)
            .Where(entry => s_countHack.IsMatch(entry.Value))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);

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

    private static HashSet<string> ReadBaseline() =>
        File.ReadAllLines(Path.Combine(FindRepoRoot(), "tests", "Heimdall.Core.Tests", BaselineFileName))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

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
