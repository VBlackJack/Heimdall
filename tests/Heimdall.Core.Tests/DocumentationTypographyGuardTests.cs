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

namespace Heimdall.Core.Tests;

/// <summary>
/// Freezes which characters public documentation may use.
/// </summary>
/// <remarks>
/// <para>Two kinds of character are refused, and both for the same reason: a plain ASCII character
/// says the same thing and survives a Windows terminal, a diff, a console code page and a CI log.
/// The first kind is the punctuation an editor inserts on its own. The second is a typographic
/// substitute for something the author could have typed: the French guillemets and the ligatures,
/// which <c>scripts/NotesTypographyGuard.ps1</c> lists as deliberately absent from the AZERTY
/// layout.</para>
/// <para>Arrows, box-drawing characters, ballot boxes and emoji are NOT refused. A dependency graph,
/// a directory tree and a checklist read better with them, and they carry something no pair of ASCII
/// characters carries as clearly. That is a deliberate exception, and it is the difference between
/// this rule and the stricter one the release notes are held to.</para>
/// <para>The scan is recursive on purpose. A non-recursive sweep of <c>docs/*.md</c> once passed
/// while <c>docs/repro/</c> and <c>docs/audits/</c> went unexamined, which is why
/// <see cref="TheScanReachesSubdirectories"/> exists.</para>
/// </remarks>
public sealed class DocumentationTypographyGuardTests
{
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
        [' '] = "no-break space, use a plain space",
        [' '] = "narrow no-break space, use a plain space",
        [' '] = "thin space, use a plain space",
        ['​'] = "zero-width space, delete it",
        ['﻿'] = "byte order mark, delete it",
    };

    [Fact]
    public void PublicDocumentationUsesNoTypographicSubstitutes()
    {
        List<string> violations = [];
        int scanned = 0;

        foreach (string path in PublicDocuments())
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

        // Guarding the guard: a glob that matched nothing would report success having read nothing.
        Assert.True(scanned >= 20, $"only {scanned} public documents were scanned");
        Assert.True(violations.Count == 0, string.Join("\n", violations.Take(40)));
    }

    /// <summary>
    /// The scan must reach documents in subdirectories of <c>docs/</c>.
    /// </summary>
    /// <remarks>
    /// This is not hypothetical. A hand-run sweep of <c>docs/*.md</c> reported a clean result while
    /// two subdirectories were never opened, and the miss was only found later by reading the tree
    /// rather than trusting the sweep.
    /// </remarks>
    [Fact]
    public void TheScanReachesSubdirectories()
    {
        IReadOnlyList<string> documents = PublicDocuments();

        bool anyNested = documents.Any(path =>
        {
            string relative = Relative(path);
            return relative.StartsWith("docs", System.StringComparison.Ordinal)
                && relative.Count(c => c is '/' or '\\') >= 2;
        });

        Assert.True(
            anyNested,
            "No document below a docs subdirectory was scanned, so the sweep is not recursive.");
    }

    /// <summary>
    /// Release notes are held to the strict rule: printable ASCII and what the French AZERTY layout
    /// types, arrows included. The general documentation guard above lets arrows through; a release
    /// note is read from a release page and a console updater, where they do not survive.
    /// </summary>
    /// <remarks>
    /// The allow-list is read from <c>scripts/NotesTypographyGuard.ps1</c>, the guard that blocks
    /// <c>Build.ps1 -Mode Release</c>, instead of being copied here: two copies of one decision drift
    /// apart. Both languages are scanned, <c>docs/release-notes</c> and its French mirror
    /// <c>docs/fr/release-notes</c>.
    /// </remarks>
    [Fact]
    public void ReleaseNotesUseOnlyAsciiAndAzertyCharacters()
    {
        HashSet<char> allowed = ReadReleaseNotesAllowList();
        List<string> violations = [];
        IReadOnlyList<string> notes = ReleaseNoteFiles();

        foreach (string path in notes)
        {
            // Read with byte order mark detection off, so that a leading U+FEFF is seen and refused.
            using StreamReader reader = new(path, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            int lineNumber = 0;
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                foreach (char character in line.Where(c => !allowed.Contains(c)))
                {
                    violations.Add(
                        $"{Relative(path)}:{lineNumber} contains U+{(int)character:X4}, "
                        + "which is neither printable ASCII nor typeable on the French AZERTY layout");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations.Take(40)));
        Assert.True(notes.Count >= 100, $"only {notes.Count} release notes were scanned");
    }

    /// <summary>
    /// The French mirror sits one directory below <c>docs/</c>; a sweep that stopped at the English
    /// folder would report success having read half the notes.
    /// </summary>
    [Fact]
    public void TheReleaseNoteScanReachesTheFrenchMirror()
    {
        IReadOnlyList<string> notes = ReleaseNoteFiles();

        Assert.Contains(notes, path => Relative(path).Replace('\\', '/').StartsWith("docs/release-notes/", System.StringComparison.Ordinal));
        Assert.Contains(notes, path => Relative(path).Replace('\\', '/').StartsWith("docs/fr/release-notes/", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The allow-list read from the PowerShell guard is the AZERTY one, not an empty or a permissive
    /// set: the accents are in, and the arrows, the guillemets and the ligatures are out.
    /// </summary>
    [Fact]
    public void TheReleaseNoteAllowListIsTheAzertyOne()
    {
        HashSet<char> allowed = ReadReleaseNotesAllowList();

        foreach (char accent in new[] { 'é', 'è', 'ç', 'à', 'ù', 'ê', 'î', 'ô', 'û', 'ï', 'ë', 'ü', 'ñ' })
            Assert.Contains(accent, allowed);

        foreach (char refused in new[] { '→', '←', '↔', '—', '–', '«', '»', 'œ', 'æ', '…', '’', ' ', '﻿', '☐', '×' })
            Assert.DoesNotContain(refused, allowed);

        Assert.Contains('A', allowed);
        Assert.Contains('~', allowed);
        Assert.Contains('\t', allowed);
    }

    private const string NotesGuardScript = "scripts/NotesTypographyGuard.ps1";
    private const string NotesAllowListVariable = "$script:AzertyNonAsciiChars";

    /// <summary>Tab, printable ASCII, and the non-ASCII code points listed in the PowerShell guard.</summary>
    private static HashSet<char> ReadReleaseNotesAllowList()
    {
        string[] lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), NotesGuardScript));
        int start = Array.FindIndex(lines, line => line.StartsWith(NotesAllowListVariable + " = @(", System.StringComparison.Ordinal));
        Assert.True(start >= 0, $"{NotesAllowListVariable} was not found in {NotesGuardScript}");

        HashSet<char> allowed = ['\t'];
        for (char c = ' '; c <= '~'; c++)
            allowed.Add(c);

        // Only the array body is read. The allow-list and the remedy table further down both list
        // code points as 0x....; the table grants nothing, and reading it here would allow the
        // very characters the guard refuses.
        for (int index = start + 1; index < lines.Length && lines[index].Trim() != ")"; index++)
        {
            string code = lines[index].Split('#')[0];
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(code, @"0x([0-9A-Fa-f]{4})"))
                allowed.Add((char)Convert.ToInt32(match.Groups[1].Value, 16));
        }

        return allowed;
    }

    private static IReadOnlyList<string> ReleaseNoteFiles()
    {
        string docs = Path.Combine(FindRepoRoot(), "docs");
        return
        [
            .. Directory.EnumerateFiles(Path.Combine(docs, "release-notes"), "*.md", SearchOption.AllDirectories),
            .. Directory.EnumerateFiles(Path.Combine(docs, "fr", "release-notes"), "*.md", SearchOption.AllDirectories),
        ];
    }

    // Arrows and trees are the deliberate exception. If someone tightens this guard into "ASCII
    // only", that intent is lost silently, so it is asserted rather than left to the remarks.
    [Fact]
    public void ArrowsAndBoxDrawingAreNotRefused()
    {
        foreach (char welcome in new[] { '→', '↔', '├', '│', '└', '─', '☐' })
        {
            Assert.False(
                Refused.ContainsKey(welcome),
                $"U+{(int)welcome:X4} carries meaning in a graph, a tree or a checklist and is allowed.");
        }
    }

    private static IReadOnlyList<string> PublicDocuments()
    {
        string root = FindRepoRoot();
        List<string> paths =
        [
            .. Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly),
            .. Directory.EnumerateFiles(
                Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories),
        ];

        // CLAUDE.md is a working file for contributors, not published documentation.
        return [.. paths.Where(p =>
            !string.Equals(Path.GetFileName(p), "CLAUDE.md", System.StringComparison.Ordinal))];
    }

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
