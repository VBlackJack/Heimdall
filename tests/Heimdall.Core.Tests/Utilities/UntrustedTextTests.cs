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

using Heimdall.Core.Utilities;

namespace Heimdall.Core.Tests;

/// <summary>
/// A value written by someone else is shown with every character that draws nothing, or looks
/// like blank space, written as a visible escape, while ordinary text stays readable.
/// </summary>
public sealed class UntrustedTextTests
{
    private const string Escape200B = "\\u{200b}";

    private static string Visible(string text) => UntrustedText.MakeVisible(text, UntrustedText.NoLengthLimit);

    [Fact]
    public void Space_SingleBetweenWords_IsKept()
    {
        Assert.Equal("a b", Visible("a b"));
        Assert.Equal("a  b", Visible("a  b"));
    }

    [Theory]
    [InlineData("a\u00A0b")]
    [InlineData("a\u202Fb")]
    [InlineData("\u5C71\u3000\u7530")]
    public void TypographicSpace_Alone_IsKept(string text)
    {
        Assert.Equal(text, Visible(text));
    }

    [Theory]
    [InlineData("a\u1680b", "a\\u{1680}b")]
    [InlineData("a\u2000b", "a\\u{2000}b")]
    [InlineData("a\u200Ab", "a\\u{200a}b")]
    [InlineData("a\u205Fb", "a\\u{205f}b")]
    public void OtherSpaceSeparators_AreEscaped(string text, string expected)
    {
        Assert.Equal(expected, Visible(text));
    }

    [Fact]
    public void TypographicSpace_InARun_IsEscaped()
    {
        Assert.Equal("a\\u{00a0}\\u{00a0}b", Visible("a\u00A0\u00A0b"));
    }

    [Fact]
    public void TypographicSpace_NextToAnOrdinarySpace_IsEscapedAndTheSpaceIsKept()
    {
        Assert.Equal("a \\u{00a0}b", Visible("a \u00A0b"));
    }

    [Fact]
    public void SpaceRun_LongerThanLimit_IsCounted()
    {
        Assert.Equal("a\\u{0020*300}b", Visible("a" + new string(' ', 300) + "b"));
    }

    [Fact]
    public void SpaceRun_AtLimit_IsKept()
    {
        string atLimit = "a" + new string(' ', UntrustedText.MaxPlainSpaceRun) + "b";

        Assert.Equal(atLimit, Visible(atLimit));
        Assert.Equal(
            "a\\u{0020*" + (UntrustedText.MaxPlainSpaceRun + 1) + "}b",
            Visible("a" + new string(' ', UntrustedText.MaxPlainSpaceRun + 1) + "b"));
    }

    [Theory]
    [InlineData("\u034F", "\\u{034f}")]
    [InlineData("\u115F", "\\u{115f}")]
    [InlineData("\u1160", "\\u{1160}")]
    [InlineData("\u17B4", "\\u{17b4}")]
    [InlineData("\u17B5", "\\u{17b5}")]
    [InlineData("\u180B", "\\u{180b}")]
    [InlineData("\u180C", "\\u{180c}")]
    [InlineData("\u180D", "\\u{180d}")]
    [InlineData("\u180F", "\\u{180f}")]
    [InlineData("\u2800", "\\u{2800}")]
    [InlineData("\u3164", "\\u{3164}")]
    [InlineData("\uFFA0", "\\u{ffa0}")]
    [InlineData("\U00016FE4", "\\u{16fe4}")]
    [InlineData("\U0001D159", "\\u{1d159}")]
    public void InvisibleLetters_AreEscaped(string invisible, string expected)
    {
        Assert.Equal("a" + expected + "b", Visible("a" + invisible + "b"));
    }

    [Fact]
    public void CfCoverage_TagsAndMongolianSeparator_AreEscaped()
    {
        Assert.Equal("\\u{e0041}", Visible("\U000E0041"));
        Assert.Equal("\\u{180e}", Visible("\u180E"));
    }

    [Theory]
    [InlineData("\u0378", "\\u{0378}")]
    [InlineData("\uFFF0", "\\u{fff0}")]
    [InlineData("\u2065", "\\u{2065}")]
    public void Unassigned_IsEscaped(string unassigned, string expected)
    {
        Assert.Equal("a" + expected + "b", Visible("a" + unassigned + "b"));
    }

    [Fact]
    public void LoneSurrogate_IsEscapedWithItsValue()
    {
        Assert.Equal("a\\u{d800}b", Visible("a\uD800b"));
        Assert.Equal("a\\u{dc00}b", Visible("a\uDC00b"));
        Assert.Equal("a\\u{d800}", Visible("a\uD800"));
    }

    [Fact]
    public void ValidPair_IsKept()
    {
        Assert.Equal("\U0001F600", Visible("\U0001F600"));
    }

    [Fact]
    public void RepeatedPairs_AreCountedByTheirCodePoint()
    {
        Assert.Equal("\\u{e0041*4}", Visible(string.Concat(Enumerable.Repeat("\U000E0041", 4))));
    }

