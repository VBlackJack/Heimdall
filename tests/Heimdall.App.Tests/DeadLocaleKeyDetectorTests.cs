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

namespace Heimdall.App.Tests;

/// <summary>
/// The dead-key detector has to see every way this codebase can build a locale key at runtime,
/// or a live key reads as dead and the next clean-up deletes it. One test per shape: removing a
/// shape from the detector turns exactly its test red.
/// </summary>
public sealed class DeadLocaleKeyDetectorTests
{
    private static readonly string[] s_catalogue =
    [
        "ReportBannerOk",
        "ReportBannerFailed",
        "Validation.RuleRequired",
        "UnrelatedLeftover",
    ];

    [Fact]
    public void AKeySpelledOutInFullIsNotDead()
    {
        List<string> dead = DeadLocaleKeyDetector.FindDeadKeys(
            s_catalogue, "var text = loc[\"UnrelatedLeftover\"];");

        Assert.DoesNotContain("UnrelatedLeftover", dead);
    }

    [Fact]
    public void ANewKeyNothingReadsIsRefused()
    {
        List<string> dead = DeadLocaleKeyDetector.FindDeadKeys(
            ["LiveKey", "BrandNewUnusedKey"], "var text = loc[\"LiveKey\"];");

        Assert.Equal(["BrandNewUnusedKey"], dead);
    }

    [Fact]
    public void AnInterpolatedPrefixKeepsItsFamilyAlive()
    {
        AssertFamilyAlive("var text = loc[$\"ReportBanner{state}\"];");
    }

    [Theory]
    [InlineData("var text = loc[$@\"ReportBanner{state}\"];")]
    [InlineData("var text = loc[@$\"ReportBanner{state}\"];")]
    public void AVerbatimInterpolatedPrefixKeepsItsFamilyAlive(string source)
    {
        AssertFamilyAlive(source);
    }

    [Fact]
    public void ADottedInterpolatedPrefixKeepsItsFamilyAlive()
    {
        List<string> dead = DeadLocaleKeyDetector.FindDeadKeys(
            s_catalogue, "var text = loc[$\"Validation.{rule}\"];");

        Assert.DoesNotContain("Validation.RuleRequired", dead);
        Assert.Contains("ReportBannerOk", dead);
    }

    [Fact]
    public void ALiteralPlusExpressionKeepsItsFamilyAlive()
    {
        AssertFamilyAlive("var text = loc[\"ReportBanner\" + state];");
    }

    [Fact]
    public void AStringFormatTemplateKeepsItsFamilyAlive()
    {
        AssertFamilyAlive("var text = loc[string.Format(\"ReportBanner{0}\", state)];");
    }

    [Fact]
    public void AStringConcatLiteralKeepsItsFamilyAlive()
    {
        AssertFamilyAlive("var text = loc[string.Concat(\"ReportBanner\", state)];");
    }

    [Fact]
    public void ANameofPlusSuffixKeepsItsFamilyAlive()
    {
        AssertFamilyAlive("var text = loc[nameof(ReportBanner) + state];");
    }

    [Fact]
    public void AnInterpolatedNameofKeepsItsFamilyAlive()
    {
        AssertFamilyAlive("var text = loc[$\"{nameof(ReportBanner)}{state}\"];");
    }

    [Fact]
    public void ACommentEntryOfTheCatalogueIsNotAKey()
    {
        List<string> dead = DeadLocaleKeyDetector.FindDeadKeys(
            ["_comment_ux_misc", "RealDeadKey"], string.Empty);

        Assert.Equal(["RealDeadKey"], dead);
    }

    [Fact]
    public void AWordTooShortToBeAFamilyPardonsNothing()
    {
        // "Error" heads a hundred keys in the real catalogue; one unrelated concatenation of
        // that word must not retire the guard for all of them.
        List<string> dead = DeadLocaleKeyDetector.FindDeadKeys(
            ["ErrorBackupWriteFailed"], "var label = \"Error\" + code;");

        Assert.Equal(["ErrorBackupWriteFailed"], dead);
    }

    [Fact]
    public void APrefixThatHeadsNoKeyPardonsNothing()
    {
        List<string> dead = DeadLocaleKeyDetector.FindDeadKeys(
            s_catalogue, "var text = loc[\"UnheardOfPrefix\" + state];");

        Assert.Contains("ReportBannerOk", dead);
        Assert.Contains("ReportBannerFailed", dead);
    }

    private static void AssertFamilyAlive(string source)
    {
        List<string> dead = DeadLocaleKeyDetector.FindDeadKeys(s_catalogue, source);

        Assert.DoesNotContain("ReportBannerOk", dead);
        Assert.DoesNotContain("ReportBannerFailed", dead);
        Assert.Contains("UnrelatedLeftover", dead);
    }
}
