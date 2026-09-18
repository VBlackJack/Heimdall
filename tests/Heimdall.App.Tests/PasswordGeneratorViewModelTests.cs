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

using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels.Tools;

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="PasswordGeneratorViewModel"/>. Focuses on the
/// extracted generation engine, visibility-driving state and the init /
/// suspension guards that previously lived in the code-behind event cascade.
/// </summary>
public sealed class PasswordGeneratorViewModelTests : IDisposable
{
    private const string AmbiguousChars = "0Oo1lI|";
    private const string ShellDangerousChars = "$^&*'\"\\|`(){}[]<>!~;";
    private const string LayoutUnsafeChars = "aqwzmAQWZM";
    private readonly string _presetsDirectoryPath =
        Path.Combine(Path.GetTempPath(), nameof(PasswordGeneratorViewModelTests), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_presetsDirectoryPath))
        {
            Directory.Delete(_presetsDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public void Initialize_DefaultRandomSettings_GeneratesPasswordOfExpectedLength()
    {
        var sut = CreateInitializedVm();

        Assert.Equal(sut.Length, sut.GeneratedPassword.Length);
        Assert.NotEmpty(sut.GeneratedPassword);
    }

    [Fact]
    public void RandomMode_UppercaseOnly_ContainsOnlyUppercaseLetters()
    {
        var sut = CreateInitializedVm();

        sut.IncludeLowercase = false;
        sut.IncludeDigits = false;
        sut.IncludeSymbols = false;
        sut.Length = 64;

        Assert.NotEmpty(sut.GeneratedPassword);
        Assert.All(sut.GeneratedPassword, c => Assert.True(char.IsUpper(c)));
    }

    [Fact]
    public void RandomMode_DigitsOnly_ContainsOnlyDigits()
    {
        var sut = CreateInitializedVm();

        sut.IncludeUppercase = false;
        sut.IncludeLowercase = false;
        sut.IncludeSymbols = false;
        sut.Length = 64;

        Assert.NotEmpty(sut.GeneratedPassword);
        Assert.All(sut.GeneratedPassword, c => Assert.True(char.IsDigit(c)));
    }

    [Fact]
    public void RandomMode_SafetyFlags_FilterForbiddenCharacters()
    {
        var sut = CreateInitializedVm();

        sut.Length = 128;
        sut.ExcludeAmbiguous = true;
        Assert.DoesNotContain(sut.GeneratedPassword, c => AmbiguousChars.Contains(c));

        sut.IncludeUppercase = false;
        sut.IncludeLowercase = false;
        sut.IncludeDigits = false;
        sut.IncludeSymbols = true;
        sut.CliSafe = true;
        sut.Length = 128;
        Assert.DoesNotContain(sut.GeneratedPassword, c => ShellDangerousChars.Contains(c));

        sut.CliSafe = false;
        sut.IncludeUppercase = true;
        sut.IncludeLowercase = true;
        sut.IncludeSymbols = false;
        sut.LayoutSafe = true;
        sut.Length = 128;
        Assert.DoesNotContain(sut.GeneratedPassword, c => LayoutUnsafeChars.Contains(c));
    }

    [Fact]
    public void RandomMode_EmptyCharset_ProducesEmptyPassword()
    {
        var sut = CreateInitializedVm();

        sut.IncludeUppercase = false;
        sut.IncludeLowercase = false;
        sut.IncludeDigits = false;
        sut.IncludeSymbols = false;

        Assert.Equal(string.Empty, sut.GeneratedPassword);
    }

    [Fact]
    public void SyllableMode_CvcAndExtras_UpdateStructureAndPassword()
    {
        var sut = CreateInitializedVm();

        sut.SelectedModeIndex = 1;
        sut.SyllableLength = 18;
        sut.SyllableDigits = 2;
        sut.SyllableSpecials = 2;
        sut.SyllableSeparator = string.Empty;
        sut.SyllableCvc = true;

        var sawThreeCharGroup = false;
        for (var i = 0; i < 20 && !sawThreeCharGroup; i++)
        {
            sut.Generate();
            sawThreeCharGroup = sut.SyllableStructureText
                .Split(" \u00b7 ", StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split("  + ", StringSplitOptions.RemoveEmptyEntries)[0])
                .Any(part => part.Length == 3);
        }

        Assert.True(sawThreeCharGroup);
        Assert.InRange(sut.GeneratedPassword.Length, 18, 24);
        Assert.True(sut.GeneratedPassword.Count(char.IsDigit) >= 2);
        Assert.Contains(sut.GeneratedPassword, c => PasswordGeneratorViewModel.DefaultSymbolChars.Contains(c));
    }

    [Fact]
    public void PassphraseMode_UsesSeparatorCapitalizationAndFrenchWords()
    {
        var sut = CreateInitializedVm();
        ForceWordLists(
            sut,
            PasswordGeneratorViewModel.FallbackEnglishWords,
            PasswordGeneratorViewModel.FallbackFrenchWords,
            PasswordGeneratorViewModel.FallbackSpanishWords,
            PasswordGeneratorViewModel.FallbackLatinWords);

        sut.SelectedModeIndex = 2;
        sut.PassphraseWordCount = 4;
        sut.PassphraseSeparator = "-";
        sut.PassphraseDigits = 0;
        sut.PassphraseSpecials = 0;
        sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.WordCase;
        sut.PassphraseLanguageIndex = 1;

        var words = sut.GeneratedPassword.Split('-', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, sut.GeneratedPassword.Count(c => c == '-'));
        Assert.Equal(4, words.Length);
        Assert.All(words, word => Assert.True(char.IsUpper(word[0])));
        Assert.All(words, word => Assert.Contains(word.ToLowerInvariant(), PasswordGeneratorViewModel.FallbackFrenchWords));
    }

    /// <summary>
    /// Every language in the table draws from its own word list, including the ones past the
    /// second.
    /// </summary>
    /// <remarks>
    /// The generator used to fork on <c>index == 1</c>: French or, for everything else, English. A
    /// third language selected in the box produced English passphrases and nothing said so. The
    /// three lists here are disjoint and synthetic rather than the shipped ones, so a word can only
    /// have come from the list under test and the assertion cannot pass by coincidence.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PassphraseMode_DrawsFromTheWordListOfTheSelectedLanguage(int languageIndex)
    {
        string[][] lists =
        [
            ["alfa", "bravo", "charlie", "delta", "echo", "foxtrot"],
            ["golf", "hotel", "india", "juliet", "kilo", "lima"],
            ["mike", "november", "oscar", "papa", "quebec", "romeo"],
            ["sierra", "tango", "uniform", "victor", "whiskey", "xray"],
        ];

        Assert.Equal(PasswordGeneratorViewModel.PassphraseLanguages.Length, lists.Length);

        var allWords = lists.SelectMany(list => list).ToList();
        Assert.Equal(allWords.Count, allWords.Distinct(StringComparer.Ordinal).Count());

        var sut = CreateInitializedVm();
        ForceWordLists(sut, lists);

        sut.SelectedModeIndex = 2;
        sut.PassphraseWordCount = 4;
        sut.PassphraseSeparator = "-";
        sut.PassphraseDigits = 0;
        sut.PassphraseSpecials = 0;
        sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Lower;
        sut.PassphraseLanguageIndex = languageIndex;

        var words = sut.GeneratedPassword.Split('-', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, words.Length);
        Assert.All(words, word => Assert.Contains(word.ToLowerInvariant(), lists[languageIndex]));
    }

    /// <summary>
    /// The interface language decides which word list the tool opens on, and an interface language
    /// with no list of its own opens on English rather than on nothing.
    /// </summary>
    [Theory]
    [InlineData("en", 0)]
    [InlineData("fr", 1)]
    [InlineData("es", 2)]
    [InlineData("ES", 2)]
    [InlineData("la", 3)]
    [InlineData("de", 0)]
    [InlineData(null, 0)]
    public void PassphraseLanguageIndexFor_MapsTheInterfaceLocaleToItsWordList(string? locale, int expected)
    {
        Assert.Equal(expected, PasswordGeneratorViewModel.PassphraseLanguageIndexFor(locale));
    }

    /// <summary>
    /// The language order is append-only. The selected index is written into saved presets, so
    /// inserting a language rather than appending it repoints every preset already on disk at a
    /// different language, silently and on the next launch.
    /// </summary>
    [Fact]
    public void PassphraseLanguages_KeepTheOrderSavedPresetsWereWrittenAgainst()
    {
        Assert.Equal(
            ["en", "fr", "es", "la"],
            PasswordGeneratorViewModel.PassphraseLanguages.Select(language => language.Locale));
    }

    [Fact]
    public void StrengthEvaluation_TracksWeakAndStrongConfigurations()
    {
        var sut = CreateInitializedVm();

        sut.IncludeUppercase = false;
        sut.IncludeLowercase = false;
        sut.IncludeDigits = true;
        sut.IncludeSymbols = false;
        sut.Length = 4;
        Assert.True(sut.StrengthLevel <= 1);
        Assert.InRange(sut.StrengthPercent, 0.0, 1.0);

        sut.IncludeUppercase = true;
        sut.IncludeLowercase = true;
        sut.IncludeDigits = true;
        sut.IncludeSymbols = true;
        sut.CustomSpecials = PasswordGeneratorViewModel.DefaultSymbolChars;
        sut.Length = 24;
        Assert.True(sut.StrengthLevel >= 3);
        Assert.True(sut.StrengthPercent >= 0.75);
        Assert.InRange(sut.StrengthPercent, 0.0, 1.0);
    }

    [Fact]
    public void PhoneticDisplay_HandlesShortLongAndNatoCases()
    {
        var sut = CreateInitializedVm();

        Assert.NotEmpty(sut.PhoneticText);

        sut.Length = 40;
        Assert.Equal(string.Empty, sut.PhoneticText);

        InvokePrivate(sut, "UpdatePhoneticDisplay", "A");
        Assert.Contains("ALPHA", sut.PhoneticText, StringComparison.Ordinal);
    }

    [Fact]
    public void CrackTime_IsPopulatedForWeakAndStrongPasswords()
    {
        var sut = CreateInitializedVm();

        sut.IncludeUppercase = false;
        sut.IncludeLowercase = false;
        sut.IncludeDigits = true;
        sut.IncludeSymbols = false;
        sut.Length = 4;
        Assert.NotEmpty(sut.CrackTimeText);

        sut.IncludeUppercase = true;
        sut.IncludeLowercase = true;
        sut.IncludeDigits = true;
        sut.IncludeSymbols = true;
        sut.Length = 40;
        Assert.NotEmpty(sut.CrackTimeText);
    }

    [Fact]
    public void InitGuard_PreventsGenerationBeforeInitialize()
    {
        PasswordGeneratorViewModel sut = new(new PasswordPresetStorage(_presetsDirectoryPath));

        sut.Length = 16;

        Assert.Equal(string.Empty, sut.GeneratedPassword);
    }

    [Fact]
    public void SuspendResume_BatchesChangesAndRegeneratesOnResume()
    {
        var sut = CreateInitializedVm();
        var originalLength = sut.GeneratedPassword.Length;

        sut.SuspendRegeneration();
        sut.Length = 12;
        Assert.Equal(originalLength, sut.GeneratedPassword.Length);

        sut.ResumeRegeneration();
        Assert.Equal(12, sut.GeneratedPassword.Length);
    }

    [Fact]
    public void ApplyRandomPreset_SetsCorrectPropertiesAndRegenerates()
    {
        var sut = CreateInitializedVm();

        sut.ApplyRandomPreset(63, upper: true, lower: true, digits: true, symbols: true);

        Assert.Equal(0, sut.SelectedModeIndex);
        Assert.Equal(63, sut.Length);
        Assert.True(sut.IncludeUppercase);
        Assert.True(sut.IncludeLowercase);
        Assert.True(sut.IncludeDigits);
        Assert.True(sut.IncludeSymbols);
        Assert.False(sut.ExcludeAmbiguous);
        Assert.False(sut.CliSafe);
        Assert.False(sut.LayoutSafe);
        Assert.Equal(PasswordGeneratorViewModel.DefaultSymbolChars, sut.CustomSpecials);
        Assert.Equal(63, sut.GeneratedPassword.Length);
    }

    [Fact]
    public void ApplySyllablePreset_SwitchesModeAndSetsProperties()
    {
        var sut = CreateInitializedVm();

        sut.ApplySyllablePreset(24, caseIndex: 3, digits: 2, specials: 1, separator: "-", cvc: true);

        Assert.Equal(1, sut.SelectedModeIndex);
        Assert.Equal(24, sut.SyllableLength);
        Assert.Equal(3, sut.SyllableCaseIndex);
        Assert.Equal(2, sut.SyllableDigits);
        Assert.Equal(1, sut.SyllableSpecials);
        Assert.Equal("-", sut.SyllableSeparator);
        Assert.True(sut.SyllableCvc);
        Assert.True(sut.ShowSyllablePlacement);
        Assert.NotEmpty(sut.GeneratedPassword);
    }

    [Fact]
    public void SnapshotAndApplyPreset_RoundTripsAllProperties()
    {
        var source = CreateInitializedVm();
        source.SuspendRegeneration();
        try
        {
            source.SelectedModeIndex = 2;
            source.Length = 31;
            source.IncludeUppercase = false;
            source.IncludeLowercase = true;
            source.IncludeDigits = true;
            source.IncludeSymbols = true;
            source.LayoutSafe = true;
            source.ExcludeAmbiguous = true;
            source.CliSafe = true;
            source.CustomSpecials = "!?";
            source.SyllableLength = 22;
            source.SyllableCaseIndex = 4;
            source.SyllableDigits = 3;
            source.SyllableSpecials = 2;
            source.SyllablePlacementIndex = 2;
            source.SyllableSeparator = ".";
            source.SyllableCvc = true;
            source.PassphraseWordCount = 6;
            source.PassphraseSeparator = "_";
            source.PassphraseLanguageIndex = 1;
            source.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Alternating;
            source.PassphraseDigits = 4;
            source.PassphraseSpecials = 5;
            source.PassphrasePlacementIndex = 3;
            source.LeetBaseWord = "anchor";
            source.LeetRandomWord = false;
            source.LeetFullSubstitution = false;
            source.LeetDigits = 4;
            source.LeetSpecials = 3;
            source.LeetPlacementIndex = 2;
            source.LeetCaseIndex = 5;
            source.EntropyFloorIndex = 2;
            source.CaseBlocks = "UUlT";
            source.CaseBlocksAutoSync = false;
            source.DigitPositions = "10,90";
            source.SpecialPositions = "40";
            source.BatchCount = 6;
        }
        finally
        {
            source.ResumeRegeneration();
        }

        var preset = source.SnapshotCurrentPreset("roundtrip");
        var target = CreateInitializedVm();

        target.ApplyPreset(preset);

        Assert.Equal(source.SelectedModeIndex, target.SelectedModeIndex);
        Assert.Equal(source.Length, target.Length);
        Assert.Equal(source.IncludeUppercase, target.IncludeUppercase);
        Assert.Equal(source.IncludeLowercase, target.IncludeLowercase);
        Assert.Equal(source.IncludeDigits, target.IncludeDigits);
        Assert.Equal(source.IncludeSymbols, target.IncludeSymbols);
        Assert.Equal(source.LayoutSafe, target.LayoutSafe);
        Assert.Equal(source.ExcludeAmbiguous, target.ExcludeAmbiguous);
        Assert.Equal(source.CliSafe, target.CliSafe);
        Assert.Equal(source.CustomSpecials, target.CustomSpecials);
        Assert.Equal(source.SyllableLength, target.SyllableLength);
        Assert.Equal(source.SyllableCaseIndex, target.SyllableCaseIndex);
        Assert.Equal(source.SyllableDigits, target.SyllableDigits);
        Assert.Equal(source.SyllableSpecials, target.SyllableSpecials);
        Assert.Equal(source.SyllablePlacementIndex, target.SyllablePlacementIndex);
        Assert.Equal(source.SyllableSeparator, target.SyllableSeparator);
        Assert.Equal(source.SyllableCvc, target.SyllableCvc);
        Assert.Equal(source.PassphraseWordCount, target.PassphraseWordCount);
        Assert.Equal(source.PassphraseSeparator, target.PassphraseSeparator);
        Assert.Equal(source.PassphraseLanguageIndex, target.PassphraseLanguageIndex);
        Assert.Equal(source.PassphraseCaseIndex, target.PassphraseCaseIndex);
        Assert.Equal(source.PassphraseDigits, target.PassphraseDigits);
        Assert.Equal(source.PassphraseSpecials, target.PassphraseSpecials);
        Assert.Equal(source.PassphrasePlacementIndex, target.PassphrasePlacementIndex);
        Assert.Equal(source.LeetBaseWord, target.LeetBaseWord);
        Assert.Equal(source.LeetRandomWord, target.LeetRandomWord);
        Assert.Equal(source.LeetFullSubstitution, target.LeetFullSubstitution);
        Assert.Equal(source.LeetDigits, target.LeetDigits);
        Assert.Equal(source.LeetSpecials, target.LeetSpecials);
        Assert.Equal(source.LeetPlacementIndex, target.LeetPlacementIndex);
        Assert.Equal(source.LeetCaseIndex, target.LeetCaseIndex);
        Assert.Equal(source.EntropyFloorIndex, target.EntropyFloorIndex);
        Assert.Equal(source.CaseBlocks, target.CaseBlocks);
        Assert.Equal(source.CaseBlocksAutoSync, target.CaseBlocksAutoSync);
        Assert.Equal(source.DigitPositions, target.DigitPositions);
        Assert.Equal(source.SpecialPositions, target.SpecialPositions);
        Assert.Equal(source.BatchCount, target.BatchCount);
    }

    [Fact]
    public void History_TracksGeneratedPasswords_RespectsSizeLimit()
    {
        var sut = CreateInitializedVm();
        sut.IncludeUppercase = false;
        sut.IncludeLowercase = false;
        sut.IncludeDigits = true;
        sut.IncludeSymbols = false;
        sut.ClearHistoryCommand.Execute(null);

        for (var length = 4; length <= 15; length++)
        {
            sut.Length = length;
        }

        Assert.Equal(10, sut.PasswordHistory.Count);
        Assert.Equal(sut.GeneratedPassword, sut.PasswordHistory[0]);
        Assert.Equal(15, sut.PasswordHistory[0].Length);
        Assert.Equal(6, sut.PasswordHistory[^1].Length);
        Assert.All(sut.PasswordHistory, password => Assert.False(string.IsNullOrEmpty(password)));
        Assert.False(sut.IsHistoryEmpty);
    }

    [Fact]
    public async Task DeletePresetAsync_WithoutDialogService_DeletesImmediately()
    {
        var sut = CreateInitializedVm();
        sut.SavePreset("delete-me");

        Assert.Contains(sut.GetCustomPresetsForCurrentMode(), preset => preset.Name == "delete-me");

        var deleted = await sut.DeletePresetAsync("delete-me");

        Assert.True(deleted);
        Assert.DoesNotContain(sut.GetCustomPresetsForCurrentMode(), preset => preset.Name == "delete-me");
    }

    /// <summary>
    /// The word a leet password is built from comes out of the list of the selected language, and
    /// the tool shows which word that was.
    /// </summary>
    /// <remarks>
    /// The three lists are disjoint and synthetic, so a word can only have come from the list under
    /// test. Lowercase is forced because the default case mode uppercases at random, and the
    /// substitution is left on so the assertion also pins what the mode does to the word it drew.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LeetMode_DrawsItsBaseWordFromTheListOfTheSelectedLanguage(int languageIndex)
    {
        string[][] lists =
        [
            ["alfa", "bravo", "delta"],
            ["golf", "hotel", "india"],
            ["mike", "oscar", "papa"],
            ["sierra", "tango", "uniform"],
        ];

        var sut = CreateInitializedVm();
        ForceWordLists(sut, lists);

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 3;
            sut.LeetRandomWord = true;
            sut.LeetFullSubstitution = true;
            sut.LeetCaseIndex = 1;
            sut.LeetDigits = 0;
            sut.LeetSpecials = 0;
            sut.PassphraseLanguageIndex = languageIndex;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.True(sut.IsLeetMode);
        Assert.Contains(sut.LeetWordSource, lists[languageIndex]);
        Assert.Equal(LeetOf(sut.LeetWordSource), sut.GeneratedPassword);
    }

    /// <summary>
    /// Every letter the table covers is rewritten, and every letter it does not is left alone.
    /// </summary>
    [Fact]
    public void LeetMode_RewritesEveryLetterTheTableCovers()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 3;
            sut.LeetRandomWord = false;
            sut.LeetBaseWord = "abegilostqu";
            sut.LeetFullSubstitution = true;
            sut.LeetCaseIndex = 1;
            sut.LeetDigits = 0;
            sut.LeetSpecials = 0;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal("@8391!057qu", sut.GeneratedPassword);
    }

    /// <summary>
    /// CLI-safe exists so the result can be pasted into a shell. The substitution of <c>l</c> is
    /// <c>!</c>, which an interactive shell reads as history expansion, so that one substitution is
    /// skipped rather than the whole mode being unavailable.
    /// </summary>
    [Fact]
    public void LeetMode_SkipsTheSubstitutionAShellWouldReadAsSyntaxWhenCliSafe()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 3;
            sut.LeetRandomWord = false;
            sut.LeetBaseWord = "ball";
            sut.LeetFullSubstitution = true;
            sut.LeetCaseIndex = 1;
            sut.LeetDigits = 0;
            sut.LeetSpecials = 0;
            sut.CliSafe = true;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal("8@ll", sut.GeneratedPassword);
        Assert.DoesNotContain('!', sut.GeneratedPassword);
    }

    /// <summary>
    /// A word the operator typed is a word an attacker guesses, so the strength figure credits it
    /// with nothing and the issue list says why. A word drawn from the list is credited with the
    /// size of that list.
    /// </summary>
    [Fact]
    public void LeetMode_CreditsNothingForAWordTheOperatorTyped()
    {
        string[][] lists =
        [
            ["alfa", "bravo", "delta", "echo", "golf", "hotel", "india", "kilo"],
            ["golf", "hotel", "india"],
            ["mike", "oscar", "papa"],
            ["sierra", "tango", "uniform"],
        ];

        var sut = CreateInitializedVm();
        ForceWordLists(sut, lists);

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 3;
            sut.LeetRandomWord = true;
            sut.LeetFullSubstitution = true;
            sut.LeetCaseIndex = 1;
            sut.LeetDigits = 0;
            sut.LeetSpecials = 0;
            sut.PassphraseLanguageIndex = 0;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(3, BitsOf(sut.StrengthText));
        Assert.DoesNotContain("ToolPwdGenIssueChosenWord", sut.IssuesText);

        sut.LeetBaseWord = "alfa";
        sut.LeetRandomWord = false;

        Assert.Equal(0, BitsOf(sut.StrengthText));
        Assert.Contains("ToolPwdGenIssueChosenWord", sut.IssuesText);
    }

    /// <summary>
    /// Applying the whole table is a fixed rewriting worth nothing. Applying it letter by letter on
    /// a coin toss is worth one bit for each letter it could have touched, and only those letters:
    /// the <c>c</c> below is not in the table and is not paid for.
    /// </summary>
    [Fact]
    public void LeetMode_PaysForSubstitutionsOnlyWhenTheyAreNotAllApplied()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 3;
            sut.LeetRandomWord = false;
            sut.LeetBaseWord = "abc";
            sut.LeetFullSubstitution = true;
            sut.LeetCaseIndex = 1;
            sut.LeetDigits = 0;
            sut.LeetSpecials = 0;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(0, BitsOf(sut.StrengthText));

        sut.LeetFullSubstitution = false;

        Assert.Equal(2, BitsOf(sut.StrengthText));
    }

    /// <summary>
    /// Layout-safe drops the letters that move between AZERTY and QWERTY, which a mode that takes
    /// its letters from a word list cannot honour, so the box is not offered there.
    /// </summary>
    [Fact]
    public void LayoutSafe_IsOfferedOnlyWhereTheGeneratorChoosesItsOwnLetters()
    {
        var sut = CreateInitializedVm();

        sut.SelectedModeIndex = 0;
        Assert.True(sut.ShowLayoutSafe);

        sut.SelectedModeIndex = 1;
        Assert.True(sut.ShowLayoutSafe);

        sut.SelectedModeIndex = 2;
        Assert.False(sut.ShowLayoutSafe);

        sut.SelectedModeIndex = 3;
        Assert.False(sut.ShowLayoutSafe);
    }

    /// <summary>
    /// With no floor set, the size is the operator's and nothing moves it.
    /// </summary>
    /// <remarks>
    /// The control for every other test here: a floor that raised the length unconditionally would
    /// satisfy all of them.
    /// </remarks>
    [Fact]
    public void EntropyFloor_Off_LeavesTheSizeWhereItWasSet()
    {
        var sut = CreateInitializedVm();

        sut.Length = 8;

        Assert.Equal(0, sut.EntropyFloorBits);
        Assert.Equal(8, sut.Length);
        Assert.Equal(8, sut.EffectiveLength);
        Assert.Equal(8, sut.GeneratedPassword.Length);
        Assert.Empty(sut.FloorNoticeText);
    }

    /// <summary>
    /// A floor generates at the size it needs and takes the smallest size that carries it.
    /// </summary>
    /// <remarks>
    /// Minimality is the assertion that matters. Without it, a floor that jumped straight to the
    /// longest password the slider allows would pass every other assertion here and hand out 128
    /// characters to meet 60 bits.
    /// </remarks>
    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 80)]
    [InlineData(3, 100)]
    [InlineData(4, 128)]
    public void EntropyFloor_GeneratesAtTheSmallestSizeThatCarriesTheFloor(int floorIndex, int floorBits)
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 0;
            sut.Length = 4;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        sut.EntropyFloorIndex = floorIndex;

        double bitsPerCharacter = sut.LastEntropyBits / sut.EffectiveLength;

        Assert.Equal(floorBits, sut.EntropyFloorBits);
        Assert.True(sut.LastEntropyBits >= floorBits, $"the figure is {sut.LastEntropyBits}");
        Assert.True(
            bitsPerCharacter * (sut.EffectiveLength - 1) < floorBits,
            $"{sut.EffectiveLength} characters is one more than the floor needed");
        Assert.Equal(sut.EffectiveLength, sut.GeneratedPassword.Length);
        Assert.DoesNotContain("ToolPwdGenIssueFloorUnreachable", sut.IssuesText);
    }

    /// <summary>
    /// The floor never writes to the control the operator set, and clearing it returns the tool to
    /// what they asked for, immediately and without them having to put the value back.
    /// </summary>
    /// <remarks>
    /// The first version of this raised the slider itself. That looked honest - the length you
    /// would have to type was on screen - but it was one-way: the operator's own setting was gone,
    /// turning the floor off restored nothing, and dragging the slider back down fought the floor
    /// writing it up again on every mouse move.
    /// </remarks>
    [Fact]
    public void EntropyFloor_LeavesTheOperatorsOwnSettingAloneAndIsUndoneByClearingIt()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 0;
            sut.Length = 6;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        sut.EntropyFloorIndex = 4;

        Assert.Equal(6, sut.Length);
        Assert.True(sut.EffectiveLength > 6);
        Assert.NotEmpty(sut.FloorNoticeText);

        sut.EntropyFloorIndex = 0;

        Assert.Equal(6, sut.Length);
        Assert.Equal(6, sut.EffectiveLength);
        Assert.Equal(6, sut.GeneratedPassword.Length);
        Assert.Empty(sut.FloorNoticeText);
    }

    /// <summary>
    /// A floor out of reach changes nothing at all and says so.
    /// </summary>
    /// <remarks>
    /// A charset of one character carries no bits at any length, so no length reaches the floor.
    /// The earlier version raised the length to its maximum on the way to finding that out, and
    /// left it there.
    /// </remarks>
    [Fact]
    public void EntropyFloor_OutOfReach_ChangesNothingAndSaysSo()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 0;
            sut.Length = 10;
            sut.IncludeUppercase = false;
            sut.IncludeLowercase = false;
            sut.IncludeDigits = false;
            sut.IncludeSymbols = true;
            sut.CustomSpecials = "!";
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        sut.EntropyFloorIndex = 1;

        Assert.Equal(10, sut.Length);
        Assert.Equal(10, sut.EffectiveLength);
        Assert.Equal(10, sut.GeneratedPassword.Length);
        Assert.Empty(sut.FloorNoticeText);
        Assert.Contains("ToolPwdGenIssueFloorUnreachable", sut.IssuesText);
    }

    /// <summary>
    /// A floor that would need a longer password than the slider allows changes nothing either.
    /// </summary>
    /// <remarks>
    /// Asserted on the decision itself, with a 256-bit floor. The box offers 128 at most and the
    /// length slider reaches 128, so a two-character alphabet carries the highest floor on offer
    /// exactly: through the interface, the only random password that cannot reach its floor is one
    /// whose alphabet holds a single character, and that is the case the test above covers. This
    /// one keeps the other branch honest for the day a higher floor is offered.
    /// </remarks>
    [Fact]
    public void EntropyFloor_NeedingMoreLengthThanTheSliderAllows_ChangesNothing()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 0;
            sut.Length = 12;
            sut.IncludeUppercase = false;
            sut.IncludeLowercase = false;
            sut.IncludeDigits = false;
            sut.IncludeSymbols = true;
            sut.CustomSpecials = "!?";
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(12, sut.EffectiveLength);

        InvokePrivate(sut, "ResolveRandomFloor", 256);

        Assert.Equal(12, sut.Length);
        Assert.Equal(12, sut.EffectiveLength);
        Assert.True(sut.FloorOutOfReach);
        Assert.Empty(sut.FloorNoticeText);
    }

    /// <summary>
    /// A passphrase reaches the floor by gaining words, and the words stay words.
    /// </summary>
    [Fact]
    public void EntropyFloor_GivesAPassphraseMoreWordsRatherThanARandomTail()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 2;
            sut.PassphraseWordCount = 2;
            sut.PassphraseSeparator = "-";
            sut.PassphraseDigits = 0;
            sut.PassphraseSpecials = 0;
            sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Lower;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        sut.EntropyFloorIndex = 2;

        var words = sut.GeneratedPassword.Split('-', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(sut.LastEntropyBits >= 80, $"the figure is {sut.LastEntropyBits}");
        Assert.Equal(2, sut.PassphraseWordCount);
        Assert.True(sut.EffectivePassphraseWordCount > 2);
        Assert.Equal(sut.EffectivePassphraseWordCount, words.Length);
        Assert.All(words, word => Assert.All(word, character => Assert.True(char.IsLetter(character))));
    }

    /// <summary>
    /// A passphrase floor that no word count can carry leaves the word count alone.
    /// </summary>
    [Fact]
    public void EntropyFloor_OutOfReachForAPassphrase_LeavesTheWordCountAlone()
    {
        string[][] lists =
        [
            ["alfa", "bravo", "charlie", "delta"],
            ["golf", "hotel", "india", "juliet"],
            ["mike", "november", "oscar", "papa"],
            ["sierra", "tango", "uniform", "victor"],
        ];

        var sut = CreateInitializedVm();
        ForceWordLists(sut, lists);

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 2;
            sut.PassphraseWordCount = 2;
            sut.PassphraseSeparator = "-";
            sut.PassphraseDigits = 0;
            sut.PassphraseSpecials = 0;
            sut.PassphraseLanguageIndex = 0;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        sut.EntropyFloorIndex = 4;

        // Four words to choose from is two bits each, so even eight words is sixteen bits.
        Assert.Equal(2, sut.PassphraseWordCount);
        Assert.Equal(2, sut.EffectivePassphraseWordCount);
        Assert.Contains("ToolPwdGenIssueFloorUnreachable", sut.IssuesText);
    }

    /// <summary>
    /// A syllable password reaches the floor by gaining syllables, two characters at a time, which
    /// is the step its own slider moves in.
    /// </summary>
    [Fact]
    public void EntropyFloor_GivesASyllablePasswordMoreSyllables()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableLength = 8;
            sut.SyllableDigits = 0;
            sut.SyllableSpecials = 0;
            sut.SyllableCvc = false;
            sut.SyllableCaseIndex = 1;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        sut.EntropyFloorIndex = 1;

        Assert.Equal(8, sut.SyllableLength);
        Assert.True(sut.EffectiveSyllableLength > 8);
        Assert.Equal(0, sut.EffectiveSyllableLength % 2);
        Assert.True(sut.LastEntropyBits >= 60, $"the figure is {sut.LastEntropyBits}");
        Assert.Equal(sut.EffectiveSyllableLength, sut.GeneratedPassword.Length);
    }

    /// <summary>
    /// A leet password cannot grow its word, so the floor buys digits first and specials only once
    /// the digits are spent, and it buys as few as will do.
    /// </summary>
    [Fact]
    public void EntropyFloor_GivesALeetPasswordDigitsBeforeSpecials()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 3;
            sut.LeetRandomWord = true;
            sut.LeetDigits = 0;
            sut.LeetSpecials = 0;
            sut.LeetCaseIndex = 1;
            sut.PassphraseLanguageIndex = 0;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        sut.EntropyFloorIndex = 1;

        // An English word is worth about 11.8 bits and a digit 3.3, so the six digits the slider
        // allows leave a 60-bit floor short and the specials, worth about 4.9 each, make up the
        // rest. At this floor both end up at the maximum, so minimality is asserted as "one fewer
        // special would have missed" rather than as a number.
        Assert.Equal(0, sut.LeetDigits);
        Assert.Equal(0, sut.LeetSpecials);
        Assert.Equal(6, sut.EffectiveLeetDigits);
        Assert.True(sut.EffectiveLeetSpecials > 0, "the specials were never reached");
        Assert.True(sut.LastEntropyBits >= 60, $"the figure is {sut.LastEntropyBits}");

        double oneSpecial = sut.LastEntropyBits / sut.EffectiveLeetSpecials;
        Assert.True(
            sut.LastEntropyBits - oneSpecial < 60,
            $"{sut.EffectiveLeetSpecials} specials is more than the floor needed");
    }

    /// <summary>
    /// The order the floor spends in, asserted on the decision itself because no floor the box
    /// offers is low enough to leave a leet password with room to spare.
    /// </summary>
    /// <remarks>
    /// Driving the private decision with a 40-bit floor is the only way to see the order: at 60,
    /// the lowest the interface offers, an English word needs every digit and every special the
    /// sliders allow, and spending specials first would reach the same pair. A test that asserted
    /// the order at 60 would pass with the two arms swapped.
    /// </remarks>
    [Fact]
    public void EntropyFloor_SpendsLeetDigitsBeforeLeetSpecials()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 3;
            sut.LeetRandomWord = true;
            sut.LeetDigits = 0;
            sut.LeetSpecials = 0;
            sut.LeetCaseIndex = 1;
            sut.PassphraseLanguageIndex = 0;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(0, sut.EffectiveLeetDigits);
        Assert.Equal(0, sut.EffectiveLeetSpecials);

        InvokePrivate(sut, "ResolveLeetFloor", 40);

        // 11.8 for the word, 19.9 for six digits, and two specials at 4.9 to clear 40. One special
        // would have stopped at 36.6, and the seventh digit does not exist.
        Assert.Equal(6, sut.EffectiveLeetDigits);
        Assert.Equal(2, sut.EffectiveLeetSpecials);
    }

    /// <summary>
    /// The floor is decided from the settings, so pressing Generate again produces another password
    /// at the same size rather than walking the size upwards.
    /// </summary>
    /// <remarks>
    /// A leet password's case and a syllable password's closed syllables are worth a different
    /// number of bits on every draw. A floor that read the password rather than the settings moved
    /// the size on a click that was only meant to reroll, and never moved it back.
    /// </remarks>
    [Fact]
    public void EntropyFloor_DecidesFromTheSettingsSoRerollingDoesNotWalkTheSizeUp()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 3;
            sut.LeetRandomWord = true;
            sut.LeetDigits = 0;
            sut.LeetSpecials = 0;
            sut.LeetCaseIndex = 0;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        sut.EntropyFloorIndex = 1;

        var digits = sut.EffectiveLeetDigits;
        var specials = sut.EffectiveLeetSpecials;

        for (var reroll = 0; reroll < 12; reroll++)
        {
            sut.Generate();

            Assert.Equal(digits, sut.EffectiveLeetDigits);
            Assert.Equal(specials, sut.EffectiveLeetSpecials);
            Assert.Equal(0, sut.LeetDigits);
            Assert.Equal(0, sut.LeetSpecials);
        }
    }

    /// <summary>
    /// A quick preset is the configuration the operator clicked for. A floor changes what comes out
    /// of it, and that is visible rather than silent.
    /// </summary>
    [Fact]
    public void EntropyFloor_OverAQuickPreset_KeepsThePresetAndSaysWhatItGeneratedInstead()
    {
        var sut = CreateInitializedVm();

        sut.EntropyFloorIndex = 4;
        sut.ApplyRandomPreset(4, upper: false, lower: false, digits: true, symbols: false);

        Assert.Equal(4, sut.Length);
        Assert.True(sut.EffectiveLength > 4);
        Assert.NotEmpty(sut.FloorNoticeText);
        Assert.Equal(sut.EffectiveLength, sut.GeneratedPassword.Length);
        Assert.All(sut.GeneratedPassword, character => Assert.True(char.IsDigit(character)));
    }

    /// <summary>
    /// A block covers a whole syllable, and the pattern repeats when there are more syllables than
    /// blocks.
    /// </summary>
    /// <remarks>
    /// Closed syllables are off and the length is fixed, so the password is six open syllables of
    /// two letters: the pattern "UlT" lands on syllables 1, 2, 3 and again on 4, 5, 6.
    /// </remarks>
    [Fact]
    public void CaseBlocks_CaseOneSyllableEach_AndRepeatWhenTheyRunOut()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableLength = 17;
            sut.SyllableCvc = false;
            sut.SyllableDigits = 0;
            sut.SyllableSpecials = 0;
            sut.SyllableSeparator = "-";
            sut.CaseBlocksAutoSync = false;
            sut.CaseBlocks = "UlT";
            sut.SyllableCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Blocks;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        var syllables = sut.GeneratedPassword.Split('-', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(6, syllables.Length);
        for (var index = 0; index < syllables.Length; index++)
        {
            var syllable = syllables[index];
            switch ("UlT"[index % 3])
            {
                case 'U':
                    Assert.Equal(syllable.ToUpperInvariant(), syllable);
                    break;
                case 'l':
                    Assert.Equal(syllable.ToLowerInvariant(), syllable);
                    break;
                default:
                    Assert.True(char.IsUpper(syllable[0]), $"'{syllable}' does not start in title case");
                    Assert.Equal(syllable[1..].ToLowerInvariant(), syllable[1..]);
                    break;
            }
        }
    }

    /// <summary>
    /// In a leet password a block covers one letter, and a letter the substitution turned into a
    /// digit consumes no block: the pattern stays on the letters it can case.
    /// </summary>
    [Fact]
    public void CaseBlocks_CaseOneLetterEachInALeetPassword()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 3;
            sut.LeetRandomWord = false;
            sut.LeetBaseWord = "rhythm";
            sut.LeetFullSubstitution = true;
            sut.LeetDigits = 0;
            sut.LeetSpecials = 0;
            sut.CaseBlocksAutoSync = false;
            sut.CaseBlocks = "Ul";
            sut.LeetCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Blocks;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        // Only the t of "rhythm" is in the substitution table, and it keeps its place. The
        // letters the pattern reaches are r h y h m, alternating upper and lower from the
        // first one, and the 7 between them consumes no block.
        Assert.Equal("RhY7hM", sut.GeneratedPassword);
    }

    /// <summary>
    /// A pattern is chosen rather than drawn, so it is worth nothing and the figure says the same
    /// for every pattern. Mixed case is the only case mode that is paid for.
    /// </summary>
    [Fact]
    public void CaseBlocks_AreWorthNoEntropy()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableLength = 16;
            sut.SyllableCvc = false;
            sut.SyllableDigits = 0;
            sut.SyllableSpecials = 0;
            sut.CaseBlocksAutoSync = false;
            sut.CaseBlocks = "l";
            sut.SyllableCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Blocks;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        var allLower = sut.LastEntropyBits;

        sut.CaseBlocks = "UlTUlTUl";
        var mixedPattern = sut.LastEntropyBits;

        sut.SyllableCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Lower;
        var plainLowercase = sut.LastEntropyBits;

        sut.SyllableCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Mixed;
        var randomCase = sut.LastEntropyBits;

        Assert.Equal(allLower, mixedPattern);
        Assert.Equal(plainLowercase, mixedPattern);
        Assert.True(randomCase > mixedPattern, "mixed case is drawn, so it is worth more than a pattern");
    }

    /// <summary>
    /// The editor's operations: a block cycles through the three tokens, the pattern grows and
    /// shrinks between one and ten blocks, and setting them all writes one token everywhere.
    /// </summary>
    [Fact]
    public void CaseBlocks_EditorOperationsStayWithinTheirBounds()
    {
        var sut = CreateInitializedVm();
        sut.CaseBlocksAutoSync = false;
        sut.CaseBlocks = "Tl";

        sut.CycleCaseBlock(0);
        Assert.Equal("Ul", sut.CaseBlocks);
        sut.CycleCaseBlock(0);
        Assert.Equal("ll", sut.CaseBlocks);
        sut.CycleCaseBlock(0);
        Assert.Equal("Tl", sut.CaseBlocks);

        sut.CycleCaseBlock(-1);
        sut.CycleCaseBlock(9);
        Assert.Equal("Tl", sut.CaseBlocks);

        for (var added = 0; added < 20; added++)
        {
            sut.AddCaseBlock();
        }

        Assert.Equal(PasswordGeneratorViewModel.MaximumCaseBlocks, sut.CaseBlocks.Length);

        for (var removed = 0; removed < 20; removed++)
        {
            sut.RemoveCaseBlock();
        }

        Assert.Equal(PasswordGeneratorViewModel.MinimumCaseBlocks, sut.CaseBlocks.Length);

        sut.AddCaseBlock();
        sut.SetAllCaseBlocks('U');
        Assert.Equal("UU", sut.CaseBlocks);

        sut.SetAllCaseBlocks('x');
        Assert.Equal("UU", sut.CaseBlocks);

        sut.RandomizeCaseBlocks();
        Assert.Equal(2, sut.CaseBlocks.Length);
        Assert.All(sut.CaseBlocks, token => Assert.Contains(token, PasswordGeneratorViewModel.CaseBlockTokens));
    }

    /// <summary>
    /// While the pattern is synced, it holds one block per syllable, so a pattern read left to
    /// right lines up with the syllables read left to right. Editing a block by hand ends the sync,
    /// because the operator has said what they want.
    /// </summary>
    [Fact]
    public void CaseBlocks_SyncedPatternFollowsTheSyllableCount()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Blocks;
            sut.CaseBlocksAutoSync = true;
            sut.SyllableDigits = 0;
            sut.SyllableSpecials = 0;
            sut.SyllableLength = 12;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(6, sut.CaseBlocks.Length);

        sut.SyllableLength = 8;
        Assert.Equal(4, sut.CaseBlocks.Length);

        // The slider reaches 32, which is sixteen syllables, and the editor shows ten blocks.
        sut.SyllableLength = 32;
        Assert.Equal(PasswordGeneratorViewModel.MaximumCaseBlocks, sut.CaseBlocks.Length);

        // At the cap there is nothing to add, so nothing is edited and the sync stands.
        sut.AddCaseBlock();
        Assert.True(sut.CaseBlocksAutoSync);

        sut.SyllableLength = 12;
        Assert.Equal(6, sut.CaseBlocks.Length);

        sut.AddCaseBlock();
        Assert.False(sut.CaseBlocksAutoSync);

        var afterHand = sut.CaseBlocks;
        sut.SyllableLength = 10;
        Assert.Equal(afterHand, sut.CaseBlocks);
    }

    /// <summary>
    /// The block editor follows the syllables the password actually has, which is not the length
    /// when some of that length is spent on digits, specials or separators.
    /// </summary>
    [Fact]
    public void CaseBlocks_SyncedPatternCountsTheSyllablesAndNotTheLength()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Blocks;
            sut.CaseBlocksAutoSync = true;
            sut.SyllableSeparator = string.Empty;
            sut.SyllableDigits = 0;
            sut.SyllableSpecials = 0;
            sut.SyllableLength = 16;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(8, sut.CaseBlocks.Length);

        // Three of the sixteen characters go elsewhere, leaving thirteen for six syllables.
        sut.SyllableDigits = 2;
        sut.SyllableSpecials = 1;
        Assert.Equal(6, sut.CaseBlocks.Length);

        // A separator is a character of the password too: five of them between six syllables
        // would not fit, so the password holds four.
        sut.SyllableSeparator = "-";
        Assert.Equal(4, sut.CaseBlocks.Length);
    }

    /// <summary>
    /// A pattern read off disk is held to what the editor can produce.
    /// </summary>
    [Theory]
    [InlineData(null, "Tl")]
    [InlineData("", "Tl")]
    [InlineData("xyz", "Tl")]
    [InlineData("U l T", "UlT")]
    [InlineData("UUUUUUUUUUUUUUU", "UUUUUUUUUU")]
    public void CaseBlocks_ReadFromAPresetAreHeldToWhatTheEditorCanProduce(string? stored, string expected)
    {
        Assert.Equal(expected, PasswordGeneratorViewModel.SanitizeCaseBlocks(stored));
    }

    /// <summary>
    /// A digit set to nought percent opens the password and one set to a hundred closes it.
    /// </summary>
    /// <remarks>
    /// The syllable password itself is all letters, so where the digits landed can be read off the
    /// result without knowing what they are.
    /// </remarks>
    [Fact]
    public void Positions_PutEachDigitWhereItsCursorIs()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableLength = 12;
            sut.SyllableCvc = false;
            sut.SyllableCaseIndex = 1;
            sut.SyllableSeparator = string.Empty;
            sut.SyllableDigits = 2;
            sut.SyllableSpecials = 0;
            sut.SyllablePlacementIndex = (int)PasswordGeneratorViewModel.Placement.Positions;
            sut.DigitPositions = "0,100";
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        var password = sut.GeneratedPassword;

        Assert.Equal(12, password.Length);
        Assert.True(char.IsDigit(password[0]), $"'{password}' does not open on a digit");
        Assert.True(char.IsDigit(password[^1]), $"'{password}' does not close on a digit");
        Assert.All(password[1..^1], character => Assert.True(char.IsLetter(character)));
    }

    /// <summary>
    /// A cursor in the middle of the bar puts its character in the middle of the password.
    /// </summary>
    [Fact]
    public void Positions_PutADigitInTheMiddleWhenItsCursorIsInTheMiddle()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableLength = 10;
            sut.SyllableCvc = false;
            sut.SyllableCaseIndex = 1;
            sut.SyllableSeparator = string.Empty;
            sut.SyllableDigits = 1;
            sut.SyllableSpecials = 0;
            sut.SyllablePlacementIndex = (int)PasswordGeneratorViewModel.Placement.Positions;
            sut.DigitPositions = "50";
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        var password = sut.GeneratedPassword;

        Assert.Equal(10, password.Length);
        Assert.True(char.IsDigit(password[5]), $"'{password}' has no digit in the middle");
    }

    /// <summary>
    /// Digits and specials are placed from their own rows of the bar, and the specials are placed
    /// on the password the digits have already been written into.
    /// </summary>
    [Fact]
    public void Positions_PlaceSpecialsOnTheStringTheDigitsAreAlreadyIn()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableLength = 8;
            sut.SyllableCvc = false;
            sut.SyllableCaseIndex = 1;
            sut.SyllableSeparator = string.Empty;
            sut.SyllableDigits = 1;
            sut.SyllableSpecials = 1;
            sut.CustomSpecials = "!";
            sut.SyllablePlacementIndex = (int)PasswordGeneratorViewModel.Placement.Positions;
            sut.DigitPositions = "0";
            sut.SpecialPositions = "100";
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        var password = sut.GeneratedPassword;

        Assert.Equal(8, password.Length);
        Assert.True(char.IsDigit(password[0]), $"'{password}' does not open on a digit");
        Assert.Equal('!', password[^1]);
    }

    /// <summary>
    /// The bar always carries one cursor per character the mode inserts, however the count changed:
    /// a slider, a preset, or the strength floor buying a digit of its own.
    /// </summary>
    [Fact]
    public void Positions_KeepOneCursorPerCharacterTheModeInserts()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableDigits = 3;
            sut.SyllableSpecials = 2;
            sut.SyllablePlacementIndex = (int)PasswordGeneratorViewModel.Placement.Positions;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(3, PasswordGeneratorViewModel.ParsePositions(sut.DigitPositions).Length);
        Assert.Equal(2, PasswordGeneratorViewModel.ParsePositions(sut.SpecialPositions).Length);

        sut.SyllableDigits = 6;
        Assert.Equal(6, PasswordGeneratorViewModel.ParsePositions(sut.DigitPositions).Length);

        sut.SyllableSpecials = 0;
        Assert.Empty(PasswordGeneratorViewModel.ParsePositions(sut.SpecialPositions));
    }

    /// <summary>
    /// Moving one cursor leaves the others where they were, and a cursor cannot be pushed off
    /// either end of the bar.
    /// </summary>
    [Fact]
    public void Positions_MoveOneCursorAndClampItToTheBar()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableDigits = 3;
            sut.SyllableSpecials = 0;
            sut.SyllablePlacementIndex = (int)PasswordGeneratorViewModel.Placement.Positions;
            sut.DigitPositions = "10,50,90";
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        sut.MovePosition(digit: true, index: 1, percent: 73.25);
        Assert.Equal([10, 73.3, 90], PasswordGeneratorViewModel.ParsePositions(sut.DigitPositions));

        sut.MovePosition(digit: true, index: 0, percent: -40);
        Assert.Equal([0, 73.3, 90], PasswordGeneratorViewModel.ParsePositions(sut.DigitPositions));

        sut.MovePosition(digit: true, index: 2, percent: 250);
        Assert.Equal([0, 73.3, 100], PasswordGeneratorViewModel.ParsePositions(sut.DigitPositions));

        sut.MovePosition(digit: true, index: 7, percent: 50);
        Assert.Equal([0, 73.3, 100], PasswordGeneratorViewModel.ParsePositions(sut.DigitPositions));
    }

    /// <summary>
    /// Spreading puts each cursor in the middle of its own share of the bar, so one character sits
    /// at the centre rather than pinned to an end nobody chose.
    /// </summary>
    [Theory]
    [InlineData(1, new double[] { 50 })]
    [InlineData(2, new double[] { 25, 75 })]
    [InlineData(4, new double[] { 12.5, 37.5, 62.5, 87.5 })]
    public void Positions_SpreadEvenlyAroundTheMiddleOfEachShare(int count, double[] expected)
    {
        Assert.Equal(expected, PasswordGeneratorViewModel.DistributeEvenly(count));
    }

    /// <summary>
    /// A position list read off disk is held to the bar: numbers only, inside its two ends.
    /// </summary>
    [Theory]
    [InlineData(null, new double[0])]
    [InlineData("", new double[0])]
    [InlineData("nonsense", new double[0])]
    [InlineData(" 10 , 20 ", new double[] { 10, 20 })]
    [InlineData("-5,250", new double[] { 0, 100 })]
    [InlineData("33.333", new double[] { 33.3 })]
    public void Positions_ReadFromAPresetAreHeldToTheBar(string? stored, double[] expected)
    {
        Assert.Equal(expected, PasswordGeneratorViewModel.ParsePositions(stored));
    }

    /// <summary>
    /// The bar is only offered where there is something to place.
    /// </summary>
    [Fact]
    public void Positions_BarIsShownOnlyWhenTheModePlacesSomethingByPosition()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableDigits = 2;
            sut.SyllableSpecials = 0;
            sut.SyllablePlacementIndex = (int)PasswordGeneratorViewModel.Placement.Random;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.False(sut.ShowPlacementBar);

        sut.SyllablePlacementIndex = (int)PasswordGeneratorViewModel.Placement.Positions;
        Assert.True(sut.ShowPlacementBar);

        sut.SyllableDigits = 0;
        Assert.False(sut.ShowPlacementBar);

        sut.SelectedModeIndex = 0;
        Assert.False(sut.ShowPlacementBar);
    }

    /// <summary>
    /// The passphrase takes digits and specials by the handful, not one of each.
    /// </summary>
    [Fact]
    public void PassphraseMode_TakesAsManyDigitsAndSpecialsAsAsked()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 2;
            sut.PassphraseWordCount = 3;
            sut.PassphraseSeparator = "-";
            sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Lower;
            sut.CustomSpecials = "!";
            sut.PassphraseDigits = 4;
            sut.PassphraseSpecials = 3;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(4, sut.GeneratedPassword.Count(char.IsDigit));
        Assert.Equal(3, sut.GeneratedPassword.Count(character => character == '!'));
    }

    /// <summary>
    /// Each mode's own case box governs its own mode, and the passphrase cases whole words.
    /// </summary>
    [Fact]
    public void PassphraseMode_CasesWholeWordsFromItsOwnCaseBox()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 2;
            sut.PassphraseWordCount = 4;
            sut.PassphraseSeparator = "-";
            sut.PassphraseDigits = 0;
            sut.PassphraseSpecials = 0;
            sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Upper;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(sut.GeneratedPassword.ToUpperInvariant(), sut.GeneratedPassword);

        sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Alternating;
        var alternating = sut.GeneratedPassword.Split('-', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, alternating.Length);
        for (var index = 0; index < alternating.Length; index++)
        {
            var word = alternating[index];
            var expected = index % 2 == 0 ? word.ToLowerInvariant() : word.ToUpperInvariant();
            Assert.Equal(expected, word);
        }
    }

    /// <summary>
    /// A block covers one word in a passphrase, and the pattern repeats over the words.
    /// </summary>
    [Fact]
    public void PassphraseMode_CaseBlocksCoverOneWordEach()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 2;
            sut.PassphraseWordCount = 4;
            sut.PassphraseSeparator = "-";
            sut.PassphraseDigits = 0;
            sut.PassphraseSpecials = 0;
            sut.CaseBlocksAutoSync = false;
            sut.CaseBlocks = "Ul";
            sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Blocks;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        var words = sut.GeneratedPassword.Split('-', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, words.Length);
        Assert.Equal(words[0].ToUpperInvariant(), words[0]);
        Assert.Equal(words[1].ToLowerInvariant(), words[1]);
        Assert.Equal(words[2].ToUpperInvariant(), words[2]);
        Assert.Equal(words[3].ToLowerInvariant(), words[3]);
    }

    /// <summary>
    /// While the pattern is synced it holds one block per word, as it holds one per syllable in the
    /// other mode.
    /// </summary>
    [Fact]
    public void PassphraseMode_SyncedPatternFollowsTheWordCount()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 2;
            sut.CaseBlocksAutoSync = true;
            sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Blocks;
            sut.PassphraseWordCount = 5;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(5, sut.CaseBlocks.Length);

        sut.PassphraseWordCount = 8;
        Assert.Equal(8, sut.CaseBlocks.Length);
    }

    /// <summary>
    /// A preset written before the passphrase had counts still means what it said: one digit, one
    /// special, and words with a capital letter.
    /// </summary>
    /// <remarks>
    /// Those presets are on the operator's disk and carry three flags and no counts. Reading them
    /// as "nothing at all" would quietly weaken every passphrase preset ever saved.
    /// </remarks>
    [Fact]
    public void PassphrasePreset_WrittenBeforeTheCounts_KeepsWhatItMeant()
    {
        var sut = CreateInitializedVm();

        var old = new PasswordGeneratorViewModel.PasswordPreset
        {
            Name = "before",
            Mode = 2,
            PpWordCount = 5,
            PpSeparator = "-",
            PpDigit = true,
            PpSpecial = false,
            PpCapitalize = true,
        };

        sut.ApplyPreset(old);

        Assert.Equal(1, sut.PassphraseDigits);
        Assert.Equal(0, sut.PassphraseSpecials);
        Assert.Equal((int)PasswordGeneratorViewModel.SyllableCase.WordCase, sut.PassphraseCaseIndex);

        var saved = sut.SnapshotCurrentPreset("after");

        Assert.Equal(1, saved.PpDigits);
        Assert.Equal(0, saved.PpSpecials);
        Assert.True(saved.PpDigit, "the flag an older build reads should still say there is a digit");
        Assert.False(saved.PpSpecial);
        Assert.True(saved.PpCapitalize);
    }

    /// <summary>
    /// Mixed case is drawn at generation time, so a passphrase is credited for it as a syllable
    /// password is. Every other case mode is chosen, and is worth nothing.
    /// </summary>
    /// <remarks>
    /// The word lists are synthetic and every word is five letters long, so the letter count is
    /// known and the credit can be asserted as a number rather than as "more than before".
    /// </remarks>
    [Fact]
    public void PassphraseMode_CreditsMixedCaseAndNoOtherCaseMode()
    {
        string[] fiveLetters = ["alpha", "bravo", "delta", "gamma", "sigma", "omega"];
        var sut = CreateInitializedVm();
        ForceWordLists(sut, fiveLetters, fiveLetters, fiveLetters, fiveLetters);

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 2;
            sut.PassphraseWordCount = 4;
            sut.PassphraseSeparator = "-";
            sut.PassphraseDigits = 0;
            sut.PassphraseSpecials = 0;
            sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Lower;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        double chosenCase = sut.LastEntropyBits;

        sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Upper;
        Assert.Equal(chosenCase, sut.LastEntropyBits);

        sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Blocks;
        Assert.Equal(chosenCase, sut.LastEntropyBits);

        sut.PassphraseCaseIndex = (int)PasswordGeneratorViewModel.SyllableCase.Mixed;

        // Four words of five letters, at the 0.81 bits a character uppercased one time in four is
        // worth.
        Assert.Equal(chosenCase + (0.81 * 20), sut.LastEntropyBits, 3);
    }

    /// <summary>
    /// One click produces as many passwords as the count asks for, and they are all different.
    /// </summary>
    /// <remarks>
    /// Twenty random passwords of twenty-four characters colliding would mean the generator is not
    /// random, so distinctness is a fair thing to assert here even though it is a probabilistic
    /// claim: the chance of a false failure is far below that of the hardware faulting.
    /// </remarks>
    [Fact]
    public void Batch_ProducesAsManyPasswordsAsAsked()
    {
        var sut = CreateInitializedVm();

        sut.BatchCount = 20;

        Assert.Equal(20, sut.GeneratedBatch.Count);
        Assert.Equal(20, sut.BatchRows.Count);
        Assert.True(sut.ShowBatch);
        Assert.All(sut.GeneratedBatch, password => Assert.Equal(sut.Length, password.Length));
        Assert.Equal(20, sut.GeneratedBatch.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The password on display is the first of the batch, and the figures beside it describe that
    /// one rather than the last of a batch nobody asked to see.
    /// </summary>
    [Fact]
    public void Batch_PutsThePasswordOnDisplayFirst()
    {
        var sut = CreateInitializedVm();

        sut.BatchCount = 6;

        Assert.Equal(sut.GeneratedPassword, sut.GeneratedBatch[0]);
        Assert.Equal(sut.GeneratedPassword.Length, sut.Length);
    }

    /// <summary>
    /// A batch of one is the single password and nothing else: no list, and the tool reads as it
    /// always has.
    /// </summary>
    [Fact]
    public void Batch_OfOne_ShowsNoList()
    {
        var sut = CreateInitializedVm();

        Assert.Equal(1, sut.BatchCount);
        Assert.Single(sut.GeneratedBatch);
        Assert.False(sut.ShowBatch);
        Assert.Equal(sut.GeneratedPassword, sut.GeneratedBatch[0]);
    }

    /// <summary>
    /// Only the password on display enters the history. A batch of twenty would otherwise push out
    /// everything generated before it.
    /// </summary>
    [Fact]
    public void Batch_AddsOnlyThePasswordOnDisplayToTheHistory()
    {
        var sut = CreateInitializedVm();
        sut.ClearHistoryCommand.Execute(null);

        sut.BatchCount = 8;

        Assert.Single(sut.PasswordHistory);
        Assert.Equal(sut.GeneratedPassword, sut.PasswordHistory[0]);
    }

    /// <summary>
    /// Hiding the batch changes what is shown and nothing else: the passwords themselves are still
    /// there for the button that copies them.
    /// </summary>
    [Fact]
    public void Batch_Masked_HidesTheRowsAndKeepsThePasswords()
    {
        var sut = CreateInitializedVm();

        sut.BatchCount = 4;
        var generated = sut.GeneratedBatch.ToList();

        sut.MaskBatch = true;

        Assert.Equal(generated, sut.GeneratedBatch);
        Assert.Equal(4, sut.BatchRows.Count);
        for (var index = 0; index < sut.BatchRows.Count; index++)
        {
            Assert.Equal(generated[index].Length, sut.BatchRows[index].Length);
            Assert.All(sut.BatchRows[index], character => Assert.Equal('\u2022', character));
        }

        // What is copied is the password, not the dots: hiding a batch on screen must not hide it
        // from the button that copies it.
        Assert.Equal(string.Join(Environment.NewLine, generated), sut.BatchAsText);

        sut.MaskBatch = false;

        Assert.Equal(generated, sut.BatchRows);
    }

    /// <summary>
    /// Masking hides what is on screen; it does not ask for other passwords.
    /// </summary>
    [Fact]
    public void Batch_Masking_DoesNotRegenerate()
    {
        var sut = CreateInitializedVm();

        sut.BatchCount = 5;
        var generated = sut.GeneratedBatch.ToList();

        sut.MaskBatch = true;
        sut.MaskBatch = false;

        Assert.Equal(generated, sut.GeneratedBatch);
        Assert.Equal(generated[0], sut.GeneratedPassword);
    }

    /// <summary>
    /// What is copied and exported is one password per line, in the order shown.
    /// </summary>
    [Fact]
    public void Batch_IsCopiedOnePasswordPerLine()
    {
        var sut = CreateInitializedVm();

        sut.BatchCount = 7;

        var lines = sut.BatchAsText.Split(Environment.NewLine);

        Assert.Equal(7, lines.Length);
        Assert.Equal(sut.GeneratedBatch, lines);
    }

    /// <summary>
    /// The count is held to what the slider offers, whichever way it arrives.
    /// </summary>
    [Fact]
    public void Batch_CountFromAPresetIsHeldToTheSlider()
    {
        var sut = CreateInitializedVm();

        sut.ApplyPreset(new PasswordGeneratorViewModel.PasswordPreset
        {
            Name = "too many",
            Mode = 0,
            Length = 12,
            BatchCount = 500,
        });

        Assert.Equal(PasswordGeneratorViewModel.MaximumBatchCount, sut.BatchCount);
        Assert.Equal(PasswordGeneratorViewModel.MaximumBatchCount, sut.GeneratedBatch.Count);

        sut.ApplyPreset(new PasswordGeneratorViewModel.PasswordPreset
        {
            Name = "none",
            Mode = 0,
            Length = 12,
            BatchCount = 0,
        });

        Assert.Equal(PasswordGeneratorViewModel.MinimumBatchCount, sut.BatchCount);
    }

    /// <summary>
    /// A special character has to be ASCII punctuation, each one counts once, and the box is read
    /// only so far.
    /// </summary>
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("!@#", "!@#")]
    [InlineData("!!!@@@", "!@")]
    [InlineData("abc123", "")]
    [InlineData("a!b@c#", "!@#")]
    [InlineData("\u20ac\u00a3\u00a5", "")]
    [InlineData("!\u20ac@", "!@")]
    [InlineData("! @ #", "!@#")]
    public void Specials_AreHeldToAsciiPunctuation(string? typed, string expected)
    {
        Assert.Equal(expected, PasswordGeneratorViewModel.SanitizeCustomSpecials(typed));
    }

    /// <summary>
    /// Whatever is pasted into the box, what comes out of it is a subset of the allowed set, so it
    /// needs no length cap of its own.
    /// </summary>
    /// <remarks>
    /// A cap of thirty-two was written here first and removed: ASCII holds exactly thirty-two
    /// punctuation characters, so the cap could never have bound anything.
    /// </remarks>
    [Fact]
    public void Specials_CannotExceedTheAllowedSet()
    {
        string every = PasswordGeneratorViewModel.AllowedSpecialChars;
        string pasted = string.Concat(Enumerable.Repeat(every + "abc123€", 20));

        string usable = PasswordGeneratorViewModel.SanitizeCustomSpecials(pasted);

        Assert.Equal(every.Length, usable.Length);
        Assert.Equal(every.Length, usable.Distinct().Count());
        Assert.All(usable, character => Assert.Contains(character, every));
    }

    /// <summary>
    /// A password takes its specials from what the box is worth, not from what was typed into it.
    /// </summary>
    [Fact]
    public void Specials_APasswordUsesOnlyWhatTheBoxIsWorth()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 0;
            sut.Length = 64;
            sut.IncludeUppercase = false;
            sut.IncludeLowercase = false;
            sut.IncludeDigits = false;
            sut.IncludeSymbols = true;
            sut.CustomSpecials = "!\u20ac@\u00a3#";
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.All(sut.GeneratedPassword, character => Assert.Contains(character, "!@#"));
    }

    /// <summary>
    /// The box is left as typed, and a line under it says what survived, or that nothing did.
    /// </summary>
    [Fact]
    public void Specials_NoticeSaysWhatWillBeUsed()
    {
        var sut = CreateInitializedVm();

        sut.CustomSpecials = "!@#";
        Assert.Equal("!@#", sut.CustomSpecials);
        Assert.False(sut.ShowCustomSpecialsNotice);
        Assert.Empty(sut.CustomSpecialsNotice);

        sut.CustomSpecials = "!abc@";
        Assert.Equal("!abc@", sut.CustomSpecials);
        Assert.True(sut.ShowCustomSpecialsNotice);
        Assert.Contains("ToolPwdGenSpecialsUsable", sut.CustomSpecialsNotice);
        Assert.Equal("!@", PasswordGeneratorViewModel.SanitizeCustomSpecials(sut.CustomSpecials));

        sut.CustomSpecials = "\u20ac\u00a3\u00a5";
        Assert.Equal("\u20ac\u00a3\u00a5", sut.CustomSpecials);
        Assert.True(sut.ShowCustomSpecialsNotice);
        Assert.Contains("ToolPwdGenSpecialsNoneUsable", sut.CustomSpecialsNotice);
    }

    /// <summary>
    /// The clipboard delay is the one the box names, and the first entry is the thirty seconds the
    /// tool has always used.
    /// </summary>
    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 10)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    // Either side by one. Minus four and ninety-nine would have been satisfied by a modulo as
    // well as by a clamp, and a mutant that wrapped instead of clamping survived them.
    [InlineData(-1, 30)]
    [InlineData(4, 120)]
    public void ClipboardDelay_IsTheOneTheBoxNames(int index, int expectedSeconds)
    {
        var sut = CreateInitializedVm();

        sut.ClipboardClearIndex = index;

        Assert.Equal(expectedSeconds, sut.ClipboardClearSeconds);
    }

    private PasswordGeneratorViewModel CreateInitializedVm()
    {
        var sut = new PasswordGeneratorViewModel(new PasswordPresetStorage(_presetsDirectoryPath));
        sut.Initialize(context: null, localizer: null);
        return sut;
    }

    /// <summary>
    /// Replaces the loaded word lists, one per entry of
    /// <c>PasswordGeneratorViewModel.PassphraseLanguages</c> and in that order, so a test can say
    /// which words a passphrase is allowed to be built from.
    /// </summary>
    private static void ForceWordLists(PasswordGeneratorViewModel sut, params string[][] wordLists)
    {
        Assert.Equal(PasswordGeneratorViewModel.PassphraseLanguages.Length, wordLists.Length);

        typeof(PasswordGeneratorViewModel)
            .GetField("_wordLists", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sut, wordLists);
        sut.Generate();
    }

    /// <summary>
    /// The bit count the strength line advertises. The line reads "&lt;level&gt; (N bits)", and with
    /// no localizer the level and the unit are their own catalogue keys.
    /// </summary>
    private static double BitsOf(string strengthText)
    {
        Match match = Regex.Match(strengthText, @"\((\d+)");
        Assert.True(match.Success, $"no bit count in '{strengthText}'");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The word as the leet mode rewrites it with every substitution applied, spelled out here
    /// rather than read back from the view-model so that a test asserting on it cannot agree with a
    /// table that changed underneath it.
    /// </summary>
    private static string LeetOf(string word)
    {
        Dictionary<char, char> substitutions = new()
        {
            ['a'] = '@',
            ['b'] = '8',
            ['e'] = '3',
            ['g'] = '9',
            ['i'] = '1',
            ['l'] = '!',
            ['o'] = '0',
            ['s'] = '5',
            ['t'] = '7',
        };

        return new string(word
            .Select(character => substitutions.TryGetValue(character, out char replacement)
                ? replacement
                : character)
            .ToArray());
    }

    /// <summary>
    /// The length asked for is the length that comes out, whatever it is spent on.
    /// </summary>
    /// <remarks>
    /// The digits, the specials and the separator are all characters of the password, so all
    /// three are paid for out of the length rather than added to it. Each row draws a full batch,
    /// because the shape of a syllable is a coin toss and one draw proves nothing about the next.
    /// </remarks>
    [Theory]
    [InlineData(8, 0, 0, "", false)]
    [InlineData(8, 2, 1, "", false)]
    [InlineData(9, 2, 1, "", false)]
    [InlineData(12, 2, 1, "-", false)]
    [InlineData(12, 0, 0, "-", true)]
    [InlineData(16, 3, 2, "-", true)]
    [InlineData(17, 1, 0, "-", true)]
    [InlineData(20, 1, 0, "..", true)]
    [InlineData(24, 6, 6, "", true)]
    [InlineData(32, 4, 4, "-", true)]
    public void SyllableLength_IsTheLengthOfTheWholePassword(
        int length, int digits, int specials, string separator, bool cvc)
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableLength = length;
            sut.SyllableDigits = digits;
            sut.SyllableSpecials = specials;
            sut.SyllableSeparator = separator;
            sut.SyllableCvc = cvc;
            sut.BatchCount = 12;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(length, sut.GeneratedPassword.Length);
        Assert.Equal(length, sut.SyllableTotalLength);
        Assert.Equal(12, sut.GeneratedBatch.Count);
        foreach (var password in sut.GeneratedBatch)
        {
            Assert.Equal(length, password.Length);
        }

        Assert.Equal(digits, sut.EffectiveSyllableDigits);
        Assert.Equal(specials, sut.EffectiveSyllableSpecials);
        Assert.False(sut.ShowFloorNotice);
    }

    /// <summary>
    /// The counts are what gives when they ask for more characters than the length holds.
    /// </summary>
    /// <remarks>
    /// A length is exact and a count is a wish for how the length is spent, so the password stays
    /// the size it was asked for. The cut takes from whichever count is larger, so one kind of
    /// character does not disappear while the other keeps every place it asked for, and the notice
    /// line says what it ended up with.
    /// </remarks>
    [Theory]
    [InlineData(8, 6, 6, 3, 3)]
    [InlineData(8, 1, 6, 1, 5)]
    [InlineData(8, 6, 1, 5, 1)]
    [InlineData(8, 4, 4, 3, 3)]
    public void SyllableCounts_GiveWayToTheLength_AndSayThatTheyDid(
        int length, int digits, int specials, int expectedDigits, int expectedSpecials)
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableLength = length;
            sut.SyllableDigits = digits;
            sut.SyllableSpecials = specials;
            sut.SyllableSeparator = string.Empty;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        Assert.Equal(expectedDigits, sut.EffectiveSyllableDigits);
        Assert.Equal(expectedSpecials, sut.EffectiveSyllableSpecials);

        // What was asked for is left alone: the cut is a decision about this generation, not a
        // correction of the operator's sliders.
        Assert.Equal(digits, sut.SyllableDigits);
        Assert.Equal(specials, sut.SyllableSpecials);

        Assert.Equal(length, sut.GeneratedPassword.Length);
        Assert.True(sut.ShowFloorNotice);
        Assert.NotEmpty(sut.FloorNoticeText);
    }

    /// <summary>
    /// A cut that the floor undoes is not reported, because by the time the password is drawn it
    /// did not happen.
    /// </summary>
    [Fact]
    public void SyllableCounts_ThatTheFloorMakesRoomFor_AreNotReportedAsCut()
    {
        var sut = CreateInitializedVm();

        sut.SuspendRegeneration();
        try
        {
            sut.SelectedModeIndex = 1;
            sut.SyllableLength = 8;
            sut.SyllableDigits = 4;
            sut.SyllableSpecials = 4;
            sut.SyllableSeparator = string.Empty;
        }
        finally
        {
            sut.ResumeRegeneration();
        }

        // Eight characters have no room for eight of anything else, so two of each are cut.
        Assert.Equal(3, sut.EffectiveSyllableDigits);
        Assert.Equal(3, sut.EffectiveSyllableSpecials);
        Assert.True(sut.ShowFloorNotice);

        // A hundred bits cannot be carried by eight characters, so the floor raises the length,
        // and the longer password has room for the counts the shorter one did not.
        sut.EntropyFloorIndex = 3;

        Assert.True(sut.EffectiveSyllableLength > 8, $"the floor did not raise: {sut.EffectiveSyllableLength}");
        Assert.Equal(4, sut.EffectiveSyllableDigits);
        Assert.Equal(4, sut.EffectiveSyllableSpecials);
        Assert.Equal(sut.EffectiveSyllableLength, sut.GeneratedPassword.Length);
    }

    /// <summary>
    /// A preset written before the length covered the digits and the specials keeps the password
    /// length it used to produce.
    /// </summary>
    /// <remarks>
    /// The separator is not added back: how many separators a password carries depends on how its
    /// syllables fall, which a preset does not record. The migration therefore restores the count
    /// of characters the preset explicitly asked for, not the exact length it happened to produce.
    /// </remarks>
    [Theory]
    [InlineData(16, 2, 1, 19)]
    [InlineData(8, 0, 0, 8)]
    [InlineData(30, 3, 2, 32)]
    public void ASyllablePresetWrittenBeforeTheChange_HasItsCountsAddedBack(
        int stored, int digits, int specials, int expected)
    {
        var sut = CreateInitializedVm();

        sut.ApplyPreset(new PasswordGeneratorViewModel.PasswordPreset
        {
            Name = "written before",
            Mode = 1,
            SylLength = stored,
            SylDigits = digits,
            SylSpecials = specials,

            // What a file written before the change reads as, the property not being in it.
            SylLengthIncludesExtras = false,
        });

        Assert.Equal(expected, sut.SyllableLength);
    }

    /// <summary>
    /// A preset written since is taken at its word, and one written now says so.
    /// </summary>
    [Fact]
    public void ASyllablePresetWrittenSinceTheChange_IsTakenAtItsWord()
    {
        var source = CreateInitializedVm();

        source.SuspendRegeneration();
        try
        {
            source.SelectedModeIndex = 1;
            source.SyllableLength = 22;
            source.SyllableDigits = 3;
            source.SyllableSpecials = 2;
        }
        finally
        {
            source.ResumeRegeneration();
        }

        var preset = source.SnapshotCurrentPreset("written since");
        Assert.True(preset.SylLengthIncludesExtras);

        var target = CreateInitializedVm();
        target.ApplyPreset(preset);

        Assert.Equal(22, target.SyllableLength);
        Assert.Equal(22, target.GeneratedPassword.Length);
    }

    private static void InvokePrivate(PasswordGeneratorViewModel sut, string methodName, params object[] args)
    {
        typeof(PasswordGeneratorViewModel)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sut, args);
    }

}
