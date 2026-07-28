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
using System.Reflection;
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
        ForceWordLists(sut, PasswordGeneratorViewModel.FallbackEnglishWords, PasswordGeneratorViewModel.FallbackFrenchWords);

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
        var sut = new PasswordGeneratorViewModel();

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

    private PasswordGeneratorViewModel CreateInitializedVm()
    {
        var sut = new PasswordGeneratorViewModel(new PasswordPresetStorage(_presetsDirectoryPath));
        sut.Initialize(context: null, localizer: null);
        return sut;
    }

    private static void ForceWordLists(PasswordGeneratorViewModel sut, string[] englishWords, string[] frenchWords)
    {
        typeof(PasswordGeneratorViewModel)
            .GetField("_englishWords", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sut, englishWords);
        typeof(PasswordGeneratorViewModel)
            .GetField("_frenchWords", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sut, frenchWords);
        sut.Generate();
    }

    private static void InvokePrivate(PasswordGeneratorViewModel sut, string methodName, params object[] args)
    {
        typeof(PasswordGeneratorViewModel)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sut, args);
    }

}
