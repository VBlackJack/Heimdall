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

using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels.Tools;

namespace Heimdall.App.Tests;

/// <summary>
/// The passphrase word lists that ship under <c>Assets/</c>, held to what the generator and the
/// keyboard both require of them.
/// </summary>
/// <remarks>
/// <para>A passphrase is read off a screen and typed on whatever keyboard is in front of the
/// person. That is why the shipped lists carry no accent in any language: the French list writes
/// <c>hamecon</c>, and the Spanish list is built from words that need no accent rather than from
/// accented words with their accents stripped, which would simply be misspelled.</para>
/// <para>The advertised strength is computed as <c>log2(distinct words) * word count</c>, so the
/// size of these files is not cosmetic. A file that fails to load costs six bits per word on the
/// English list, silently, because the generator falls back to a fifty-word array.</para>
/// </remarks>
public sealed class PassphraseWordListGuardTests
{
    /// <summary>The shortest and longest word the loader keeps.</summary>
    private const int MinimumWordLength = 4;
    private const int MaximumWordLength = 12;

    /// <summary>
    /// Floor on any shipped list, well above the fifty-word fallback, so this fails if a list is
    /// ever replaced by its own fallback.
    /// </summary>
    private const int MinimumWords = 400;

    /// <summary>
    /// Floor per list, under what each file ships today and over <see cref="MinimumWords"/>. The
    /// general floor only catches a list that collapsed to its fallback; these catch the English
    /// and French lists being reverted to the five-hundred-word versions they grew from, which
    /// costs nearly three bits per word and would otherwise pass every assertion here in silence.
    /// </summary>
    private static readonly Dictionary<string, int> MinimumWordsPerList = new()
    {
        ["wordlist_en.txt"] = 3000,
        ["wordlist_fr.txt"] = 2500,
        ["wordlist_es.txt"] = 700,
        ["wordlist_la.txt"] = 3000,
    };

    public static TheoryData<string> ShippedWordLists()
    {
        TheoryData<string> data = new();
        foreach (PasswordGeneratorViewModel.PassphraseLanguage language in
                 PasswordGeneratorViewModel.PassphraseLanguages)
        {
            data.Add(language.FileName);
        }

        return data;
    }

    /// <summary>
    /// An empty member-data source runs no rows and reports success, so what the table holds is
    /// asserted here, where a failure is visible.
    /// </summary>
    [Fact]
    public void EveryDeclaredLanguageIsCovered()
    {
        List<string> files = PasswordGeneratorViewModel.PassphraseLanguages
            .Select(language => language.FileName)
            .ToList();

        Assert.Equal(4, files.Count);
        Assert.Contains("wordlist_en.txt", files);
        Assert.Contains("wordlist_fr.txt", files);
        Assert.Contains("wordlist_es.txt", files);
        Assert.Contains("wordlist_la.txt", files);
    }

    /// <summary>
    /// A list absent from <see cref="MinimumWordsPerList"/> would throw rather than assert, and a
    /// floor at or under <see cref="MinimumWords"/> would add nothing to the general floor, so a
    /// shrunk list would read as covered while nothing measured its size.
    /// </summary>
    [Fact]
    public void EveryDeclaredLanguageCarriesItsOwnFloor()
    {
        List<string> unfloored = PasswordGeneratorViewModel.PassphraseLanguages
            .Select(language => language.FileName)
            .Where(fileName => !MinimumWordsPerList.ContainsKey(fileName))
            .ToList();

        Assert.True(
            unfloored.Count == 0,
            "these lists have no floor of their own: " + string.Join(", ", unfloored));

        Assert.Equal(
            PasswordGeneratorViewModel.PassphraseLanguages.Length,
            MinimumWordsPerList.Count);
        Assert.All(MinimumWordsPerList, entry => Assert.True(
            entry.Value > MinimumWords,
            $"{entry.Key} is floored at {entry.Value}, which the general floor already covers"));
    }