    [Fact]
    public void VariationSelector_AfterBase_IsKept()
    {
        Assert.Equal("\u26A0\uFE0F", Visible("\u26A0\uFE0F"));
        Assert.Equal("1\uFE0F\u20E3", Visible("1\uFE0F\u20E3"));
        Assert.Equal("\u845B\U000E0100", Visible("\u845B\U000E0100"));
    }

    [Fact]
    public void VariationSelector_AfterAnOrdinaryAsciiCharacter_IsEscaped()
    {
        Assert.Equal("a\\u{fe0f}", Visible("a\uFE0F"));
        Assert.Equal("note\\u{fe0f}pad.exe", Visible("note\uFE0Fpad.exe"));
        Assert.Equal("a\\u{e0100}", Visible("a\U000E0100"));
    }

    [Fact]
    public void VariationSelector_AfterACombiningMark_IsEscaped()
    {
        Assert.Equal("a\u0301\\u{fe0f}", Visible("a\u0301\uFE0F"));
    }

    [Fact]
    public void SupplementaryVariationSelector_AfterANonIdeograph_IsEscaped()
    {
        Assert.Equal("\u26A0\\u{e0100}", Visible("\u26A0\U000E0100"));
    }

    [Fact]
    public void VariationSelector_AfterAHandListedInvisibleCharacter_IsEscaped()
    {
        Assert.Equal("\\u{3164}\\u{fe0f}", Visible("\u3164\uFE0F"));
        Assert.Equal("\\u{16fe4}\\u{fe0f}", Visible("\U00016FE4\uFE0F"));
    }

    [Fact]
    public void VariationSelector_RunOrLeading_IsEscaped()
    {
        Assert.Equal("\u26A0\uFE0F\\u{fe0f}", Visible("\u26A0\uFE0F\uFE0F"));
        Assert.Equal("\\u{fe0f}a", Visible("\uFE0Fa"));
    }

    [Fact]
    public void VariationSelector_AfterAnEscapeOrASpace_IsEscaped()
    {
        Assert.Equal(Escape200B + "\\u{fe0f}", Visible("\u200B\uFE0F"));
        Assert.Equal("a \\u{fe0f}", Visible("a \uFE0F"));
    }

    [Fact]
    public void NonLatinText_IsKept()
    {
        const string text = "\u5C71\u7530 \u0645\u064E\u0631 \u05E9\u05B8";

        Assert.Equal(text, Visible(text));
    }

    [Fact]
    public void RepeatedEscape_IsCounted()
    {
        Assert.Equal("\\u{200b*4000}", Visible(new string('\u200B', 4000)));
    }

    [Fact]
    public void RepeatedEscape_AtLimit_IsWrittenOut()
    {
        Assert.Equal(
            string.Concat(Enumerable.Repeat(Escape200B, UntrustedText.MaxRepeatedEscapesWrittenOut)),
            Visible(new string('\u200B', UntrustedText.MaxRepeatedEscapesWrittenOut)));
        Assert.Equal(
            "\\u{200b*" + (UntrustedText.MaxRepeatedEscapesWrittenOut + 1) + "}",
            Visible(new string('\u200B', UntrustedText.MaxRepeatedEscapesWrittenOut + 1)));
    }

