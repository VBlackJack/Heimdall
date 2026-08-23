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

using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// View-model backing the Password Generator tool. Encapsulates the password
/// generation engine, strength evaluation, phonetic rendering and the
/// mode-dependent visibility state while leaving WPF-only clipboard, focus and
/// dynamic button styling concerns in the view.
/// </summary>
public sealed partial class PasswordGeneratorViewModel : ObservableObject
{
    internal sealed class PasswordPreset
    {
        public string Name { get; set; } = string.Empty;
        public int Mode { get; set; }
        public int Length { get; set; } = 24;
        public bool Upper { get; set; } = true;
        public bool Lower { get; set; } = true;
        public bool Digits { get; set; } = true;
        public bool Symbols { get; set; }
        public bool LayoutSafe { get; set; }
        public bool ExcludeAmbiguous { get; set; }
        public bool CliSafe { get; set; }
        public string CustomSpecials { get; set; } = string.Empty;
        public int SylLength { get; set; } = 16;
        public int SylCase { get; set; }
        public int SylDigits { get; set; } = 2;
        public int SylSpecials { get; set; } = 1;
        public int SylPlacement { get; set; }
        public string SylSeparator { get; set; } = string.Empty;
        public bool SylCvc { get; set; }
        public int PpWordCount { get; set; } = 4;
        public string PpSeparator { get; set; } = "-";
        public int PpLanguage { get; set; }
        public bool PpCapitalize { get; set; } = true;
        public bool PpDigit { get; set; } = true;
        public bool PpSpecial { get; set; } = true;
        public int PpPlacement { get; set; }
    }

    internal enum GeneratorMode
    {
        Random,
        Syllable,
        Passphrase
    }

    internal enum SyllableCase
    {
        Mixed,
        Lower,
        Upper,
        Title,
        Alternating,
        WordCase,
        Inverse
    }

    internal enum Placement
    {
        Random,
        Start,
        End,
        Middle
    }

    private const string UppercaseChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string LowercaseChars = "abcdefghijklmnopqrstuvwxyz";
    private const string DigitChars = "0123456789";
    public const string DefaultSymbolChars = "!@#$%^&*()-_=+[]{}|;:',.<>?/~`";
    private const string AmbiguousChars = "0Oo1lI|";
    private const string ShellDangerousChars = "$^&*'\"\\|`(){}[]<>!~;";
    private const string DefaultPassphraseSeparator = "-";
    private const int PhoneticMaxLength = 32;
    private const string LayoutUnsafeChars = "aqwzmAQWZM";
    private const int HistoryMaxSize = 10;
    private const double BruteForceGuessesPerSecond = 10_000_000_000;

    private static readonly Dictionary<char, string> NatoAlphabet = new()
    {
        ['A'] = "Alpha",
        ['B'] = "Bravo",
        ['C'] = "Charlie",
        ['D'] = "Delta",
        ['E'] = "Echo",
        ['F'] = "Foxtrot",
        ['G'] = "Golf",
        ['H'] = "Hotel",
        ['I'] = "India",
        ['J'] = "Juliet",
        ['K'] = "Kilo",
        ['L'] = "Lima",
        ['M'] = "Mike",
        ['N'] = "November",
        ['O'] = "Oscar",
        ['P'] = "Papa",
        ['Q'] = "Quebec",
        ['R'] = "Romeo",
        ['S'] = "Sierra",
        ['T'] = "Tango",
        ['U'] = "Uniform",
        ['V'] = "Victor",
        ['W'] = "Whiskey",
        ['X'] = "X-ray",
        ['Y'] = "Yankee",
        ['Z'] = "Zulu",
        ['0'] = "Zero",
        ['1'] = "One",
        ['2'] = "Two",
        ['3'] = "Three",
        ['4'] = "Four",
        ['5'] = "Five",
        ['6'] = "Six",
        ['7'] = "Seven",
        ['8'] = "Eight",
        ['9'] = "Nine"
    };

    private static readonly Dictionary<char, string> SpecialCharNames = new()
    {
        ['!'] = "Exclamation",
        ['@'] = "At",
        ['#'] = "Hash",
        ['$'] = "Dollar",
        ['%'] = "Percent",
        ['^'] = "Caret",
        ['&'] = "Ampersand",
        ['*'] = "Asterisk",
        ['('] = "OpenParen",
        [')'] = "CloseParen",
        ['-'] = "Dash",
        ['_'] = "Underscore",
        ['='] = "Equals",
        ['+'] = "Plus",
        ['['] = "OpenBracket",
        [']'] = "CloseBracket",
        ['{'] = "OpenBrace",
        ['}'] = "CloseBrace",
        ['\\'] = "Backslash",
        ['/'] = "Slash",
        [';'] = "Semicolon",
        [':'] = "Colon",
        ['\''] = "Apostrophe",
        ['"'] = "Quote",
        [','] = "Comma",
        ['.'] = "Period",
        ['<'] = "LessThan",
        ['>'] = "GreaterThan",
        ['?'] = "Question",
        ['~'] = "Tilde",
        ['`'] = "Backtick",
        ['|'] = "Pipe",
        [' '] = "Space"
    };