    [Theory]
    [MemberData(nameof(ShippedWordLists))]
    public void AWordListHoldsTypeableWordsAndNoRepeats(string fileName)
    {
        string[] words = ReadWordList(fileName);

        Assert.True(
            words.Length >= MinimumWords,
            $"{fileName} holds {words.Length} words, at or below the fallback array's worth");

        int floor = MinimumWordsPerList[fileName];
        Assert.True(
            words.Length >= floor,
            $"{fileName} holds {words.Length} words, under the {floor} it ships, so a passphrase "
            + $"drawn from it is weaker than the tool says");

        List<string> untypeable = words
            .Where(word => !word.All(character => character is >= 'a' and <= 'z'))
            .ToList();
        Assert.True(
            untypeable.Count == 0,
            $"{fileName} holds {untypeable.Count} word(s) that are not plain lowercase ASCII, which "
            + $"cannot be typed on every keyboard a passphrase is retyped on: "
            + string.Join(", ", untypeable.Take(20)));

        List<string> misSized = words
            .Where(word => word.Length is < MinimumWordLength or > MaximumWordLength)
            .ToList();
        Assert.True(
            misSized.Count == 0,
            $"{fileName} holds {misSized.Count} word(s) outside {MinimumWordLength}..{MaximumWordLength}, "
            + $"which the loader drops without saying so: {string.Join(", ", misSized.Take(20))}");

        List<string> repeated = words
            .GroupBy(word => word, System.StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        Assert.True(
            repeated.Count == 0,
            $"{fileName} repeats {repeated.Count} word(s). The loader removes them, so the file "
            + $"overstates the pool it provides: {string.Join(", ", repeated.Take(20))}");
    }

    /// <summary>
    /// Two languages offer two different vocabularies. Every other assertion here reads one file
    /// at a time, so a list copied over another would satisfy all of them: same size, same
    /// alphabet, no repeats inside itself, and a language box offering the same words twice.
    /// </summary>
    /// <remarks>
    /// The threshold is a quarter of the smaller list. What the shipped files actually share is
    /// far below it - 8.3 percent between English and French, 3.3 between English and Latin,
    /// which is English having taken the word from Latin - so this fails on a copy or a merge,
    /// not on the ordinary kinship of neighbouring languages.
    /// </remarks>
    [Fact]
    public void NoTwoWordListsAreTheSameVocabulary()
    {
        const double maximumSharedFraction = 0.25;

        List<string> files = PasswordGeneratorViewModel.PassphraseLanguages
            .Select(language => language.FileName)
            .ToList();

        List<string> tooAlike = [];
        for (int first = 0; first < files.Count; first++)
        {
            for (int second = first + 1; second < files.Count; second++)
            {
                HashSet<string> left = new(ReadWordList(files[first]), System.StringComparer.Ordinal);
                HashSet<string> right = new(ReadWordList(files[second]), System.StringComparer.Ordinal);
                int shared = left.Count(right.Contains);
                double fraction = (double)shared / Math.Min(left.Count, right.Count);

                if (fraction > maximumSharedFraction)
                {
                    tooAlike.Add(
                        $"{files[first]} and {files[second]} share {shared} words, "
                        + $"{fraction:P0} of the smaller");
                }
            }
        }

        Assert.True(tooAlike.Count == 0, string.Join(Environment.NewLine, tooAlike));
    }

    /// <summary>
    /// Each language in the table names a label the interface can actually show, in every language
    /// the interface speaks.
    /// </summary>
    [Fact]
    public void EveryLanguageLabelExistsInEveryCatalogue()
    {
        List<string> missing = [];

        foreach (string path in Directory.GetFiles(Path.Combine(ViewSource.RepoRoot(), "locales"), "*.json"))
        {
            using JsonDocument catalogue = JsonDocument.Parse(File.ReadAllText(path));
            foreach (PasswordGeneratorViewModel.PassphraseLanguage language in
                     PasswordGeneratorViewModel.PassphraseLanguages)
            {
                if (!catalogue.RootElement.TryGetProperty(language.LabelKey, out _))
                {
                    missing.Add($"{Path.GetFileName(path)} has no {language.LabelKey}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "the language box would show the key itself instead of a language name:\n"
            + string.Join("\n", missing));
    }

    /// <summary>
    /// The fallback arrays are what a user gets when the file cannot be read, so they are held to
    /// the same typing rule. They are deliberately not held to the size floor: being small is what
    /// they are for.
    /// </summary>
    [Fact]
    public void EveryFallbackArrayIsTypeableToo()
    {
        List<string> violations = [];

        foreach (PasswordGeneratorViewModel.PassphraseLanguage language in
                 PasswordGeneratorViewModel.PassphraseLanguages)
        {
            Assert.NotEmpty(language.Fallback);

            violations.AddRange(language.Fallback
                .Where(word => !word.All(character => character is >= 'a' and <= 'z')
                    || word.Length is < MinimumWordLength or > MaximumWordLength)
                .Select(word => $"{language.Locale}: {word}"));
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    private static string[] ReadWordList(string fileName)
    {
        string path = Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Assets", fileName);

        Assert.True(File.Exists(path), $"Word list not found: {path}");

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }
}
