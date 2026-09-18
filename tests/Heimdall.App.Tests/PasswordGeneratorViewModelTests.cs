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
        sut.PassphraseAddDigit = false;
        sut.PassphraseAddSpecial = false;
        sut.PassphraseCapitalize = true;
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
        sut.PassphraseAddDigit = false;
        sut.PassphraseAddSpecial = false;
        sut.PassphraseCapitalize = false;
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
            source.PassphraseCapitalize = false;
            source.PassphraseAddDigit = false;
            source.PassphraseAddSpecial = true;
            source.PassphrasePlacementIndex = 3;
            source.LeetBaseWord = "anchor";
            source.LeetRandomWord = false;
            source.LeetFullSubstitution = false;
            source.LeetDigits = 4;
            source.LeetSpecials = 3;
            source.LeetPlacementIndex = 2;
            source.LeetCaseIndex = 5;
            source.EntropyFloorIndex = 2;
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
        Assert.Equal(source.PassphraseCapitalize, target.PassphraseCapitalize);
        Assert.Equal(source.PassphraseAddDigit, target.PassphraseAddDigit);
        Assert.Equal(source.PassphraseAddSpecial, target.PassphraseAddSpecial);
        Assert.Equal(source.PassphrasePlacementIndex, target.PassphrasePlacementIndex);
        Assert.Equal(source.LeetBaseWord, target.LeetBaseWord);
        Assert.Equal(source.LeetRandomWord, target.LeetRandomWord);
        Assert.Equal(source.LeetFullSubstitution, target.LeetFullSubstitution);
        Assert.Equal(source.LeetDigits, target.LeetDigits);
        Assert.Equal(source.LeetSpecials, target.LeetSpecials);
        Assert.Equal(source.LeetPlacementIndex, target.LeetPlacementIndex);
        Assert.Equal(source.LeetCaseIndex, target.LeetCaseIndex);
        Assert.Equal(source.EntropyFloorIndex, target.EntropyFloorIndex);
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
            sut.PassphraseAddDigit = false;
            sut.PassphraseAddSpecial = false;
            sut.PassphraseCapitalize = false;
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
            sut.PassphraseAddDigit = false;
            sut.PassphraseAddSpecial = false;
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

    private static void InvokePrivate(PasswordGeneratorViewModel sut, string methodName, params object[] args)
    {
        typeof(PasswordGeneratorViewModel)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sut, args);
    }

}
