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
/// A locale key referenced from C# and absent from the catalogue is shown to the
/// user as its own identifier.
/// </summary>
/// <remarks>
/// <see cref="XamlLocalizationKeyCoverageTests"/> guards the same direction for
/// XAML, and <see cref="DeadLocaleKeyGuardTests"/> guards the reverse one -
/// catalogue keys nothing references. Between them sat the case measured on the
/// SSH auth-diagnosis lot: a key referenced only from C#, never merged into
/// en.json, every suite green, and the failure message the change existed to
/// improve rendered as the literal string of its own key on exactly the path the
/// change was written for.
///
/// <para>
/// References are read from the call shapes the source actually uses, not from a
/// shape invented here: the localizer indexer, a localizer-named delegate or
/// helper invoked with a literal, <c>GetString</c> / <c>Format</c> on a
/// localizer, and constants that hold a key. Keys assembled at runtime are the
/// known false positive, and the literal prefixes they are assembled from are
/// learned from the source the way <see cref="DeadLocaleKeyGuardTests"/> learns
/// them.
/// </para>
///
/// <para>
/// Constants are discovered two ways because this repository holds keys two
/// ways. Most sit in a class whose name ends in Keys; sixteen do not, and seven
/// of those are the <c>MessageKey*</c> constants of
/// <c>SshConnectionProbe</c>. Reading only the first convention made the guard
/// depend on a naming choice: had the class of this lot been called
/// SshAuthFailureMessageKeys - the naming its own assembly already uses - the
/// guard would have been green with three keys shipping raw.
/// </para>
/// </remarks>
public sealed class CSharpLocaleKeyCoverageTests
{
    // Vacuity guards. Measured at the time of writing: 1176 files under src/,
    // and the per-rule counts in RuleFloors below. A discovery that silently
    // breaks returns far less than its floor rather than passing on an empty
    // set.
    private const int MinExpectedSourceFiles = 1000;
    private const int MinExpectedKeyReferences = 900;

    /// <summary>
    /// How each reference was discovered. The rule is carried on the reference
    /// so a floor can be asserted per rule: a single total is inert against
    /// losing any rule but the largest, and the rule that finds constants - the
    /// one that discovers this lot's keys - is the smallest of them.
    /// </summary>
    internal enum DiscoveryRule
    {
        Indexer,
        LocalizerCall,
        LocalizerMethod,
        KeyHolderClass,
        KeyNamedConstant
    }