    private static readonly string[] LayoutSafeConsonants =
        ["b", "c", "d", "f", "g", "h", "j", "k", "l", "n", "p", "r", "s", "t", "v", "x"];

    private static readonly string[] LayoutSafeVowels = ["e", "i", "o", "u", "y"];

    private static readonly string[] Consonants =
        ["b", "c", "d", "f", "g", "h", "j", "k", "l", "m", "n", "p", "r", "s", "t", "v", "w", "x", "z"];

    private static readonly string[] Vowels = ["a", "e", "i", "o", "u", "y"];

    private static readonly string[] EndingConsonants =
        ["b", "d", "f", "g", "k", "l", "m", "n", "p", "r", "s", "t"];

    internal static readonly string[] FallbackEnglishWords =
    [
        "anchor","apple","arrow","badge","beach","bridge","cabin","candle","castle","cherry",
        "circle","cloud","coffee","copper","coral","crane","crystal","delta","desert","dolphin",
        "dragon","eagle","ember","falcon","flame","forest","garden","glacier","golden","hammer",
        "harbor","helmet","honey","hunter","island","jacket","jewel","jungle","ladder","lantern",
        "marble","meadow","mirror","monkey","mountain","nature","noble","ocean","oracle","palace"
    ];

    internal static readonly string[] FallbackFrenchWords =
    [
        "abricot","amande","ancre","aurore","balcon","barque","bonnet","bougie","branche","cabane",
        "canard","cerise","chalet","chemin","cheval","citron","coffre","comete","cristal","dauphin",
        "desert","dragon","enigme","etoile","faucon","flamme","fleuve","fortin","galion","glacier",
        "harpon","jardin","jasmin","jungle","lanterne","marbre","miroir","montagne","moulin","nature",
        "oiseau","olive","orange","palmier","pensee","portail","radeau","renard","soleil","volcan"
    ];

    private LocalizationManager? _localizer;
    private bool _isInitialized;
    private bool _isSuspended;
    private Func<string, string, Task<bool>>? _confirmAsync;
    private readonly IPasswordPresetStorage _presetStorage;
    private List<PasswordPreset>? _cachedPresets;
    private string[] _englishWords = [];
    private string[] _frenchWords = [];

    /// <summary>Creates the view model over the supplied preset storage.</summary>
    /// <param name="presetStorage">Where presets are read and written.</param>
    /// <remarks>
    /// <b>There is deliberately no parameterless constructor.</b> One existed and built an
    /// unbound <see cref="PasswordPresetStorage"/>, which resolved the operator's own data
    /// directory. It was the second of the only two ways a test could reach production
    /// storage; both are gone, so the compiler now enforces what a source guard used to
    /// police by pattern.
    /// </remarks>
    internal PasswordGeneratorViewModel(IPasswordPresetStorage presetStorage)
    {
        ArgumentNullException.ThrowIfNull(presetStorage);
        _presetStorage = presetStorage;
    }

    [ObservableProperty] private int _selectedModeIndex;

    [ObservableProperty] private int _length = 24;
    [ObservableProperty] private bool _includeUppercase = true;
    [ObservableProperty] private bool _includeLowercase = true;
    [ObservableProperty] private bool _includeDigits = true;
    [ObservableProperty] private bool _includeSymbols = true;
    [ObservableProperty] private bool _excludeAmbiguous;
    [ObservableProperty] private bool _cliSafe;
    [ObservableProperty] private bool _layoutSafe;
    [ObservableProperty] private string _customSpecials = DefaultSymbolChars;

    [ObservableProperty] private int _syllableLength = 16;
    [ObservableProperty] private int _syllableCaseIndex;
    [ObservableProperty] private int _syllableDigits = 2;
    [ObservableProperty] private int _syllableSpecials = 1;
    [ObservableProperty] private int _syllablePlacementIndex;
    [ObservableProperty] private string _syllableSeparator = string.Empty;
    [ObservableProperty] private bool _syllableCvc;

    [ObservableProperty] private int _passphraseWordCount = 4;
    [ObservableProperty] private string _passphraseSeparator = DefaultPassphraseSeparator;
    [ObservableProperty] private int _passphraseLanguageIndex;
    [ObservableProperty] private bool _passphraseCapitalize = true;
    [ObservableProperty] private bool _passphraseAddDigit = true;
    [ObservableProperty] private bool _passphraseAddSpecial = true;
    [ObservableProperty] private int _passphrasePlacementIndex;

    [ObservableProperty] private bool _clipboardAutoClear;