    [Fact]
    public void Truncation_NeverSplitsACountedRun()
    {
        string text = "a" + new string('\u200B', 4000) + "b";

        string shown = UntrustedText.MakeVisible(text, 10);

        Assert.Equal("a" + UntrustedText.TruncationMarker, shown);
        Assert.DoesNotContain("\\u", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncation_NeverSplitsAnEscapeOrACountedRunThatFits()
    {
        string text = "a" + new string('\u200B', 4000) + "b";

        Assert.Equal("a\\u{200b*4000}b", UntrustedText.MakeVisible(text, 15));
        Assert.Equal(
            Escape200B + Escape200B + UntrustedText.TruncationMarker,
            UntrustedText.MakeVisible(new string('\u200B', 3), (Escape200B.Length * 2) + 1));
    }

    [Fact]
    public void Truncation_NeverSplitsASurrogatePair()
    {
        Assert.Equal("a" + UntrustedText.TruncationMarker, UntrustedText.MakeVisible("a\U0001F600", 2));
        Assert.Equal("a\U0001F600", UntrustedText.MakeVisible("a\U0001F600", 3));
    }

    [Fact]
    public void ControlFormatAndSeparators_AreStillEscaped()
    {
        Assert.Equal(
            "a\\u{000a}b\\u{202e}c\\u{2028}d\\u{2029}e",
            Visible("a\nb\u202Ec\u2028d\u2029e"));
    }

    [Fact]
    public void EmptyAndNull_GiveAnEmptyString()
    {
        Assert.Equal(string.Empty, UntrustedText.MakeVisible(null, 5));
        Assert.Equal(string.Empty, UntrustedText.MakeVisible(string.Empty, 5));
    }

    [Fact]
    public void EscapeForDoubleQuotes_BackslashBeforeQuote_IsDoubledAndQuoteEscaped()
    {
        Assert.Equal(@"x\\\""y", UntrustedText.EscapeForDoubleQuotes(@"x\""y"));
        Assert.Equal(@"x\\\\\""y", UntrustedText.EscapeForDoubleQuotes(@"x\\""y"));
    }

    [Fact]
    public void EscapeForDoubleQuotes_QuoteAlone_IsEscaped()
    {
        Assert.Equal(@"a\""b", UntrustedText.EscapeForDoubleQuotes(@"a""b"));
    }

    [Fact]
    public void EscapeForDoubleQuotes_TrailingBackslash_IsDoubled()
    {
        Assert.Equal(@"C:\dir\\", UntrustedText.EscapeForDoubleQuotes(@"C:\dir\"));
        Assert.Equal(@"C:\dir\\\\", UntrustedText.EscapeForDoubleQuotes(@"C:\dir\\"));
    }

    [Fact]
    public void EscapeForDoubleQuotes_OtherBackslashes_AreUnchanged()
    {
        Assert.Equal(@"C:\Temp", UntrustedText.EscapeForDoubleQuotes(@"C:\Temp"));
        Assert.Equal(@"C:\Temp \""x\""", UntrustedText.EscapeForDoubleQuotes(@"C:\Temp ""x"""));
        Assert.Equal(string.Empty, UntrustedText.EscapeForDoubleQuotes(null));
        Assert.Equal(string.Empty, UntrustedText.EscapeForDoubleQuotes(string.Empty));
    }

    [Fact]
    public void CountedRun_FollowedByDigits_CannotBeReadAsALongerCount()
    {
        Assert.Equal("\\u{200b*4}000", Visible("\u200B\u200B\u200B\u200B000"));
    }

    [Theory]
    [InlineData("\u007F", "\\u{007f}")]
    [InlineData("\u0085", "\\u{0085}")]
    [InlineData("\u009B", "\\u{009b}")]
    public void DeleteAndC1Controls_AreEscaped(string control, string expected)
    {
        Assert.Equal("a" + expected + "b", Visible("a" + control + "b"));
    }

    [Fact]
    public void EscapeForDoubleQuotes_OnlyBackslashes_AreDoubled()
    {
        Assert.Equal(@"\\\\", UntrustedText.EscapeForDoubleQuotes(@"\\"));
        Assert.Equal(@"\\", UntrustedText.EscapeForDoubleQuotes(@"\"));
    }

    [Fact]
    public void EscapeForDoubleQuotes_ValueEndingWithAQuote_EscapesIt()
    {
        Assert.Equal(@"a\""", UntrustedText.EscapeForDoubleQuotes(@"a"""));
    }

    [Fact]
    public void EscapeForDoubleQuotes_ValueEndingWithABackslashAndAQuote_DoublesTheBackslash()
    {
        Assert.Equal(@"a\\\""", UntrustedText.EscapeForDoubleQuotes(@"a\"""));
    }

    [Fact]
    public void EscapeForDoubleQuotes_NotClosedByAQuote_KeepsATrailingBackslash()
    {
        Assert.Equal(@"C:\dir\", UntrustedText.EscapeForDoubleQuotes(@"C:\dir\", closedByQuote: false));
        Assert.Equal(@"\\", UntrustedText.EscapeForDoubleQuotes(@"\\", closedByQuote: false));
        Assert.Equal(@"a\""b\", UntrustedText.EscapeForDoubleQuotes(@"a""b\", closedByQuote: false));
    }

    [Fact]
    public void EscapeForDoubleQuotes_NotClosedByAQuote_StillDoublesABackslashBeforeAQuote()
    {
        Assert.Equal(@"a\\\""b", UntrustedText.EscapeForDoubleQuotes(@"a\""b", closedByQuote: false));
        Assert.Equal(@"a\\\""", UntrustedText.EscapeForDoubleQuotes(@"a\""", closedByQuote: false));
    }

    [Fact]
    public void Truncation_ExactlyAtAnEscapeBoundary_HasNoMarker()
    {
        Assert.Equal("ab\\u{200b}", UntrustedText.MakeVisible("ab\u200B", 2 + Escape200B.Length));
        Assert.Equal("a\\u{200b}...", UntrustedText.MakeVisible("a\u200Bb", 1 + Escape200B.Length));
    }

    [Fact]
    public void LoneHighSurrogate_BeforeAValidPair_EscapesOnlyTheLoneUnit()
    {
        Assert.Equal("\\u{d800}\U0001F600", Visible("\uD800\U0001F600"));
    }

    [Fact]
    public void SpaceRun_InterruptedByATabOrAZeroWidthSpace_IsJudgedOnEachSide()
    {
        string side = new(' ', UntrustedText.MaxPlainSpaceRun);

        Assert.Equal(side + "\\u{0009}" + side, Visible(side + "\t" + side));
        Assert.Equal(side + Escape200B + side, Visible(side + "\u200B" + side));
        Assert.Equal("\u00A0" + Escape200B + "\u00A0", Visible("\u00A0\u200B\u00A0"));
    }

    [Fact]
    public void IndentedMultiLineText_StaysReadable()
    {
        string indent = new(' ', 12);

        Assert.Equal(
            "if (x) {\\u{000a}" + indent + "run\\u{000a}}",
            Visible("if (x) {\n" + indent + "run\n}"));
    }
}
