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
using System.Text.RegularExpressions;

namespace Heimdall.Core.Tests;

/// <summary>
/// Freezes the language of published release notes: English, and English only.
/// </summary>
/// <remarks>
/// <para>Release notes are published to a GitHub release and shown by the in-app updater, where
/// they are read by people who did not choose a locale. They are the one document category that is
/// not mirrored EN/FR: a French half doubles the maintenance and halves the attention the English
/// half gets, and eight releases shipped with the French half first, which is the order a reader
/// of the English half has to scroll past.</para>
/// <para>Notes older than <see cref="EnglishOnlyFrom"/> predate the rule and are left exactly as
/// they were published. Rewriting a published release body would say something the release itself
/// never said, so the exemption is by version, not by content.</para>
/// <para>This is a language guard, not a typography guard. Which CHARACTERS a note may use is
/// <c>scripts/NotesTypographyGuard.ps1</c>, fail-closed inside <c>Build.ps1 -Mode Release</c>;
/// which LANGUAGE it is written in is here.</para>
/// </remarks>
public sealed class ReleaseNotesLanguageGuardTests
{
    /// <summary>
    /// First version held to the English-only rule. Version strings sort ordinally because the
    /// scheme is fixed-width (<c>v&lt;year&gt;.&lt;MMDDNN&gt;</c>).
    /// </summary>
    private const string EnglishOnlyFrom = "v2026.083101";

    /// <summary>
    /// Number of distinct markers below which a file is not called French. One stray word in a
    /// quoted identifier or a product name is not a French release note; three are.
    /// </summary>
    private const int FrenchMarkerThreshold = 3;

    /// <summary>
    /// Words that occur in ordinary French prose and not in ordinary English prose. Verified
    /// against the whole directory: every English note scores zero, every French one scores seven
    /// or more, so the threshold sits in a gap rather than on a slope.
    /// </summary>
    private static readonly HashSet<string> FrenchMarkers = new(System.StringComparer.Ordinal)
    {
        "cette", "cet", "celui", "celle", "cela", "ceux",
        "dans", "avec", "pour", "sous", "chez",
        "sont", "etait", "était", "etaient", "étaient", "sera", "seront", "etre", "être",
        "qui", "dont", "lorsque", "puisque", "parce",
        "les", "des", "une", "aux", "leur", "leurs",
        "nouvelle", "ancienne", "toujours", "jamais", "desormais", "désormais", "maintenant",
        "ainsi", "donc", "alors", "aussi",
        "chaque", "toute", "toutes", "tous", "plusieurs",
        "vous", "nous", "elle", "elles",
        "peut", "doit", "faire", "rien", "sans",
    };

    // Hyphenated and apostrophed forms are one token on purpose: "sans-serif" is not the French
    // "sans", and "l'utilisateur" is not "les". Joining them removes the only false positives the
    // marker list would otherwise have.
    private static readonly Regex WordPattern = new(
        @"\p{L}+(?:['’-]\p{L}+)*", RegexOptions.Compiled);

    // The bilingual layout the rule replaces: a French language section, French half first.
    private static readonly Regex FrenchHeadingPattern = new(
        @"^#{1,6}\s+Fran(c|ç)ais\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    [Fact]
    public void PublishedReleaseNotesAreEnglishOnly()
    {
        List<string> violations = [];
        int scanned = 0;

        foreach (string path in ReleaseNotes().Where(IsHeldToTheRule))
        {
            scanned++;
            string content = File.ReadAllText(path);
            string name = Path.GetFileName(path);

            if (FrenchHeadingPattern.IsMatch(content))
            {
                violations.Add(
                    $"{name} carries a French language section; release notes are English only.");
            }

            IReadOnlyCollection<string> markers = FrenchMarkersIn(content);
            if (markers.Count >= FrenchMarkerThreshold)
            {
                violations.Add(
                    $"{name} reads as French ({markers.Count} markers: "
                    + $"{string.Join(", ", markers.Order().Take(8))}).");
            }
        }

        // Guarding the guard: a glob that matched nothing would report success having read nothing.
        Assert.True(scanned >= 8, $"only {scanned} release notes were held to the rule");
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>
    /// The detector fires on real French, not only on a sample written to make it fire.
    /// </summary>
    /// <remarks>
    /// The notes below the cutoff are the positive control this guard has and a synthetic string
    /// would not: they are French release notes, written by the same hand, in the same directory,
    /// under the same typography rule. If the marker list ever stops recognizing them, the clean
    /// verdict above stops meaning anything.
    /// </remarks>
    [Fact]
    public void TheDetectorRecognizesTheFrenchNotesItExempts()
    {
        List<string> exempt = [.. ReleaseNotes().Where(path => !IsHeldToTheRule(path))];
        Assert.NotEmpty(exempt);

        int recognized = exempt.Count(
            path => FrenchMarkersIn(File.ReadAllText(path)).Count >= FrenchMarkerThreshold);

        Assert.True(
            recognized >= 10,
            $"only {recognized} of {exempt.Count} exempt notes were recognized as French, "
            + "so the marker list no longer detects what it was measured against.");
    }

    /// <summary>
    /// The exemption covers published history and nothing else.
    /// </summary>
    /// <remarks>
    /// Every note from the cutoff on must be scanned. Without this, adding a file named below the
    /// cutoff, or moving the cutoff forward past a note that was already cleaned, would exempt a
    /// new note silently and the guard above would still pass.
    /// </remarks>
    [Fact]
    public void EveryNoteFromTheCutoffOnIsHeldToTheRule()
    {
        IReadOnlyList<string> notes = ReleaseNotes();
        Assert.All(notes, path => Assert.Matches(@"^v\d{4}\.\d{6}\.md$", Path.GetFileName(path)));

        List<string> exempt =
            [.. notes.Where(path => !IsHeldToTheRule(path)).Select(path => Path.GetFileName(path) ?? path)];

        Assert.All(
            exempt,
            note => Assert.True(
                string.CompareOrdinal(note, EnglishOnlyFrom + ".md") < 0,
                $"{note} is at or after {EnglishOnlyFrom} and must not be exempt."));
    }

    private static IReadOnlyCollection<string> FrenchMarkersIn(string content)
    {
        HashSet<string> found = new(System.StringComparer.Ordinal);
        foreach (Match match in WordPattern.Matches(content))
        {
            string word = match.Value;

            // An all-caps token is an acronym, not a word: DES is a cipher, EST is a time zone.
            if (word.All(c => !char.IsLower(c)))
            {
                continue;
            }

            string lowered = word.ToLowerInvariant();
            if (FrenchMarkers.Contains(lowered))
            {
                found.Add(lowered);
            }
        }

        return found;
    }

    private static bool IsHeldToTheRule(string path)
        => string.CompareOrdinal(Path.GetFileNameWithoutExtension(path), EnglishOnlyFrom) >= 0;

    private static IReadOnlyList<string> ReleaseNotes()
    {
        string directory = Path.Combine(FindRepoRoot(), "docs", "release-notes");
        return [.. Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, System.StringComparer.Ordinal)];
    }

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