    [ObservableProperty] private string _generatedPassword = string.Empty;
    [ObservableProperty] private string _phoneticText = string.Empty;
    [ObservableProperty] private int _strengthLevel;
    [ObservableProperty] private string _strengthText = string.Empty;
    [ObservableProperty] private double _strengthPercent;
    [ObservableProperty] private string _crackTimeText = string.Empty;
    [ObservableProperty] private string _issuesText = string.Empty;
    [ObservableProperty] private string _syllableStructureText = string.Empty;
    [ObservableProperty] private int _syllableTotalLength;

    public bool IsInitialized => _isInitialized;
    public ObservableCollection<string> PasswordHistory { get; } = new();
    public bool IsHistoryEmpty => PasswordHistory.Count == 0;

    /// <summary>
    /// Notified when the custom presets list changes (save or delete). The
    /// view observes this through PropertyChanged to rebuild preset buttons.
    /// </summary>
    public object? CustomPresetsChanged => null;

    internal GeneratorMode CurrentMode =>
        SelectedModeIndex switch
        {
            1 => GeneratorMode.Syllable,
            2 => GeneratorMode.Passphrase,
            _ => GeneratorMode.Random
        };

    public bool IsRandomMode => CurrentMode == GeneratorMode.Random;
    public bool IsSyllableMode => CurrentMode == GeneratorMode.Syllable;
    public bool IsPassphraseMode => CurrentMode == GeneratorMode.Passphrase;
    public bool ShowLayoutSafe => CurrentMode != GeneratorMode.Passphrase;
    public bool ShowExcludeAmbiguous => CurrentMode == GeneratorMode.Random;
    public bool ShowPhonetic => !string.IsNullOrEmpty(PhoneticText);
    public bool ShowSyllableStructure => IsSyllableMode && !string.IsNullOrEmpty(SyllableStructureText);
    public bool ShowStrength => !string.IsNullOrEmpty(GeneratedPassword);
    public bool ShowSyllablePlacement => IsSyllableMode && (SyllableDigits > 0 || SyllableSpecials > 0);
    public bool ShowPassphrasePlacement => IsPassphraseMode && (PassphraseAddDigit || PassphraseAddSpecial);
    public bool HasActiveSpecials => CurrentMode switch
    {
        GeneratorMode.Random => IncludeSymbols,
        GeneratorMode.Syllable => SyllableSpecials > 0,
        GeneratorMode.Passphrase => PassphraseAddSpecial,
        _ => false
    };

    /// <summary>
    /// Called by the view's IToolView.Initialize. Loads word lists, applies the
    /// initial context and generates the first password.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        LoadWordLists();

        PassphraseLanguageIndex = string.Equals(
            localizer?.CurrentLocale,
            "fr",
            StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        if (context?.Argument is { } arg && int.TryParse(arg, out var len))
        {
            Length = Math.Clamp(len, 4, 128);
        }

        _isInitialized = true;
        RaiseVisibilityProperties();
        GenerateCore();
    }

    public void SuspendRegeneration() => _isSuspended = true;

    public void ResumeRegeneration()
    {
        _isSuspended = false;
        RegenerateIfReady();
    }

    internal void SetDialogService(Func<string, string, Task<bool>>? confirmAsync)
        => _confirmAsync = confirmAsync;

    /// <summary>
    /// Public entry point for view-layer code that needs to trigger generation
    /// explicitly (preset handlers, keyboard shortcuts).
    /// </summary>
    public void Generate() => GenerateCoreCommand.Execute(null);

    internal PasswordPreset SnapshotCurrentPreset(string name) => new()
    {
        Name = name,
        Mode = SelectedModeIndex,
        Length = Length,
        Upper = IncludeUppercase,
        Lower = IncludeLowercase,
        Digits = IncludeDigits,
        Symbols = IncludeSymbols,
        LayoutSafe = LayoutSafe,
        ExcludeAmbiguous = ExcludeAmbiguous,
        CliSafe = CliSafe,
        CustomSpecials = CustomSpecials,
        SylLength = SyllableLength,
        SylCase = SyllableCaseIndex,
        SylDigits = SyllableDigits,
        SylSpecials = SyllableSpecials,
        SylPlacement = SyllablePlacementIndex,
        SylSeparator = SyllableSeparator,
        SylCvc = SyllableCvc,
        PpWordCount = PassphraseWordCount,
        PpSeparator = PassphraseSeparator,
        PpLanguage = PassphraseLanguageIndex,
        PpCapitalize = PassphraseCapitalize,
        PpDigit = PassphraseAddDigit,
        PpSpecial = PassphraseAddSpecial,
        PpPlacement = PassphrasePlacementIndex,
    };