    // Localizer indexer: _localizer["Key"], Localizer["Key"], loc?["Key"].
    private static readonly Regex s_indexerRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*ocaliz[A-Za-z0-9_]*\s*\??\[\s*""([A-Za-z0-9_]+)""\s*\]",
        RegexOptions.Compiled);

    // A localizer-named delegate or helper called with the key: localize("Key"),
    // LocalizeKey("Key"), LocalizeWindowString("Key").
    private static readonly Regex s_localizerCallRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*ocaliz[A-Za-z0-9_]*\s*\(\s*""([A-Za-z0-9_]+)""\s*[,)]",
        RegexOptions.Compiled);

    // GetString / Format on a localizer: _localizer.GetString("Key").
    private static readonly Regex s_localizerMethodRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*ocaliz[A-Za-z0-9_]*\s*\??\.\s*(?:GetString|Format)\s*\(\s*""([A-Za-z0-9_]+)""\s*[,)]",
        RegexOptions.Compiled);

    // A class that holds locale keys, by the naming this repository actually
    // uses: SshLocalizationKeys, CloseGuardLocaleKeys, SshAuthFailureLocaleKeys,
    // RdpSessionStatusKeys, MessageKeys. Narrowing this to *Locale*Keys made the
    // guard depend on which of the two house namings the author happened to pick.
    private static readonly Regex s_localeKeyClassRegex = new(
        @"\bclass\s+[A-Za-z0-9_]*Keys\b",
        RegexOptions.Compiled);

    // The other convention: a constant named for the key it holds, in a class
    // named for something else. SshConnectionProbe holds seven of these.
    private static readonly Regex s_keyNamedConstantRegex = new(
        @"\bconst\s+string\s+(?:MessageKey|LocaleKey)[A-Za-z0-9_]*\s*=\s*""([A-Za-z0-9_]+)""\s*;",
        RegexOptions.Compiled);

    private static readonly Regex s_stringConstantRegex = new(
        @"\bconst\s+string\s+[A-Za-z0-9_]+\s*=\s*""([A-Za-z0-9_]+)""\s*;",
        RegexOptions.Compiled);

    // Same shape DeadLocaleKeyGuardTests learns its dynamic families from.
    private static readonly Regex s_composedKeyRegex = new(
        @"\$""([A-Z][A-Za-z0-9]*)\{",
        RegexOptions.Compiled);

    [Fact]
    public void EveryLocaleKeyReferencedFromCSharp_ExistsInBothCatalogues()
    {
        string root = RepoRoot();
        IReadOnlyDictionary<string, string> english = LoadCatalogue(root, "en.json");
        IReadOnlyDictionary<string, string> french = LoadCatalogue(root, "fr.json");

        IReadOnlyList<string> files = SourceFiles(root);
        Assert.True(
            files.Count >= MinExpectedSourceFiles,
            $"Only {files.Count} C# files found under src/. Discovery failed, so this guard would "
            + "pass while measuring nothing.");

        List<KeyReference> references = Analyse(Documents(root, files), english.Keys);

        int distinct = references
            .Select(reference => reference.Key)
            .Distinct(StringComparer.Ordinal)
            .Count();
        Assert.True(
            distinct >= MinExpectedKeyReferences,
            $"Only {distinct} distinct locale keys were discovered in C# sources, below the "
            + $"{MinExpectedKeyReferences} floor. The call-shape patterns no longer match this "
            + "codebase, so this guard would pass while measuring nothing.");

        List<string> missing = references
            .Where(reference =>
                !english.ContainsKey(reference.Key) || !french.ContainsKey(reference.Key))
            .GroupBy(reference => reference.Key, StringComparer.Ordinal)
            .Select(group => Describe(group.Key, group.First(), english, french))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} locale key(s) are referenced from C# and missing from a catalogue. "
            + "The user is shown the key itself where the sentence should be. Add them to "
            + "locales/en.json and locales/fr.json:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// A floor per discovery rule, so losing any one of them fails here with the
    /// rule named.
    /// <para>
    /// The single total is inert against three of the five: measured on this
    /// tree, dropping the indexer rule falls below the 900 floor, while dropping
    /// either of the other call shapes, or either constant rule, leaves the
    /// total well above it - including the rules that discover this lot's keys.
    /// A guard that quietly stops discovering reads as coverage, which is worse
    /// than no guard.
    /// </para>
    /// <para>
    /// The shape Theory below tests each regex against a fixture, so it catches
    /// an edit to a pattern. It cannot catch the codebase drifting off the
    /// convention a pattern encodes, which is the realistic failure and the one
    /// already sixteen sites strong. This test is what measures that.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryDiscoveryRule_StillFindsKeysInThisRepository()
    {
        string root = RepoRoot();
        IReadOnlyDictionary<string, string> english = LoadCatalogue(root, "en.json");
        List<KeyReference> references = Analyse(Documents(root, SourceFiles(root)), english.Keys);

        List<string> starved = new();
        foreach ((DiscoveryRule rule, int floor) in RuleFloors)
        {
            int found = references
                .Where(reference => reference.Rule == rule)
                .Select(reference => reference.Key)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (found < floor)
            {
                starved.Add(
                    $"  {rule}: {found} distinct key(s) discovered, below its floor of {floor}.");
            }
        }

        Assert.True(
            starved.Count == 0,
            "A discovery rule stopped finding keys in this repository, so this guard measures less "
            + "than it reports:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, starved));
    }

    /// <summary>
    /// The exemption for runtime-composed families must cover the literal prefix
    /// and nothing else. Exempting everything that starts with a composed prefix
    /// would blow a hole through the family it is meant to protect: the source
    /// composes <c>ErrorSsh</c> + a failure code, and the keys of the SSH
    /// auth-diagnosis lot are all called ErrorSsh-something.
    /// </summary>
    [Fact]
    public void AComposedFamilyExemptsItsPrefixOnly_NotEveryKeyThatSharesIt()
    {
        string[] catalogue = ["ToolDescHTTPHEADERS", "ToolDescSSH"];
        SourceDocument document = new SourceDocument(
            "Fixture.cs",
            """
            var composed = _localizer[$"ToolDesc{descriptor.Id}"];
            var prefixItself = _localizer["ToolDesc"];
            var completeKey = _localizer["ToolDescNotInTheCatalogue"];
            """);

        List<KeyReference> references = Analyse([document], catalogue);
        List<string> keys = references.Select(reference => reference.Key).ToList();

        Assert.DoesNotContain("ToolDesc", keys);
        Assert.Contains("ToolDescNotInTheCatalogue", keys);
    }

    /// <summary>
    /// Each discovery shape is exercised on a fixture, so a regex that stops
    /// matching the codebase fails here with the shape named rather than only
    /// dropping the repository count towards a floor.
    /// </summary>
    [Theory]
    [InlineData("indexer", @"var text = _localizer[""FixtureKeyAlpha""];")]
    [InlineData("nullable indexer", @"var text = _localizer?[""FixtureKeyAlpha""] ?? """";")]
    [InlineData("delegate", @"var text = localize(""FixtureKeyAlpha"");")]
    [InlineData("helper", @"var text = LocalizeKey(""FixtureKeyAlpha"");")]
    [InlineData("GetString", @"var text = _localizationService.GetString(""FixtureKeyAlpha"");")]
    [InlineData("Format", @"var text = _localizer.Format(""FixtureKeyAlpha"", count);")]
    [InlineData("locale key class", """
        internal static class FixtureLocaleKeys
        {
            public const string Alpha = "FixtureKeyAlpha";
        }
        """)]
    [InlineData("key holder class not named for locales", """
        internal static class FixtureMessageKeys
        {
            public const string Alpha = "FixtureKeyAlpha";
        }
        """)]
    [InlineData("key named constant outside a key holder class", """
        internal static class FixtureProbe
        {
            public const string MessageKeyAlpha = "FixtureKeyAlpha";
        }
        """)]
    public void EachDiscoveryShape_FindsTheKeyItIsMeantToFind(string shape, string source)
    {
        List<KeyReference> references = Analyse([new SourceDocument("Fixture.cs", source)], []);

        Assert.True(
            references.Any(reference => reference.Key == "FixtureKeyAlpha"),
            $"The {shape} shape no longer yields a reference.");
    }

    /// <summary>
    /// A string constant outside a locale-key class is not a locale key. Without
    /// this the guard reports brush names, registry values and timeout setting
    /// names as missing translations, and gets switched off.
    /// </summary>
    [Fact]
    public void AStringConstantOutsideALocaleKeyClass_IsNotTakenForALocaleKey()
    {
        SourceDocument document = new SourceDocument(
            "Fixture.cs",
            """
            internal static class ThemeBrushes
            {
                public const string Accent = "AccentBrush";
            }
            """);

        Assert.Empty(Analyse([document], []));
    }

    private static string Describe(
        string key,
        KeyReference reference,
        IReadOnlyDictionary<string, string> english,
        IReadOnlyDictionary<string, string> french)
    {
        List<string> absent = new();
        if (!english.ContainsKey(key))
        {
            absent.Add("en.json");
        }

        if (!french.ContainsKey(key))
        {
            absent.Add("fr.json");
        }

        return $"{key}  <-  {reference.RelativePath}  (missing from {string.Join(" and ", absent)})";
    }

    /// <summary>
    /// Pure over its inputs so the fixtures above can exercise it without a
    /// repository on disk.
    /// </summary>
    internal static List<KeyReference> Analyse(
        IEnumerable<SourceDocument> documents,
        IEnumerable<string> catalogueKeys)
    {
        List<SourceDocument> materialized = documents.ToList();
        List<string> keys = catalogueKeys.ToList();

        // Literal prefixes of keys assembled at runtime, learned from the source
        // rather than hand-listed. Kept only when the prefix actually heads a
        // longer catalogue key, so an unrelated interpolation grants nothing.
        HashSet<string> composedPrefixes = materialized
            .SelectMany(document => s_composedKeyRegex.Matches(document.Text).Cast<Match>())
            .Select(match => match.Groups[1].Value)
            .Where(prefix => keys.Any(key =>
                key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);

        List<KeyReference> references = new();
        foreach (SourceDocument document in materialized)
        {
            foreach ((Regex pattern, DiscoveryRule rule) in new[]
                     {
                         (s_indexerRegex, DiscoveryRule.Indexer),
                         (s_localizerCallRegex, DiscoveryRule.LocalizerCall),
                         (s_localizerMethodRegex, DiscoveryRule.LocalizerMethod)
                     })
            {
                foreach (Match match in pattern.Matches(document.Text))
                {
                    references.Add(
                        new KeyReference(match.Groups[1].Value, document.RelativePath, rule));
                }
            }

            foreach (Match match in s_keyNamedConstantRegex.Matches(document.Text))
            {
                references.Add(new KeyReference(
                    match.Groups[1].Value,
                    document.RelativePath,
                    DiscoveryRule.KeyNamedConstant));
            }

            if (!s_localeKeyClassRegex.IsMatch(document.Text))
            {
                continue;
            }

            foreach (Match match in s_stringConstantRegex.Matches(document.Text))
            {
                references.Add(new KeyReference(
                    match.Groups[1].Value,
                    document.RelativePath,
                    DiscoveryRule.KeyHolderClass));
            }
        }

        // Only the prefix itself is exempt. A complete literal that merely begins
        // with a composed prefix is a real reference to a real key, and the guard
        // must still require it.
        return references
            .Where(reference => !composedPrefixes.Contains(reference.Key))
            .ToList();
    }

    internal sealed record SourceDocument(string RelativePath, string Text);

    internal sealed record KeyReference(string Key, string RelativePath, DiscoveryRule Rule);

    // Measured on this tree: Indexer 658, LocalizerCall 271, LocalizerMethod
    // 242, KeyHolderClass 68, KeyNamedConstant 7 distinct keys. The floors sit
    // under those with room for ordinary churn; a rule that stops matching
    // returns zero and lands far below its own.
    private static readonly (DiscoveryRule Rule, int Floor)[] RuleFloors =
    [
        (DiscoveryRule.Indexer, 600),
        (DiscoveryRule.LocalizerCall, 250),
        (DiscoveryRule.LocalizerMethod, 220),
        (DiscoveryRule.KeyHolderClass, 60),
        (DiscoveryRule.KeyNamedConstant, 5)
    ];

    private static IEnumerable<SourceDocument> Documents(string root, IReadOnlyList<string> files) =>
        files.Select(path => new SourceDocument(RelativePath(root, path), File.ReadAllText(path)));

    private static IReadOnlyList<string> SourceFiles(string root) =>
        SourceFileEnumeration
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static IReadOnlyDictionary<string, string> LoadCatalogue(string root, string fileName)
    {
        string path = Path.Combine(root, "locales", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Locale file not found: {path}", path);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Failed to deserialize {fileName}");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
