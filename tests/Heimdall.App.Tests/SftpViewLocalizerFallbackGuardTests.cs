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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The SFTP view resolves its text through the localizer and keeps no English prose behind
/// a null-coalescing arm.
/// </summary>
/// <remarks>
/// <para>
/// The file used to carry two dozen <c>_localizer?["Key"] ?? "English text"</c> arms. The
/// English half was never displayed - production always has a localizer - which is exactly
/// what made them harmful: they were user-facing sentences living in code, drifting from the
/// catalogue by construction, since nothing updated them when a message was reworded. Two
/// of them had already drifted into the catalogue's key name, and one passed the literal
/// "upload error" as an argument, which did reach a French user.
/// </para>
/// <para>
/// No fallback to prose is left. The column header used to fall back to the column identity, so
/// that a header would not read "SftpColName"; the headers now derive their text from the key
/// the column carries, and the only fallbacks are the three helpers, which return their key.
/// </para>
/// </remarks>
public sealed class SftpViewLocalizerFallbackGuardTests
{
    private const string ViewFile = "Views/EmbeddedSftpView.xaml.cs";

    /// <summary>The floor below which the rewrite would have removed the calls, not the arms.</summary>
    private const int MinimumHelperCalls = 20;

    /// <summary>
    /// The three helpers (<c>L</c>, <c>LF</c> and <c>LFC</c>), which fall back to the key they were
    /// handed.
    /// </summary>
    /// <remarks>
    /// Raised from two to three on 2026-09-14, and the reason is the defect this whole file was
    /// meant to prevent. The rewrite that introduced <c>L</c> wrote it as <c>L(key) =&gt; L(key)</c>
    /// - infinite recursion, a stack overflow on the first status message the file browser
    /// produced. Every assertion here passed, because they all measure that the English fallbacks
    /// were REMOVED and none measured that what replaced them worked. <c>L</c> now falls back to
    /// its key, exactly as <c>LF</c> already did, which is a third arm of the permitted shape and
    /// not a new exception to the rule: no English literal has come back.
    /// <para>
    /// The count did not move when the column header lost its own fallback and the counted helper
    /// <c>LFC</c> was added: one arm left, one arm arrived, both of the permitted shape.
    /// </para>
    /// </remarks>
    private const int PermittedExpressionFallbacks = 3;

    /// <summary>
    /// A localizer-shaped expression whose null-coalescing arm falls back to a literal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read on BLANKED source, and the pattern is built for what blanking leaves behind. The
    /// helper replaces every string literal with spaces, quotes included, so a first version
    /// of this guard - which looked for a quote after the <c>??</c> - could not match
    /// anything and passed on a file full of violations. Two mutants caught that, and this
    /// remark exists so the next reader does not rewrite it back.
    /// </para>
    /// <para>
    /// What survives blanking is the shape: an arm whose right-hand side is a literal has
    /// nothing but whitespace after the <c>??</c>, while an arm falling back to an
    /// expression still shows an identifier. So the ban is "<c>??</c> followed by no
    /// identifier", and the permitted case is its complement.
    /// </para>
    /// <para>
    /// Anchored on any identifier holding "ocaliz" rather than on <c>_localizer</c>: this
    /// file's own idiom copies the field into a local first, and a pattern anchored on the
    /// field name stays green while the arm comes back through the copy.
    /// </para>
    /// </remarks>
    private static readonly Regex FallbackArm = new(
        @"[A-Za-z_][A-Za-z0-9_]*ocaliz[A-Za-z0-9_]*[^\r\n]*?\?\?(?>\s*)(?![A-Za-z_])",
        RegexOptions.None,
        matchTimeout: TimeSpan.FromSeconds(5));

    /// <summary>A call to either helper, excluding their own declarations.</summary>
    /// <remarks>
    /// Matched on blanked text, where every string literal is spaces, so it cannot require a
    /// quote after the parenthesis. The lookbehinds keep the two declarations out of the
    /// count: without them a file with zero calls and two helpers would read as two.
    /// </remarks>
    private static readonly Regex HelperCall = new(
        @"(?<![A-Za-z0-9_.])(?<!string )LF?\s*\(",
        RegexOptions.None,
        matchTimeout: TimeSpan.FromSeconds(5));

    /// <summary>A localizer arm falling back to an expression rather than a literal.</summary>
    private static readonly Regex ExpressionFallbackArm = new(
        @"[A-Za-z_][A-Za-z0-9_]*ocaliz[A-Za-z0-9_]*[^\r\n]*?\?\?(?>\s*)[A-Za-z_]",
        RegexOptions.None,
        matchTimeout: TimeSpan.FromSeconds(5));

    [Fact]
    public void NoEnglishProseSitsBehindANullCoalescingArm()
    {
        string blanked = ViewSource.WithoutCommentsAndLiterals(ReadViewSource());

        // Comments and literals are blanked first, so the sentence in this file's own
        // remarks - which quotes the very shape being banned - is not a violation of it.
        // The match is printed: a guard that only says "something matched" over a 2000-line
        // file sends the next reader hunting.
        MatchCollection found = FallbackArm.Matches(blanked);

        Assert.True(
            found.Count == 0,
            "English prose sits behind a localizer fallback: "
                + string.Join(" | ", found.Select(match => Describe(blanked, match))));
    }

    [Fact]
    public void TheTextStillGoesThroughTheLocalizer()
    {
        // The reach floor. Deleting the arms would satisfy the reading above just as well by
        // deleting the calls, which is the version of this change that ships a blank status
        // bar. Counted on the blanked text so the helper declarations in the remarks do not
        // inflate it.
        string blanked = ViewSource.WithoutCommentsAndLiterals(ReadViewSource());
        int calls = HelperCall.Matches(blanked).Count;

        Assert.True(
            calls >= MinimumHelperCalls,
            $"only {calls} localizer helper call(s) remain in the SFTP view, expected at least {MinimumHelperCalls}");
    }

    [Fact]
    public void OnlyTheThreeKnownHelperFallbacksRemain()
    {
        // The complement of the reading above, and the reason it is not vacuous: these are what a
        // localizer arm is still allowed to look like here - a helper falling back to the key it
        // was given. A fourth would be a new exception, added quietly.
        string blanked = ViewSource.WithoutCommentsAndLiterals(ReadViewSource());

        int permitted = ExpressionFallbackArm.Matches(blanked).Count;

        Assert.True(
            permitted == PermittedExpressionFallbacks,
            $"{permitted} localizer arm(s) fall back to an expression; exactly {PermittedExpressionFallbacks} are known");
    }

    /// <summary>The 1-based line of a match, with its text, for a failure message.</summary>
    private static string Describe(string text, Match match)
    {
        int line = 1;
        for (int index = 0; index < match.Index && index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                line++;
            }
        }

        return $"line {line}: {match.Value.Trim()}";
    }

    private static string ReadViewSource()
    {
        string full = Path.Combine(
            ViewSource.RepoRoot(),
            "src",
            "Heimdall.App",
            ViewFile.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }
}
