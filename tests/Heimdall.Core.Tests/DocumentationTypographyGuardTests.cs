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