    internal void ApplyPreset(PasswordPreset preset)
    {
        SuspendRegeneration();
        try
        {
            SelectedModeIndex = preset.Mode;
            Length = preset.Length;
            IncludeUppercase = preset.Upper;
            IncludeLowercase = preset.Lower;
            IncludeDigits = preset.Digits;
            IncludeSymbols = preset.Symbols;
            LayoutSafe = preset.LayoutSafe;
            ExcludeAmbiguous = preset.ExcludeAmbiguous;
            CliSafe = preset.CliSafe;
            if (!string.IsNullOrEmpty(preset.CustomSpecials))
            {
                CustomSpecials = preset.CustomSpecials;
            }
            SyllableLength = preset.SylLength;
            SyllableCaseIndex = preset.SylCase;
            SyllableDigits = preset.SylDigits;
            SyllableSpecials = preset.SylSpecials;
            SyllablePlacementIndex = preset.SylPlacement;
            SyllableSeparator = preset.SylSeparator;
            SyllableCvc = preset.SylCvc;
            PassphraseWordCount = preset.PpWordCount;
            PassphraseSeparator = preset.PpSeparator;
            PassphraseLanguageIndex = preset.PpLanguage;
            PassphraseCapitalize = preset.PpCapitalize;
            PassphraseAddDigit = preset.PpDigit;
            PassphraseAddSpecial = preset.PpSpecial;
            PassphrasePlacementIndex = preset.PpPlacement;
        }
        finally
        {
            ResumeRegeneration();
        }
    }

    internal void ApplyRandomPreset(int length, bool upper, bool lower, bool digits, bool symbols)
    {
        SuspendRegeneration();
        try
        {
            SelectedModeIndex = 0;
            IncludeUppercase = upper;
            IncludeLowercase = lower;
            IncludeDigits = digits;
            IncludeSymbols = symbols;
            ExcludeAmbiguous = false;
            CliSafe = false;
            LayoutSafe = false;
            CustomSpecials = DefaultSymbolChars;
            Length = length;
        }
        finally
        {
            ResumeRegeneration();
        }
    }

    internal void ApplySyllablePreset(int length, int caseIndex, int digits, int specials, string separator = "", bool cvc = false)
    {
        SuspendRegeneration();
        try
        {
            SelectedModeIndex = 1;
            SyllableLength = length;
            SyllableCaseIndex = caseIndex;
            SyllableDigits = digits;
            SyllableSpecials = specials;
            SyllablePlacementIndex = 0;
            SyllableSeparator = separator;
            SyllableCvc = cvc;
            LayoutSafe = false;
        }
        finally
        {
            ResumeRegeneration();
        }
    }

    internal void ApplyPassphrasePreset(int wordCount, string separator = "-")
    {
        SuspendRegeneration();
        try
        {
            SelectedModeIndex = 2;
            PassphraseWordCount = wordCount;
            PassphraseCapitalize = true;
            PassphraseAddDigit = true;
            PassphraseAddSpecial = true;
            PassphraseSeparator = separator;
        }
        finally
        {
            ResumeRegeneration();
        }
    }

    internal IReadOnlyList<PasswordPreset> GetCustomPresetsForCurrentMode()
        => LoadCustomPresets().Where(p => p.Mode == SelectedModeIndex).ToList().AsReadOnly();

    internal void SavePreset(string name)
    {
        var preset = SnapshotCurrentPreset(name);
        var presets = LoadCustomPresets();
        presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        presets.Add(preset);
        SaveCustomPresets(presets);
        OnPropertyChanged(nameof(CustomPresetsChanged));
    }

    internal async Task<bool> DeletePresetAsync(string name)
    {
        if (_confirmAsync is not null)
        {
            var message = string.Format(L("ToolPwdGenDeletePresetConfirm"), name);
            var confirmed = await _confirmAsync(L("ToolPwdGenDeletePreset"), message);
            if (!confirmed)
            {
                return false;
            }
        }

        var presets = LoadCustomPresets();
        presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        SaveCustomPresets(presets);
        OnPropertyChanged(nameof(CustomPresetsChanged));
        return true;
    }

    /// <summary>
    /// Clears the generated output and derived strength / phonetic state.
    /// </summary>
    public void ClearOutput()
    {
        SetEmptyOutput();
    }

    [RelayCommand]
    private void GenerateCore()
    {
        switch (CurrentMode)
        {
            case GeneratorMode.Random:
                GenerateRandomPassword();
                break;
            case GeneratorMode.Syllable:
                GenerateSyllablePassword();
                break;
            case GeneratorMode.Passphrase:
                GeneratePassphrase();
                break;
        }

        RaiseVisibilityProperties();
        AddToHistory(GeneratedPassword);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        PasswordHistory.Clear();
        OnPropertyChanged(nameof(IsHistoryEmpty));
    }

    partial void OnSelectedModeIndexChanged(int value)
    {
        RaiseVisibilityProperties();
        RegenerateIfReady();
    }

