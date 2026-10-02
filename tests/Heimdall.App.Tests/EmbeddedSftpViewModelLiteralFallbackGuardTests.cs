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
using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// The SFTP view models keep no user-facing text behind a null-coalescing arm.
/// </summary>
/// <remarks>
/// A <c>?? "text"</c> arm after a localizer call is a sentence living in code: nothing updates it
/// when the catalogue entry is reworded, and it is never shown while a localizer exists. The
/// view models resolve their text through the key-returning helpers instead. The pattern is
/// proven against a positive control, so a regex that silently matched nothing cannot pass.
/// </remarks>
public sealed class EmbeddedSftpViewModelLiteralFallbackGuardTests
{
    private const int MinimumExpectedFiles = 2;

    private static readonly Regex LiteralFallbackArm = new(
        "\\?\\?\\s*\\$?\"",
        RegexOptions.None,
        matchTimeout: TimeSpan.FromSeconds(5));

    private static readonly Regex EnglishPluralHelper = new(
        @"PluralRules\s*\.\s*SelectEnglish",
        RegexOptions.None,
        matchTimeout: TimeSpan.FromSeconds(5));

    [Fact]
    public void NoSftpViewModelKeepsALiteralBehindANullCoalescingArm()
    {
        List<string> offenders = [];
        foreach (string file in SftpViewModelFiles())
        {
            string code = StripComments(File.ReadAllText(file));
            foreach (Match match in LiteralFallbackArm.Matches(code))
            {
                offenders.Add($"{Path.GetFileName(file)}: {Excerpt(code, match.Index)}");
            }

            foreach (Match match in EnglishPluralHelper.Matches(code))
            {
                offenders.Add($"{Path.GetFileName(file)}: {Excerpt(code, match.Index)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "User-facing text sits behind a fallback arm in an SFTP view model: "
                + string.Join(" | ", offenders));
    }

    [Fact]
    public void TheGuardPatternRecognisesTheShapeItForbids()
    {
        // Positive control: without it, a pattern that matches nothing keeps the guard green.
        Assert.Matches(LiteralFallbackArm, "x = localizer?[\"Key\"] ?? \"English text\";");
        Assert.Matches(LiteralFallbackArm, "x = localizer?[\"Key\"] ?? $\"Text {value}\";");
        Assert.DoesNotMatch(LiteralFallbackArm, "x = localizer?[\"Key\"] ?? key;");
        Assert.Matches(EnglishPluralHelper, "PluralRules.SelectEnglish(1, a, b)");
    }

    [Fact]
    public void TheGuardReachesBothViewModelFiles()
    {
        Assert.True(
            SftpViewModelFiles().Count >= MinimumExpectedFiles,
            "The SFTP view model sources were not found; the guard would measure nothing.");
    }

    private static List<string> SftpViewModelFiles()
    {
        string directory = Path.Combine(ViewSourceRoot(), "src", "Heimdall.App", "ViewModels");
        return Directory
            .EnumerateFiles(directory, "EmbeddedSftpViewModel*.cs", SearchOption.TopDirectoryOnly)
            .ToList();
    }

    private static string ViewSourceRoot() => Views.EmbeddedRdp.ViewSource.RepoRoot();

    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(
            source,
            @"/\*.*?\*/",
            string.Empty,
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));
        return Regex.Replace(
            withoutBlocks,
            @"//[^\r\n]*",
            string.Empty,
            RegexOptions.None,
            TimeSpan.FromSeconds(5));
    }

    private static string Excerpt(string code, int index)
    {
        int start = Math.Max(0, index - 40);
        int length = Math.Min(code.Length - start, 90);
        return code.Substring(start, length).Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
