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
/// Guards the locale source files against double-encoded (mojibake) characters.
/// Failure means a string was written to en.json or fr.json with UTF-8 bytes that
/// had been previously misread as Windows-1252 (or another single-byte codepage)
/// and then re-encoded as UTF-8, producing visually-corrupted text in the UI.
/// </summary>
public sealed class LocaleMojibakeGuardTests
{
    private const string EnLocaleFileName = "en.json";
    private const string FrLocaleFileName = "fr.json";

    /// <summary>
    /// Mojibake markers: short character sequences that almost never occur in
    /// legitimate French or English UI strings but are typical of Windows-1252 →
    /// UTF-8 double-encoding artifacts. Each entry is checked against locale values
    /// after JSON unescape (so <c>Ã‰</c> in source is caught as <c>Ã‰</c> here).
    /// </summary>
    private static readonly IReadOnlyList<string> MojibakeMarkers = new[]
    {
        "Ã‰",  // mojibake for É
        "Ã©",  // mojibake for é
        "Ã¨",  // mojibake for è
        "Ãª",  // mojibake for ê
        "Ã ",  // mojibake for à
        "Ã´",  // mojibake for ô
        "Ã®",  // mojibake for î
        "Ã¢",  // mojibake for â
        "Ã»",  // mojibake for û
        "Ã§",  // mojibake for ç
        "Â«",  // mojibake for U+00AB guillemet
        "Â»",  // mojibake for U+00BB guillemet
        "Â ",  // mojibake for NBSP
        "â€”", // mojibake for U+2014 em dash
        "â€“", // mojibake for U+2013 en dash
        "â€¦", // mojibake for U+2026 ellipsis
        "â€™", // mojibake for '
        "â€˜", // mojibake for '
        "â€œ", // mojibake for "
        "â†’", // mojibake for →
        "â†‘", // mojibake for ↑
        "�",        // U+FFFD replacement character (lost-encoding marker)
    };

    /// <summary>
    /// Scalars intentionally supported by the current English and French locale
    /// corpus. The blacklist above remains necessary for corrupt sequences made
    /// entirely from scalars in this set.
    /// </summary>
    /// <remarks>
    /// This is an allow-list, consumed below as <c>!AllowedScalars.Contains(...)</c>. Every
    /// entry it holds is a character no gate can ever fail on, so the typographic substitutes
    /// the project rule bans are deliberately absent: em dash, en dash, curly apostrophe,
    /// curly quote, both guillemets, the oe ligature, the single-character ellipsis, the
    /// no-break space and the multiplication sign. Accents, the bullet, the euro sign, arrows,
    /// stars, check marks and emoji stay: they carry something no pair of ASCII characters
    /// carries as clearly. <see cref="SourceTypographyGuardTests"/> refuses the banned set by
    /// name and reports the ASCII form to write instead.
    /// </remarks>
    private static readonly HashSet<int> AllowedScalars = CreateAllowedScalars();

    [Theory]
    [InlineData(EnLocaleFileName)]
    [InlineData(FrLocaleFileName)]
    public void LocaleValues_DoNotContainMojibakeMarkers(string fileName)
    {
        string repoRoot = FindRepoRoot();
        string localePath = Path.Combine(repoRoot, "locales", fileName);

        Assert.True(
            File.Exists(localePath),
            $"Locale file not found: {localePath}");

        string raw = File.ReadAllText(localePath, System.Text.Encoding.UTF8);
        using var document = JsonDocument.Parse(raw);

        List<string> violations = new();
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            string? value = property.Value.GetString();
            if (string.IsNullOrEmpty(value))
                continue;

            foreach (string marker in MojibakeMarkers)
            {
                int index = value.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                {
                    violations.Add(
                        $"  {fileName}::{property.Name} contains marker "
                        + $"U+{(int)marker[0]:X4}{(marker.Length > 1 ? "+" : string.Empty)}"
                        + $"{(marker.Length > 1 ? $"U+{(int)marker[1]:X4}" : string.Empty)}"
                        + $"{(marker.Length > 2 ? $"+U+{(int)marker[2]:X4}" : string.Empty)}"
                        + $" at position {index}");
                    break;
                }
            }

            int scalarPosition = 0;
            foreach (Rune rune in value.EnumerateRunes())
            {
                if (!AllowedScalars.Contains(rune.Value))
                {
                    violations.Add(
                        $"  {fileName}::{property.Name} contains disallowed scalar "
                        + $"U+{rune.Value:X4} at position {scalarPosition}");
                    break;
                }

                scalarPosition++;
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Found {violations.Count} mojibake violation(s) in {fileName}:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static HashSet<int> CreateAllowedScalars()
    {
        HashSet<int> allowed = new()
        {
            0x0009, 0x000A,
            0x00B7, 0x00C0, 0x00C9, 0x00CA,
            0x00E0, 0x00E2, 0x00E7, 0x00E8, 0x00E9, 0x00EA, 0x00EB, 0x00EE,
            0x00F4, 0x00F9, 0x00FB,
            0x2022, 0x20AC, 0x2190, 0x2191, 0x2192, 0x2193, 0x2605,
            0x2606, 0x2713, 0x2717, 0x1F512, 0x1F680,
        };

        for (int scalar = 0x0020; scalar <= 0x007E; scalar++)
            allowed.Add(scalar);

        return allowed;
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