    partial void OnLengthChanged(int value) => RegenerateIfReady();
    partial void OnIncludeUppercaseChanged(bool value) => RegenerateIfReady();
    partial void OnIncludeLowercaseChanged(bool value) => RegenerateIfReady();
    partial void OnIncludeDigitsChanged(bool value) => RegenerateIfReady();
    partial void OnIncludeSymbolsChanged(bool value) => RegenerateIfReady();
    partial void OnExcludeAmbiguousChanged(bool value) => RegenerateIfReady();
    partial void OnCliSafeChanged(bool value) => RegenerateIfReady();
    partial void OnLayoutSafeChanged(bool value) => RegenerateIfReady();
    partial void OnCustomSpecialsChanged(string value) => RegenerateIfReady();
    partial void OnSyllableLengthChanged(int value) => RegenerateIfReady();
    partial void OnSyllableCaseIndexChanged(int value) => RegenerateIfReady();
    partial void OnSyllableDigitsChanged(int value) => RegenerateIfReady();
    partial void OnSyllableSpecialsChanged(int value) => RegenerateIfReady();
    partial void OnSyllablePlacementIndexChanged(int value) => RegenerateIfReady();
    partial void OnSyllableSeparatorChanged(string value) => RegenerateIfReady();
    partial void OnSyllableCvcChanged(bool value) => RegenerateIfReady();
    partial void OnPassphraseWordCountChanged(int value) => RegenerateIfReady();
    partial void OnPassphraseSeparatorChanged(string value) => RegenerateIfReady();
    partial void OnPassphraseLanguageIndexChanged(int value) => RegenerateIfReady();
    partial void OnPassphraseCapitalizeChanged(bool value) => RegenerateIfReady();
    partial void OnPassphraseAddDigitChanged(bool value) => RegenerateIfReady();
    partial void OnPassphraseAddSpecialChanged(bool value) => RegenerateIfReady();
    partial void OnPassphrasePlacementIndexChanged(int value) => RegenerateIfReady();

    private void RegenerateIfReady()
    {
        if (!_isInitialized || _isSuspended)
        {
            return;
        }

        GenerateCore();
    }

    private void RaiseVisibilityProperties()
    {
        OnPropertyChanged(nameof(CurrentMode));
        OnPropertyChanged(nameof(IsRandomMode));
        OnPropertyChanged(nameof(IsSyllableMode));
        OnPropertyChanged(nameof(IsPassphraseMode));
        OnPropertyChanged(nameof(ShowLayoutSafe));
        OnPropertyChanged(nameof(ShowExcludeAmbiguous));
        OnPropertyChanged(nameof(ShowPhonetic));
        OnPropertyChanged(nameof(ShowSyllableStructure));
        OnPropertyChanged(nameof(ShowStrength));
        OnPropertyChanged(nameof(ShowSyllablePlacement));
        OnPropertyChanged(nameof(ShowPassphrasePlacement));
        OnPropertyChanged(nameof(HasActiveSpecials));
    }

    private void GenerateRandomPassword()
    {
        SyllableStructureText = string.Empty;
        SyllableTotalLength = 0;

        var charset = BuildCharset();
        if (charset.Length == 0)
        {
            SetEmptyOutput();
            return;
        }

        var password = new StringBuilder(Length);
        for (var i = 0; i < Length; i++)
        {
            password.Append(charset[CryptoRandomInt(charset.Length)]);
        }

        var finalPassword = password.ToString();
        GeneratedPassword = finalPassword;

        var entropyPerChar = Math.Log2(charset.Length);
        var totalEntropy = entropyPerChar * Length;
        UpdateStrengthIndicator(totalEntropy);
        UpdatePhoneticDisplay(finalPassword);
    }

    private string BuildCharset()
    {
        var sb = new StringBuilder();
        if (IncludeUppercase) sb.Append(UppercaseChars);
        if (IncludeLowercase) sb.Append(LowercaseChars);
        if (IncludeDigits) sb.Append(DigitChars);
        if (IncludeSymbols)
        {
            var effectiveSymbols = GetEffectiveSymbols();
            if (effectiveSymbols.Length > 0)
            {
                sb.Append(effectiveSymbols);
            }
        }

        if (ExcludeAmbiguous)
        {
            var charset = sb.ToString();
            sb.Clear();
            foreach (var c in charset)
            {
                if (!AmbiguousChars.Contains(c))
                {
                    sb.Append(c);
                }
            }
        }

        if (LayoutSafe)
        {
            var charset = sb.ToString();
            sb.Clear();
            foreach (var c in charset)
            {
                if (!LayoutUnsafeChars.Contains(c))
                {
                    sb.Append(c);
                }
            }
        }

        return sb.ToString();
    }

    private string GetEffectiveSymbols()
    {
        bool useCustom = CurrentMode == GeneratorMode.Random
            ? IncludeSymbols && !string.IsNullOrWhiteSpace(CustomSpecials)
            : !string.IsNullOrWhiteSpace(CustomSpecials);

        var symbols = useCustom
            ? new string(CustomSpecials.Where(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)).Distinct().ToArray())
            : DefaultSymbolChars;

        if (CliSafe)
        {
            symbols = new string(symbols.Where(c => !ShellDangerousChars.Contains(c)).ToArray());
        }

        return symbols;
    }

    private void GenerateSyllablePassword()
    {
        var caseMode = (SyllableCase)SyllableCaseIndex;
        var placement = (Placement)SyllablePlacementIndex;
        var effectiveSymbols = GetEffectiveSymbols();
        var separator = SyllableSeparator;
        var useCvc = SyllableCvc;

        var consonants = LayoutSafe ? LayoutSafeConsonants : Consonants;
        var vowels = LayoutSafe ? LayoutSafeVowels : Vowels;
        var endings = LayoutSafe
            ? EndingConsonants.Where(c => !LayoutUnsafeChars.Contains(c[0])).ToArray()
            : EndingConsonants;

        var groups = new List<string>();
        var totalSylChars = 0;
        var cvcCount = 0;
        while (totalSylChars < SyllableLength)
        {
            var consonant = consonants[CryptoRandomInt(consonants.Length)];
            var vowel = vowels[CryptoRandomInt(vowels.Length)];
            var remaining = SyllableLength - totalSylChars;

            if (useCvc && remaining >= 3 && CryptoRandomInt(2) == 0)
            {
                var ending = endings[CryptoRandomInt(endings.Length)];
                groups.Add(consonant + vowel + ending);
                totalSylChars += 3;
                cvcCount++;
            }
            else if (remaining >= 2)
            {
                groups.Add(consonant + vowel);
                totalSylChars += 2;
            }
            else
            {
                groups.Add(consonant);
                totalSylChars += 1;
            }
        }

        if (totalSylChars > SyllableLength && groups.Count > 0)
        {
            var excess = totalSylChars - SyllableLength;
            var last = groups[^1];
            groups[^1] = last[..^excess];
        }

        var charIndex = 0;
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            var sb = new StringBuilder(group.Length);
            for (var charPos = 0; charPos < group.Length; charPos++)
            {
                var ch = group[charPos];
                switch (caseMode)
                {
                    case SyllableCase.Upper:
                        ch = char.ToUpperInvariant(ch);
                        break;
                    case SyllableCase.Title:
                        if (groupIndex == 0 && charPos == 0) ch = char.ToUpperInvariant(ch);
                        break;
                    case SyllableCase.Mixed:
                        if (CryptoRandomInt(4) == 0) ch = char.ToUpperInvariant(ch);
                        break;
                    case SyllableCase.Alternating:
                        ch = charIndex % 2 == 0 ? ch : char.ToUpperInvariant(ch);
                        break;
                    case SyllableCase.WordCase:
                        if (charPos == 0) ch = char.ToUpperInvariant(ch);
                        break;
                    case SyllableCase.Inverse:
                        ch = char.ToUpperInvariant(ch);
                        break;
                }

                sb.Append(ch);
                charIndex++;
            }

            groups[groupIndex] = sb.ToString();
        }

        if (caseMode == SyllableCase.Inverse && groups.Count > 0)
        {
            var last = groups[^1];
            if (last.Length > 0)
            {
                groups[^1] = last[..^1] + char.ToLowerInvariant(last[^1]);
            }
        }

        var structure = string.Join(" \u00b7 ", groups);
        var joined = string.Join(separator, groups);
        var chars = new List<char>(joined);
        InsertExtras(chars, SyllableDigits, SyllableSpecials, placement, effectiveSymbols);

        var finalPassword = new string(chars.ToArray());
        GeneratedPassword = finalPassword;
        SyllableTotalLength = finalPassword.Length;

        if (SyllableDigits > 0 || SyllableSpecials > 0)
        {
            structure += $"  + {SyllableDigits}# {SyllableSpecials}!";
        }

        SyllableStructureText = structure;

        var cvPool = consonants.Length * vowels.Length;
        var cvcPool = consonants.Length * vowels.Length * endings.Length;
        var cvGroups = groups.Count - cvcCount;
        var entropy = Math.Log2(cvPool) * cvGroups + Math.Log2(cvcPool) * cvcCount;
        if (useCvc) entropy += groups.Count;
        if (SyllableDigits > 0) entropy += Math.Log2(DigitChars.Length) * SyllableDigits;
        if (SyllableSpecials > 0 && effectiveSymbols.Length > 0) entropy += Math.Log2(effectiveSymbols.Length) * SyllableSpecials;
        if (caseMode == SyllableCase.Mixed) entropy += groups.Count;

        UpdateStrengthIndicator(entropy);
        UpdatePhoneticDisplay(finalPassword);
    }

    private void InsertExtras(List<char> chars, int digitCount, int specialCount, Placement placement, string symbols)
    {
        var extras = new List<char>();
        for (var i = 0; i < digitCount; i++)
        {
            extras.Add(DigitChars[CryptoRandomInt(DigitChars.Length)]);
        }

        if (symbols.Length > 0)
        {
            for (var i = 0; i < specialCount; i++)
            {
                extras.Add(symbols[CryptoRandomInt(symbols.Length)]);
            }
        }

        switch (placement)
        {
            case Placement.Start:
                chars.InsertRange(0, extras);
                break;
            case Placement.End:
                chars.AddRange(extras);
                break;
            case Placement.Middle:
                chars.InsertRange(chars.Count / 2, extras);
                break;
            default:
                foreach (var extra in extras)
                {
                    chars.Insert(CryptoRandomInt(chars.Count + 1), extra);
                }
                break;
        }
    }

    private void GeneratePassphrase()
    {
        SyllableStructureText = string.Empty;
        SyllableTotalLength = 0;

        var isFrench = PassphraseLanguageIndex == 1;
        var wordList = isFrench ? _frenchWords : _englishWords;
        if (wordList.Length == 0)
        {
            SetEmptyOutput();
            return;
        }

        var words = new string[PassphraseWordCount];
        var usedIndices = new HashSet<int>();
        for (var i = 0; i < PassphraseWordCount; i++)
        {
            int index;
            if (usedIndices.Count < wordList.Length)
            {
                do
                {
                    index = CryptoRandomInt(wordList.Length);
                }
                while (usedIndices.Contains(index));
            }
            else
            {
                index = CryptoRandomInt(wordList.Length);
            }

            usedIndices.Add(index);
            var word = wordList[index];
            if (PassphraseCapitalize && word.Length > 0)
            {
                word = char.ToUpperInvariant(word[0]) + word[1..];
            }

            words[i] = word;
        }

        var passphraseChars = new List<char>(string.Join(PassphraseSeparator, words));
        var effectiveSymbols = GetEffectiveSymbols();
        var placement = (Placement)PassphrasePlacementIndex;
        InsertExtras(
            passphraseChars,
            PassphraseAddDigit ? 1 : 0,
            PassphraseAddSpecial ? 1 : 0,
            placement,
            effectiveSymbols);

        var finalPassword = new string(passphraseChars.ToArray());
        GeneratedPassword = finalPassword;

        var entropy = Math.Log2(wordList.Length) * PassphraseWordCount;
        if (PassphraseAddDigit) entropy += Math.Log2(DigitChars.Length);
        if (PassphraseAddSpecial && effectiveSymbols.Length > 0) entropy += Math.Log2(effectiveSymbols.Length);

        UpdateStrengthIndicator(entropy);
        UpdatePhoneticDisplay(finalPassword);
    }

    private void UpdateStrengthIndicator(double entropy)
    {
        if (string.IsNullOrEmpty(GeneratedPassword))
        {
            StrengthLevel = 0;
            StrengthText = string.Empty;
            StrengthPercent = 0;
            CrackTimeText = string.Empty;
            IssuesText = string.Empty;
            return;
        }

        string strengthKey;
        double widthPercent;
        int level;
        switch (entropy)
        {
            case < 20:
                strengthKey = "ToolPwdGenStrengthCritical";
                widthPercent = 0.10;
                level = 0;
                break;
            case < 40:
                strengthKey = "ToolPwdGenStrengthWeak";
                widthPercent = 0.25;
                level = 1;
                break;
            case < 60:
                strengthKey = "ToolPwdGenStrengthFair";
                widthPercent = 0.50;
                level = 2;
                break;
            case < 80:
                strengthKey = "ToolPwdGenStrengthGood";
                widthPercent = 0.75;
                level = 3;
                break;
            default:
                strengthKey = "ToolPwdGenStrengthStrong";
                widthPercent = 1.0;
                level = 4;
                break;
        }

        StrengthLevel = level;
        StrengthPercent = widthPercent;
        StrengthText = $"{L(strengthKey)} ({entropy:F0} {L("ToolPwdGenBits")})";
        UpdateCrackTimeEstimate(entropy);
        UpdateIssuesList();
    }

    private void UpdateCrackTimeEstimate(double entropy)
    {
        if (entropy <= 0)
        {
            CrackTimeText = string.Empty;
            return;
        }

        var totalCombinations = Math.Pow(2, Math.Min(entropy, 256));
        var secondsAvg = totalCombinations / (2 * BruteForceGuessesPerSecond);

        string timeStr;
        if (secondsAvg < 1)
        {
            timeStr = L("ToolPwdGenCrackInstant");
        }
        else if (secondsAvg < 60)
        {
            timeStr = string.Format(L("ToolPwdGenCrackSeconds"), (int)secondsAvg);
        }
        else if (secondsAvg < 3600)
        {
            timeStr = string.Format(L("ToolPwdGenCrackMinutes"), (int)(secondsAvg / 60));
        }
        else if (secondsAvg < 86400)
        {
            timeStr = string.Format(L("ToolPwdGenCrackHours"), (int)(secondsAvg / 3600));
        }
        else if (secondsAvg < 365.25 * 86400)
        {
            timeStr = string.Format(L("ToolPwdGenCrackDays"), (int)(secondsAvg / 86400));
        }
        else if (secondsAvg < 100 * 365.25 * 86400)
        {
            timeStr = string.Format(L("ToolPwdGenCrackYears"), (int)(secondsAvg / (365.25 * 86400)));
        }
        else if (secondsAvg < 1_000_000 * 365.25 * 86400)
        {
            timeStr = string.Format(L("ToolPwdGenCrackCenturies"), (int)(secondsAvg / (100 * 365.25 * 86400)));
        }
        else
        {
            timeStr = L("ToolPwdGenCrackForever");
        }

        CrackTimeText = string.Format(L("ToolPwdGenCrackTime"), timeStr);
    }

    private void UpdateIssuesList()
    {
        if (string.IsNullOrEmpty(GeneratedPassword))
        {
            IssuesText = string.Empty;
            return;
        }

        var issues = new List<string>();
        if (GeneratedPassword.Length < 8)
        {
            issues.Add(L("ToolPwdGenIssueTooShort"));
        }

        if (CurrentMode == GeneratorMode.Random)
        {
            if (IncludeUppercase && !GeneratedPassword.Any(char.IsUpper))
            {
                issues.Add(L("ToolPwdGenIssueNoUpper"));
            }

            if (IncludeLowercase && !GeneratedPassword.Any(char.IsLower))
            {
                issues.Add(L("ToolPwdGenIssueNoLower"));
            }

            if (IncludeDigits && !GeneratedPassword.Any(char.IsDigit))
            {
                issues.Add(L("ToolPwdGenIssueNoDigit"));
            }

            if (IncludeSymbols)
            {
                var effectiveSymbols = GetEffectiveSymbols();
                if (effectiveSymbols.Length > 0 && !GeneratedPassword.Any(c => effectiveSymbols.Contains(c)))
                {
                    issues.Add(L("ToolPwdGenIssueNoSpecial"));
                }
            }
        }

        IssuesText = issues.Count > 0 ? string.Join("  \u2022  ", issues) : string.Empty;
    }

    private void UpdatePhoneticDisplay(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > PhoneticMaxLength)
        {
            PhoneticText = string.Empty;
            return;
        }

        var parts = new List<string>();
        foreach (var c in password)
        {
            if (char.IsLetter(c))
            {
                var upperKey = char.ToUpperInvariant(c);
                if (NatoAlphabet.TryGetValue(upperKey, out var nato))
                {
                    parts.Add(char.IsUpper(c) ? nato.ToUpperInvariant() : nato.ToLowerInvariant());
                }
                else
                {
                    parts.Add(c.ToString());
                }
            }
            else if (char.IsDigit(c))
            {
                parts.Add(NatoAlphabet.TryGetValue(c, out var digitWord)
                    ? $"{c}:{digitWord}"
                    : c.ToString());
            }
            else
            {
                parts.Add(SpecialCharNames.TryGetValue(c, out var name)
                    ? $"{c}:{name}"
                    : c.ToString());
            }
        }

        PhoneticText = string.Join(" - ", parts);
    }

    private void SetEmptyOutput()
    {
        GeneratedPassword = string.Empty;
        PhoneticText = string.Empty;
        StrengthLevel = 0;
        StrengthText = string.Empty;
        StrengthPercent = 0;
        CrackTimeText = string.Empty;
        IssuesText = string.Empty;
        SyllableStructureText = string.Empty;
        SyllableTotalLength = 0;
        RaiseVisibilityProperties();
    }

    private void AddToHistory(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        PasswordHistory.Remove(password);
        PasswordHistory.Insert(0, password);

        while (PasswordHistory.Count > HistoryMaxSize)
        {
            PasswordHistory.RemoveAt(PasswordHistory.Count - 1);
        }

        OnPropertyChanged(nameof(IsHistoryEmpty));
    }

    /// <summary>
    /// Returns a cryptographically random integer in the range [0, exclusiveMax).
    /// </summary>
    private static int CryptoRandomInt(int exclusiveMax) => RandomNumberGenerator.GetInt32(exclusiveMax);

    private string L(string key) => _localizer?[key] ?? key;

    private List<PasswordPreset> LoadCustomPresets()
    {
        if (_cachedPresets is not null)
        {
            return _cachedPresets;
        }

        return _cachedPresets = _presetStorage.Load();
    }

    private void SaveCustomPresets(List<PasswordPreset> presets)
    {
        _cachedPresets = null;
        _presetStorage.Save(presets);
    }

    private void LoadWordLists()
    {
        _englishWords = LoadWordListFile("wordlist_en.txt", FallbackEnglishWords);
        _frenchWords = LoadWordListFile("wordlist_fr.txt", FallbackFrenchWords);
    }

    private static string[] LoadWordListFile(string fileName, string[] fallback)
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var path = Path.Combine(appDir, "Assets", fileName);
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8)
                    .Select(l => l.Trim().ToLowerInvariant())
                    .Where(l => l.Length >= 3 && l.Length <= 12)
                    .Distinct()
                    .ToArray();
                if (lines.Length >= 50)
                {
                    return lines;
                }
            }
        }
        catch
        {
            FileLogger.Warn($"[PasswordGenerator] Failed to load word list: {fileName}");
        }

        return fallback;
    }
}
