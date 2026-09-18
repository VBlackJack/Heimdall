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
using System.Globalization;
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

        /// <summary>
        /// Counts, written since the passphrase stopped being limited to one of each. A file
        /// written before that carries neither, so they start below zero and
        /// <see cref="ApplyPreset"/> falls back to the two flags above. Both are still
        /// written, so a preset saved here still says something to an older build.
        /// </summary>
        public int PpDigits { get; set; } = -1;
        public int PpSpecials { get; set; } = -1;
        public int PpCase { get; set; } = -1;
        public int PpPlacement { get; set; }
        public string LeetBaseWord { get; set; } = string.Empty;
        public bool LeetRandomWord { get; set; } = true;
        public bool LeetFullSubstitution { get; set; } = true;
        public int LeetDigits { get; set; } = 2;
        public int LeetSpecials { get; set; } = 1;
        public int LeetPlacement { get; set; }
        public int LeetCase { get; set; }
        public int EntropyFloor { get; set; }
        public string CaseBlocks { get; set; } = DefaultCaseBlocks;
        public bool CaseBlocksAutoSync { get; set; } = true;
        public string DigitPositions { get; set; } = string.Empty;
        public string SpecialPositions { get; set; } = string.Empty;
    }

    internal enum GeneratorMode
    {
        Random,
        Syllable,
        Passphrase,
        Leet
    }

    /// <summary>
    /// How a generated word is cased. Appended to, never reordered: the index is persisted
    /// inside saved presets.
    /// </summary>
    internal enum SyllableCase
    {
        Mixed,
        Lower,
        Upper,
        Title,
        Alternating,
        WordCase,
        Inverse,

        /// <summary>Cases each unit from <see cref="CaseBlocks"/>, one token per unit.</summary>
        Blocks
    }

    /// <summary>
    /// Where the digits and the special characters go. Appended to, never reordered: the index
    /// is persisted inside saved presets.
    /// </summary>
    internal enum Placement
    {
        Random,
        Start,
        End,
        Middle,

        /// <summary>One position per character, each one set on the placement bar.</summary>
        Positions
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

    /// <summary>
    /// The letters a leet password rewrites, and what each one becomes. The table is fixed and
    /// public knowledge: rewriting a word this way hides it from nobody and is worth no entropy,
    /// which is why <see cref="GenerateLeetPassword"/> credits the substitution itself with
    /// nothing and the word it starts from with the size of the list it was drawn out of.
    /// </summary>
    private static readonly Dictionary<char, char> LeetSubstitutions = new()
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

    /// <summary>
    /// What one letter is worth under <see cref="SyllableCase.Mixed"/>, which uppercases a
    /// character one time in four: -(0.25*log2(0.25) + 0.75*log2(0.75)), rounded down. The
    /// syllable generator credits mixed case one bit per syllable instead, which is the same
    /// deliberate understatement applied to a coarser unit.
    /// </summary>
    private const double MixedCaseBitsPerLetter = 0.81;

    /// <summary>
    /// What a case block can say: <c>U</c> uppercases its unit, <c>l</c> lowercases it, and
    /// <c>T</c> uppercases the first letter of it. The pattern repeats when the password has
    /// more units than the pattern has blocks.
    /// </summary>
    /// <remarks>
    /// A block pattern is chosen, not drawn, so it is worth no entropy at all and the strength
    /// figure credits it with nothing. Mixed case is the only case mode that is paid for,
    /// because it is the only one that is random.
    /// </remarks>
    internal const string CaseBlockTokens = "UlT";
    internal const string DefaultCaseBlocks = "Tl";
    internal const int MinimumCaseBlocks = 1;
    internal const int MaximumCaseBlocks = 10;

    /// <summary>Characters one syllable block covers, which is one open syllable.</summary>
    private const int CharactersPerSyllableBlock = 2;

    /// <summary>
    /// A position on the placement bar, as a percentage of the password built so far, kept to
    /// one decimal because that is finer than a bar of any usable width can be dragged.
    /// </summary>
    private const double MinimumPositionPercent = 0;
    private const double MaximumPositionPercent = 100;
    private const int PositionPercentDecimals = 1;

    /// <summary>
    /// The entropy floors the box offers, in bits, the first meaning no floor at all. A floor
    /// is a promise about the weakest password the tool will hand out, so the figures are the
    /// ones people quote: 60 for something disposable, 80 for an account, 100 and 128 for a key
    /// that has to outlive the hardware.
    /// </summary>
    internal static readonly int[] EntropyFloorChoices = [0, 60, 80, 100, 128];

    /// <summary>
    /// The ceilings the floor may raise a mode's own size control to, each one the maximum of
    /// the slider a person drags by hand. The floor moves that control and stops there: it
    /// never reaches past what the interface itself allows.
    /// </summary>
    private const int MaximumLength = 128;
    private const int MaximumSyllableLength = 32;
    private const int SyllableLengthStep = 2;
    private const int MaximumPassphraseWordCount = 8;
    private const int MaximumLeetExtras = 6;


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

    internal static readonly string[] FallbackSpanishWords =
    [
        "abeja","acero","aguja","aldea","ancla","anillo","arcilla","ardilla","arena","arroyo",
        "ballena","bandera","barco","bodega","bosque","brisa","bronce","caballo","cadena","calabaza",
        "camino","campana","cantera","caracol","cascada","castillo","cereza","cisne","colmena","cometa",
        "corcho","cordel","cristal","cuarzo","cueva","cumbre","desierto","diamante","eclipse","encina",
        "esmeralda","espuma","estrella","fogata","frambuesa","gaviota","girasol","granito","hoguera","volcan"
    ];

    internal static readonly string[] FallbackLatinWords =
    [
        "aqua","arbor","ardor","astrum","aurora","avis","bellum","caelum","campus","candela",
        "carmen","castrum","causa","civis","clamor","corona","corpus","cursus","decus","dominus",
        "donum","ferrum","fides","flamma","flumen","fortuna","forum","fulmen","gloria","gratia",
        "herba","hortus","ignis","imperium","insula","lumen","luna","magister","mare","memoria",
        "navis","nebula","nomen","oculus","populus","portus","ratio","regnum","sagitta","scutum"
    ];

    /// <summary>One passphrase language: its locale, its label, and where its words come from.</summary>
    /// <param name="Locale">The locale code that selects this language on first use.</param>
    /// <param name="LabelKey">The catalogue key naming the language in the language box.</param>
    /// <param name="FileName">The word list under <c>Assets/</c>.</param>
    /// <param name="Fallback">The words used when that file is missing or unreadable.</param>
    internal readonly record struct PassphraseLanguage(
        string Locale,
        string LabelKey,
        string FileName,
        string[] Fallback);

    /// <summary>The passphrase languages, in the order the language box offers them.</summary>
    /// <remarks>
    /// <para>The selected index is persisted inside saved presets, so a language is appended here
    /// and never inserted: reordering this table would silently repoint every preset already on
    /// disk at a different language.</para>
    /// <para>Three places used to spell this mapping out separately, and they disagreed the moment
    /// a third language existed: the initial selection read the locale, the view filled the box by
    /// hand, and the generator forked on <c>index == 1</c>, which sent every index above one to the
    /// English list without failing anything.</para>
    /// </remarks>
    internal static readonly PassphraseLanguage[] PassphraseLanguages =
    [
        new("en", "ToolPwdGenLangEnglish", "wordlist_en.txt", FallbackEnglishWords),
        new("fr", "ToolPwdGenLangFrench", "wordlist_fr.txt", FallbackFrenchWords),
        new("es", "ToolPwdGenLangSpanish", "wordlist_es.txt", FallbackSpanishWords),
        new("la", "ToolPwdGenLangLatin", "wordlist_la.txt", FallbackLatinWords),
    ];

    /// <summary>
    /// The language index a profile in <paramref name="locale"/> starts on, English when the
    /// interface language has no word list of its own.
    /// </summary>
    internal static int PassphraseLanguageIndexFor(string? locale)
    {
        for (int index = 0; index < PassphraseLanguages.Length; index++)
        {
            if (string.Equals(PassphraseLanguages[index].Locale, locale, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private LocalizationManager? _localizer;
    private bool _isInitialized;
    private bool _isSuspended;
    private Func<string, string, Task<bool>>? _confirmAsync;
    private readonly IPasswordPresetStorage _presetStorage;
    private List<PasswordPreset>? _cachedPresets;
    private string[][] _wordLists = [];

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
    [ObservableProperty] private int _passphraseCaseIndex = (int)SyllableCase.WordCase;
    [ObservableProperty] private int _passphraseDigits = 1;
    [ObservableProperty] private int _passphraseSpecials = 1;
    [ObservableProperty] private int _passphrasePlacementIndex;

    [ObservableProperty] private string _leetBaseWord = string.Empty;
    [ObservableProperty] private bool _leetRandomWord = true;
    [ObservableProperty] private bool _leetFullSubstitution = true;
    [ObservableProperty] private int _leetDigits = 2;
    [ObservableProperty] private int _leetSpecials = 1;
    [ObservableProperty] private int _leetPlacementIndex;
    [ObservableProperty] private int _leetCaseIndex;
    [ObservableProperty] private string _leetWordSource = string.Empty;

    [ObservableProperty] private int _entropyFloorIndex;

    [ObservableProperty] private string _caseBlocks = DefaultCaseBlocks;
    [ObservableProperty] private bool _caseBlocksAutoSync = true;

    /// <summary>
    /// The positions of the digits and of the specials, in percent, comma separated. One entry
    /// per character the current mode inserts, held in that shape so a preset carries them as
    /// text and a test can state them in one string.
    /// </summary>
    [ObservableProperty] private string _digitPositions = string.Empty;
    [ObservableProperty] private string _specialPositions = string.Empty;

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
            3 => GeneratorMode.Leet,
            _ => GeneratorMode.Random
        };

    public bool IsRandomMode => CurrentMode == GeneratorMode.Random;
    public bool IsSyllableMode => CurrentMode == GeneratorMode.Syllable;
    public bool IsPassphraseMode => CurrentMode == GeneratorMode.Passphrase;
    public bool IsLeetMode => CurrentMode == GeneratorMode.Leet;

    /// <summary>
    /// Layout-safe drops the letters that move on an AZERTY keyboard, which only the generators
    /// that choose their own letters can honour. A passphrase and a leet password take theirs
    /// from a word list, so the box would promise a restriction neither one applies.
    /// </summary>
    public bool ShowLayoutSafe =>
        CurrentMode is GeneratorMode.Random or GeneratorMode.Syllable;
    public bool ShowExcludeAmbiguous => CurrentMode == GeneratorMode.Random;
    public bool ShowPhonetic => !string.IsNullOrEmpty(PhoneticText);
    public bool ShowSyllableStructure => IsSyllableMode && !string.IsNullOrEmpty(SyllableStructureText);
    public bool ShowStrength => !string.IsNullOrEmpty(GeneratedPassword);
    public bool ShowSyllablePlacement => IsSyllableMode && (SyllableDigits > 0 || SyllableSpecials > 0);
    public bool ShowPassphrasePlacement =>
        IsPassphraseMode && (PassphraseDigits > 0 || PassphraseSpecials > 0);
    public bool ShowLeetPlacement =>
        IsLeetMode && (EffectiveLeetDigits > 0 || EffectiveLeetSpecials > 0);
    public bool ShowLeetBaseWord => IsLeetMode && !LeetRandomWord;
    public bool ShowLeetWordSource => IsLeetMode && !string.IsNullOrEmpty(LeetWordSource);
    public bool ShowFloorNotice => !string.IsNullOrEmpty(FloorNoticeText);

    /// <summary>The case mode of the mode on screen, or <c>null</c> where there is none.</summary>
    internal SyllableCase? CurrentCaseMode => CurrentMode switch
    {
        GeneratorMode.Syllable => CaseModeAt(SyllableCaseIndex),
        GeneratorMode.Passphrase => CaseModeAt(PassphraseCaseIndex),
        GeneratorMode.Leet => CaseModeAt(LeetCaseIndex),
        _ => null
    };

    public bool ShowCaseBlocks => CurrentCaseMode == SyllableCase.Blocks;

    /// <summary>The placement of the mode on screen, or <c>null</c> where there is none.</summary>
    internal Placement? CurrentPlacement => CurrentMode switch
    {
        GeneratorMode.Syllable => PlacementAt(SyllablePlacementIndex),
        GeneratorMode.Passphrase => PlacementAt(PassphrasePlacementIndex),
        GeneratorMode.Leet => PlacementAt(LeetPlacementIndex),
        _ => null
    };

    internal static Placement PlacementAt(int index) =>
        (Placement)Math.Clamp(index, 0, (int)Placement.Positions);

    /// <summary>How many digits the mode on screen inserts.</summary>
    internal int CurrentDigitCount => CurrentMode switch
    {
        GeneratorMode.Syllable => SyllableDigits,
        GeneratorMode.Passphrase => PassphraseDigits,
        GeneratorMode.Leet => EffectiveLeetDigits,
        _ => 0
    };

    /// <summary>How many special characters the mode on screen inserts.</summary>
    internal int CurrentSpecialCount => GetEffectiveSymbols().Length == 0 ? 0 : CurrentMode switch
    {
        GeneratorMode.Syllable => SyllableSpecials,
        GeneratorMode.Passphrase => PassphraseSpecials,
        GeneratorMode.Leet => EffectiveLeetSpecials,
        _ => 0
    };

    public bool ShowPlacementBar =>
        CurrentPlacement == Placement.Positions && CurrentDigitCount + CurrentSpecialCount > 0;

    /// <summary>
    /// Keeping the block count equal to the syllable count only means anything where the number
    /// of units is known before the password is built. A leet password has as many units as the
    /// drawn word has letters, which is not known until it is drawn.
    /// </summary>
    public bool ShowCaseBlocksAutoSync =>
        ShowCaseBlocks && (IsSyllableMode || IsPassphraseMode);

    internal static SyllableCase CaseModeAt(int index) =>
        (SyllableCase)Math.Clamp(index, 0, (int)SyllableCase.Blocks);

    /// <summary>The floor in bits, zero when the box is on its first entry.</summary>
    internal int EntropyFloorBits =>
        EntropyFloorChoices[Math.Clamp(EntropyFloorIndex, 0, EntropyFloorChoices.Length - 1)];

    /// <summary>The figure the last generation advertised, in bits.</summary>
    internal double LastEntropyBits { get; private set; }

    /// <summary>
    /// The sizes the last generation actually ran at. They equal the controls the operator set
    /// unless the floor needed more, and the controls themselves are never written to.
    /// </summary>
    internal int EffectiveLength { get; private set; }
    internal int EffectiveSyllableLength { get; private set; }
    internal int EffectivePassphraseWordCount { get; private set; }
    internal int EffectiveLeetDigits { get; private set; }
    internal int EffectiveLeetSpecials { get; private set; }

    /// <summary>Set when the floor cannot be carried by these settings at their maximum.</summary>
    internal bool FloorOutOfReach { get; private set; }

    /// <summary>What the floor did, in the operator's own units, or empty when it did nothing.</summary>
    [ObservableProperty] private string _floorNoticeText = string.Empty;
    public bool HasActiveSpecials => CurrentMode switch
    {
        GeneratorMode.Random => IncludeSymbols,
        GeneratorMode.Syllable => SyllableSpecials > 0,
        GeneratorMode.Passphrase => PassphraseSpecials > 0,
        GeneratorMode.Leet => EffectiveLeetSpecials > 0,
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

        PassphraseLanguageIndex = PassphraseLanguageIndexFor(localizer?.CurrentLocale);

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
        PpCapitalize = CaseModeAt(PassphraseCaseIndex) == SyllableCase.WordCase,
        PpDigit = PassphraseDigits > 0,
        PpSpecial = PassphraseSpecials > 0,
        PpDigits = PassphraseDigits,
        PpSpecials = PassphraseSpecials,
        PpCase = PassphraseCaseIndex,
        PpPlacement = PassphrasePlacementIndex,
        LeetBaseWord = LeetBaseWord,
        LeetRandomWord = LeetRandomWord,
        LeetFullSubstitution = LeetFullSubstitution,
        LeetDigits = LeetDigits,
        LeetSpecials = LeetSpecials,
        LeetPlacement = LeetPlacementIndex,
        LeetCase = LeetCaseIndex,
        EntropyFloor = EntropyFloorIndex,
        CaseBlocks = CaseBlocks,
        CaseBlocksAutoSync = CaseBlocksAutoSync,
        DigitPositions = DigitPositions,
        SpecialPositions = SpecialPositions,
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
            // A preset written before the passphrase had counts carries the two flags only.
            PassphraseDigits = preset.PpDigits >= 0 ? preset.PpDigits : (preset.PpDigit ? 1 : 0);
            PassphraseSpecials = preset.PpSpecials >= 0 ? preset.PpSpecials : (preset.PpSpecial ? 1 : 0);
            PassphraseCaseIndex = preset.PpCase >= 0
                ? preset.PpCase
                : (int)(preset.PpCapitalize ? SyllableCase.WordCase : SyllableCase.Lower);
            PassphrasePlacementIndex = preset.PpPlacement;
            LeetBaseWord = preset.LeetBaseWord;
            LeetRandomWord = preset.LeetRandomWord;
            LeetFullSubstitution = preset.LeetFullSubstitution;
            LeetDigits = preset.LeetDigits;
            LeetSpecials = preset.LeetSpecials;
            LeetPlacementIndex = preset.LeetPlacement;
            LeetCaseIndex = preset.LeetCase;
            EntropyFloorIndex = preset.EntropyFloor;
            CaseBlocks = SanitizeCaseBlocks(preset.CaseBlocks);
            CaseBlocksAutoSync = preset.CaseBlocksAutoSync;
            DigitPositions = FormatPositions(ParsePositions(preset.DigitPositions));
            SpecialPositions = FormatPositions(ParsePositions(preset.SpecialPositions));
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
            PassphraseCaseIndex = (int)SyllableCase.WordCase;
            PassphraseDigits = 1;
            PassphraseSpecials = 1;
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
        ResolveFloorSizes();
        ResolvePositionCounts();
        GenerateForCurrentMode();

        RaiseVisibilityProperties();
        AddToHistory(GeneratedPassword);
    }

    private void GenerateForCurrentMode()
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
            case GeneratorMode.Leet:
                GenerateLeetPassword();
                break;
        }
    }

    /// <summary>
    /// Decides the size this generation runs at, which is the operator's own setting unless the
    /// floor needs more, and reports what it decided through the effective sizes.
    /// </summary>
    /// <remarks>
    /// <para><b>The operator's controls are never written to.</b> The first version of this raised
    /// the slider itself, one step at a time, regenerating as it went. That committed each raise
    /// before knowing whether the climb would succeed, so a floor that turned out to be out of
    /// reach left every control pinned at its maximum with no way back: turning the floor off
    /// restored nothing. It also fought the mouse, since a drag writes the length on every mouse
    /// move and the floor wrote it straight back.</para>
    /// <para>What is decided here is decided from the settings alone, never from the password that
    /// came out. A leet password's mixed case and a syllable password's closed syllables are worth
    /// real bits, but a different number of them on every draw, and a floor that read them would
    /// move the size on a click that was only meant to reroll. They are left out of the decision,
    /// which therefore holds for every password these settings can produce, and left in the figure
    /// on display, which describes the one password on screen.</para>
    /// <para>When even the maximum cannot carry the floor, nothing is changed at all and the issue
    /// line says so. A promise that cannot be kept is not kept quietly.</para>
    /// </remarks>
    private void ResolveFloorSizes()
    {
        EffectiveLength = Length;
        EffectiveSyllableLength = SyllableLength;
        EffectivePassphraseWordCount = PassphraseWordCount;
        EffectiveLeetDigits = LeetDigits;
        EffectiveLeetSpecials = LeetSpecials;
        FloorOutOfReach = false;
        FloorNoticeText = string.Empty;

        var floor = EntropyFloorBits;
        if (floor <= 0)
        {
            return;
        }

        switch (CurrentMode)
        {
            case GeneratorMode.Random:
                ResolveRandomFloor(floor);
                break;
            case GeneratorMode.Syllable:
                ResolveSyllableFloor(floor);
                break;
            case GeneratorMode.Passphrase:
                ResolvePassphraseFloor(floor);
                break;
            case GeneratorMode.Leet:
                ResolveLeetFloor(floor);
                break;
        }
    }

    private void ResolveRandomFloor(int floor)
    {
        var charsetSize = BuildCharset().Length;
        var bitsPerCharacter = charsetSize > 1 ? Math.Log2(charsetSize) : 0;
        if (bitsPerCharacter <= 0)
        {
            FloorOutOfReach = true;
            return;
        }

        var required = (int)Math.Ceiling(floor / bitsPerCharacter);
        if (required <= Length)
        {
            return;
        }

        if (required > MaximumLength)
        {
            FloorOutOfReach = true;
            return;
        }

        EffectiveLength = required;
        NoteFloorRaise(Length.ToString(), required.ToString());
    }

    private void ResolveSyllableFloor(int floor)
    {
        for (var candidate = SyllableLength;
             candidate <= MaximumSyllableLength;
             candidate += SyllableLengthStep)
        {
            if (GuaranteedSyllableBits(candidate) < floor)
            {
                continue;
            }

            if (candidate > SyllableLength)
            {
                EffectiveSyllableLength = candidate;
                NoteFloorRaise(SyllableLength.ToString(), candidate.ToString());
            }

            return;
        }

        FloorOutOfReach = true;
    }

    private void ResolvePassphraseFloor(int floor)
    {
        for (var candidate = PassphraseWordCount;
             candidate <= MaximumPassphraseWordCount;
             candidate++)
        {
            if (GuaranteedPassphraseBits(candidate) < floor)
            {
                continue;
            }

            if (candidate > PassphraseWordCount)
            {
                EffectivePassphraseWordCount = candidate;
                NoteFloorRaise(PassphraseWordCount.ToString(), candidate.ToString());
            }

            return;
        }

        FloorOutOfReach = true;
    }

    /// <summary>
    /// A leet password cannot grow its word, so the floor buys digits first and specials after,
    /// and takes the fewest of either that carries the floor.
    /// </summary>
    private void ResolveLeetFloor(int floor)
    {
        var digitRoom = MaximumLeetExtras - LeetDigits;
        var specialRoom = MaximumLeetExtras - LeetSpecials;

        for (var steps = 0; steps <= digitRoom + specialRoom; steps++)
        {
            var digits = LeetDigits + Math.Min(steps, digitRoom);
            var specials = LeetSpecials + Math.Max(0, steps - digitRoom);

            if (GuaranteedLeetBits(digits, specials) < floor)
            {
                continue;
            }

            if (steps > 0)
            {
                EffectiveLeetDigits = digits;
                EffectiveLeetSpecials = specials;
                NoteFloorRaise(
                    $"{LeetDigits}+{LeetSpecials}",
                    $"{digits}+{specials}");
            }

            return;
        }

        FloorOutOfReach = true;
    }

    /// <summary>
    /// The bits a syllable password of this base length carries whatever the dice do: open
    /// syllables only, which is the cheaper of the two shapes per character, and no credit for
    /// mixed case.
    /// </summary>
    private double GuaranteedSyllableBits(int baseLength)
    {
        var consonants = LayoutSafe ? LayoutSafeConsonants : Consonants;
        var vowels = LayoutSafe ? LayoutSafeVowels : Vowels;
        var bits = Math.Log2(consonants.Length * vowels.Length) * (baseLength / 2);
        return bits + ExtrasBits(SyllableDigits, SyllableSpecials);
    }

    private double GuaranteedPassphraseBits(int wordCount)
    {
        var wordList = SelectedWordList();
        if (wordList.Length < 2)
        {
            return 0;
        }

        return Math.Log2(wordList.Length) * wordCount
            + ExtrasBits(PassphraseDigits, PassphraseSpecials);
    }

    /// <summary>
    /// The bits a leet password carries whatever word is drawn: the word itself when it is drawn
    /// rather than typed, and the digits and specials, with nothing credited to the substitutions
    /// or to the case, both of which depend on the letters that turn up.
    /// </summary>
    private double GuaranteedLeetBits(int digits, int specials)
    {
        var typed = SanitizeLeetBaseWord(LeetBaseWord);
        var drawn = LeetRandomWord || typed.Length == 0;
        var wordList = SelectedWordList();
        var wordBits = drawn && wordList.Length > 1 ? Math.Log2(wordList.Length) : 0;

        return wordBits + ExtrasBits(digits, specials);
    }

    private double ExtrasBits(int digits, int specials)
    {
        var bits = digits > 0 ? Math.Log2(DigitChars.Length) * digits : 0;
        var symbols = GetEffectiveSymbols();
        if (specials > 0 && symbols.Length > 0)
        {
            bits += Math.Log2(symbols.Length) * specials;
        }

        return bits;
    }

    private void NoteFloorRaise(string chosen, string used)
        => FloorNoticeText = string.Format(L("ToolPwdGenFloorRaised"), chosen, used);

    /// <summary>
    /// Brings the two position lists to the number of characters the current mode inserts,
    /// spreading any new ones evenly and keeping the ones already placed.
    /// </summary>
    /// <remarks>
    /// Called before every generation rather than from each control that changes a count, so a
    /// list can never be shorter than the characters it has to place, whichever way the count
    /// changed - a slider, a preset, or the strength floor buying a digit of its own.
    /// </remarks>
    private void ResolvePositionCounts()
    {
        var digits = ResizePositions(ParsePositions(DigitPositions), CurrentDigitCount);
        var specials = ResizePositions(ParsePositions(SpecialPositions), CurrentSpecialCount);

        // Writing the two properties from inside a generation would ask for another one, so
        // the regeneration is held off exactly as the strength floor holds it off.
        var wasSuspended = _isSuspended;
        _isSuspended = true;
        try
        {
            DigitPositions = FormatPositions(digits);
            SpecialPositions = FormatPositions(specials);
        }
        finally
        {
            _isSuspended = wasSuspended;
        }
    }

    private static double[] ResizePositions(double[] current, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        if (current.Length == count)
        {
            return current;
        }

        if (current.Length > count)
        {
            return current[..count];
        }

        var spread = DistributeEvenly(count);
        Array.Copy(current, spread, current.Length);
        return spread;
    }

    /// <summary>
    /// Spreads <paramref name="count"/> positions over the bar, each one in the middle of its
    /// own share of it, so one character sits at the centre and none starts out pinned to an
    /// end that the operator has not chosen.
    /// </summary>
    internal static double[] DistributeEvenly(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var spread = new double[count];
        for (var index = 0; index < count; index++)
        {
            spread[index] = ClampPercent((index + 0.5) / count * MaximumPositionPercent);
        }

        return spread;
    }

    /// <summary>
    /// Rounds away from zero rather than to even, so half a tenth of a percent goes the way the
    /// cursor was dragged and matches the rounding that turns a percent into an index.
    /// </summary>
    internal static double ClampPercent(double percent) =>
        Math.Round(
            Math.Clamp(percent, MinimumPositionPercent, MaximumPositionPercent),
            PositionPercentDecimals,
            MidpointRounding.AwayFromZero);

    internal static double[] ParsePositions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => double.TryParse(
                entry,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) ? ClampPercent(value) : (double?)null)
            .OfType<double>()
            .ToArray();
    }

    internal static string FormatPositions(IEnumerable<double> positions) =>
        string.Join(
            ',',
            positions.Select(value => ClampPercent(value).ToString(CultureInfo.InvariantCulture)));

    /// <summary>Moves one cursor of one kind, which is what dragging it does.</summary>
    internal void MovePosition(bool digit, int index, double percent)
    {
        var positions = ParsePositions(digit ? DigitPositions : SpecialPositions);
        if (index < 0 || index >= positions.Length)
        {
            return;
        }

        positions[index] = ClampPercent(percent);
        var text = FormatPositions(positions);
        if (digit)
        {
            DigitPositions = text;
        }
        else
        {
            SpecialPositions = text;
        }
    }

    /// <summary>Spreads every cursor out again, which is what the bar's own button does.</summary>
    internal void DistributePositionsEvenly()
    {
        DigitPositions = FormatPositions(DistributeEvenly(CurrentDigitCount));
        SpecialPositions = FormatPositions(DistributeEvenly(CurrentSpecialCount));
    }

    /// <summary>Adds one block, up to the ten the editor shows.</summary>
    internal void AddCaseBlock()
    {
        if (CaseBlocks.Length >= MaximumCaseBlocks)
        {
            return;
        }

        CaseBlocksAutoSync = false;
        CaseBlocks += CaseBlockTokens[1];
    }

    internal void RemoveCaseBlock()
    {
        if (CaseBlocks.Length <= MinimumCaseBlocks)
        {
            return;
        }

        CaseBlocksAutoSync = false;
        CaseBlocks = CaseBlocks[..^1];
    }

    /// <summary>Draws a token for every block, which is a shortcut, not a source of entropy.</summary>
    internal void RandomizeCaseBlocks()
    {
        var drawn = new StringBuilder(CaseBlocks.Length);
        for (var index = 0; index < CaseBlocks.Length; index++)
        {
            drawn.Append(CaseBlockTokens[CryptoRandomInt(CaseBlockTokens.Length)]);
        }

        CaseBlocks = drawn.ToString();
    }

    /// <summary>Sets every block to one token.</summary>
    internal void SetAllCaseBlocks(char token)
    {
        if (!CaseBlockTokens.Contains(token))
        {
            return;
        }

        CaseBlocks = new string(token, CaseBlocks.Length);
    }

    /// <summary>Moves one block on to the next token, which is what clicking it does.</summary>
    internal void CycleCaseBlock(int index)
    {
        if (index < 0 || index >= CaseBlocks.Length)
        {
            return;
        }

        var current = CaseBlockTokens.IndexOf(CaseBlocks[index]);
        var next = CaseBlockTokens[(current + 1) % CaseBlockTokens.Length];
        var tokens = CaseBlocks.ToCharArray();
        tokens[index] = next;
        CaseBlocks = new string(tokens);
    }

    /// <summary>
    /// Keeps the pattern as long as the password has units, so that a pattern read left to
    /// right lines up with the password read left to right instead of wrapping half way. A
    /// unit is a syllable in the syllable mode and a word in the passphrase; a leet password
    /// has as many as the drawn word has letters, which is not known until it is drawn.
    /// </summary>
    private void SyncCaseBlocksToUnitCount()
    {
        if (!CaseBlocksAutoSync || CurrentCaseMode != SyllableCase.Blocks)
        {
            return;
        }

        var units = CurrentMode switch
        {
            GeneratorMode.Syllable =>
                (int)Math.Ceiling(SyllableLength / (double)CharactersPerSyllableBlock),
            GeneratorMode.Passphrase => PassphraseWordCount,
            _ => 0
        };

        if (units <= 0)
        {
            return;
        }

        var wanted = Math.Clamp(units, MinimumCaseBlocks, MaximumCaseBlocks);

        if (wanted == CaseBlocks.Length)
        {
            return;
        }

        CaseBlocks = wanted < CaseBlocks.Length
            ? CaseBlocks[..wanted]
            : CaseBlocks + new string(CaseBlockTokens[1], wanted - CaseBlocks.Length);
    }

    /// <summary>
    /// A pattern read off disk, held to what the editor can produce: between one and ten blocks,
    /// each one of the three tokens.
    /// </summary>
    internal static string SanitizeCaseBlocks(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return DefaultCaseBlocks;
        }

        var kept = new string(pattern.Where(CaseBlockTokens.Contains).ToArray());
        if (kept.Length == 0)
        {
            return DefaultCaseBlocks;
        }

        return kept.Length > MaximumCaseBlocks ? kept[..MaximumCaseBlocks] : kept;
    }

    /// <summary>Applies the block token for <paramref name="unitIndex"/> to one unit.</summary>
    private string ApplyCaseBlock(string unit, int unitIndex)
    {
        var pattern = SanitizeCaseBlocks(CaseBlocks);
        return pattern[unitIndex % pattern.Length] switch
        {
            'U' => unit.ToUpperInvariant(),
            'T' => unit.Length > 0
                ? char.ToUpperInvariant(unit[0]) + unit[1..].ToLowerInvariant()
                : unit,
            _ => unit.ToLowerInvariant()
        };
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
    partial void OnSyllableLengthChanged(int value)
    {
        SyncCaseBlocksToUnitCount();
        RegenerateIfReady();
    }
    partial void OnSyllableCaseIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowCaseBlocks));
        OnPropertyChanged(nameof(ShowCaseBlocksAutoSync));
        SyncCaseBlocksToUnitCount();
        RegenerateIfReady();
    }
    partial void OnSyllableDigitsChanged(int value) => RegenerateIfReady();
    partial void OnSyllableSpecialsChanged(int value) => RegenerateIfReady();
    partial void OnSyllablePlacementIndexChanged(int value) => RegenerateIfReady();
    partial void OnSyllableSeparatorChanged(string value) => RegenerateIfReady();
    partial void OnSyllableCvcChanged(bool value) => RegenerateIfReady();
    partial void OnPassphraseWordCountChanged(int value)
    {
        SyncCaseBlocksToUnitCount();
        RegenerateIfReady();
    }
    partial void OnPassphraseSeparatorChanged(string value) => RegenerateIfReady();
    partial void OnPassphraseLanguageIndexChanged(int value) => RegenerateIfReady();
    partial void OnPassphraseDigitsChanged(int value) => RegenerateIfReady();
    partial void OnPassphraseSpecialsChanged(int value) => RegenerateIfReady();

    partial void OnPassphraseCaseIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowCaseBlocks));
        OnPropertyChanged(nameof(ShowCaseBlocksAutoSync));
        SyncCaseBlocksToUnitCount();
        RegenerateIfReady();
    }
    partial void OnPassphrasePlacementIndexChanged(int value) => RegenerateIfReady();
    partial void OnLeetBaseWordChanged(string value) => RegenerateIfReady();
    partial void OnLeetFullSubstitutionChanged(bool value) => RegenerateIfReady();
    partial void OnLeetDigitsChanged(int value) => RegenerateIfReady();
    partial void OnLeetSpecialsChanged(int value) => RegenerateIfReady();
    partial void OnLeetPlacementIndexChanged(int value) => RegenerateIfReady();
    partial void OnLeetCaseIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowCaseBlocks));
        OnPropertyChanged(nameof(ShowCaseBlocksAutoSync));
        RegenerateIfReady();
    }
    partial void OnEntropyFloorIndexChanged(int value) => RegenerateIfReady();

    partial void OnCaseBlocksChanged(string value) => RegenerateIfReady();
    partial void OnDigitPositionsChanged(string value) => RegenerateIfReady();
    partial void OnSpecialPositionsChanged(string value) => RegenerateIfReady();

    partial void OnCaseBlocksAutoSyncChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCaseBlocksAutoSync));
        SyncCaseBlocksToUnitCount();
        RegenerateIfReady();
    }

    partial void OnLeetRandomWordChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLeetBaseWord));
        RegenerateIfReady();
    }

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
        OnPropertyChanged(nameof(IsLeetMode));
        OnPropertyChanged(nameof(ShowLayoutSafe));
        OnPropertyChanged(nameof(ShowExcludeAmbiguous));
        OnPropertyChanged(nameof(ShowPhonetic));
        OnPropertyChanged(nameof(ShowSyllableStructure));
        OnPropertyChanged(nameof(ShowStrength));
        OnPropertyChanged(nameof(ShowSyllablePlacement));
        OnPropertyChanged(nameof(ShowPassphrasePlacement));
        OnPropertyChanged(nameof(ShowLeetPlacement));
        OnPropertyChanged(nameof(ShowLeetBaseWord));
        OnPropertyChanged(nameof(ShowLeetWordSource));
        OnPropertyChanged(nameof(ShowFloorNotice));
        OnPropertyChanged(nameof(ShowCaseBlocks));
        OnPropertyChanged(nameof(ShowCaseBlocksAutoSync));
        OnPropertyChanged(nameof(ShowPlacementBar));
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

        var password = new StringBuilder(EffectiveLength);
        for (var i = 0; i < EffectiveLength; i++)
        {
            password.Append(charset[CryptoRandomInt(charset.Length)]);
        }

        var finalPassword = password.ToString();
        GeneratedPassword = finalPassword;

        var entropyPerChar = Math.Log2(charset.Length);
        var totalEntropy = entropyPerChar * EffectiveLength;
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
        while (totalSylChars < EffectiveSyllableLength)
        {
            var consonant = consonants[CryptoRandomInt(consonants.Length)];
            var vowel = vowels[CryptoRandomInt(vowels.Length)];
            var remaining = EffectiveSyllableLength - totalSylChars;

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

        if (totalSylChars > EffectiveSyllableLength && groups.Count > 0)
        {
            var excess = totalSylChars - EffectiveSyllableLength;
            var last = groups[^1];
            groups[^1] = last[..^excess];
        }

        var charIndex = 0;
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];

            // A block covers a whole syllable, which is the unit the structure line shows and
            // the unit a pattern read left to right is meant to line up with.
            if (caseMode == SyllableCase.Blocks)
            {
                groups[groupIndex] = ApplyCaseBlock(group, groupIndex);
                charIndex += group.Length;
                continue;
            }

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

    /// <summary>
    /// Inserts each character at its own position, read as a percentage of the string as it
    /// stands when the insertion starts.
    /// </summary>
    /// <remarks>
    /// The positions are taken against one length, not recomputed as the string grows, so two
    /// cursors set to the same percent stay next to each other instead of drifting apart. The
    /// running offset is what keeps the later insertions where the earlier ones left them.
    /// </remarks>
    private static void InsertAtPositions(
        List<char> chars,
        IReadOnlyList<char> extras,
        IReadOnlyList<double> percents)
    {
        if (extras.Count == 0)
        {
            return;
        }

        var length = chars.Count;
        var placed = extras
            .Select((character, index) => (
                Character: character,
                At: (int)Math.Round(
                    (index < percents.Count ? percents[index] : MaximumPositionPercent)
                    / MaximumPositionPercent * length,
                    MidpointRounding.AwayFromZero),
                Order: index))
            .OrderBy(entry => entry.At)
            .ThenBy(entry => entry.Order)
            .ToList();

        for (var index = 0; index < placed.Count; index++)
        {
            var at = Math.Clamp(placed[index].At + index, 0, chars.Count);
            chars.Insert(at, placed[index].Character);
        }
    }

    private void InsertExtras(List<char> chars, int digitCount, int specialCount, Placement placement, string symbols)
    {
        var digits = new List<char>();
        for (var i = 0; i < digitCount; i++)
        {
            digits.Add(DigitChars[CryptoRandomInt(DigitChars.Length)]);
        }

        var specials = new List<char>();
        if (symbols.Length > 0)
        {
            for (var i = 0; i < specialCount; i++)
            {
                specials.Add(symbols[CryptoRandomInt(symbols.Length)]);
            }
        }

        if (placement == Placement.Positions)
        {
            // The digits go in first and the specials are then placed on the longer string, which
            // is what a bar showing one row above the other reads as: the second row measures the
            // password the first row has already been written into.
            InsertAtPositions(chars, digits, ParsePositions(DigitPositions));
            InsertAtPositions(chars, specials, ParsePositions(SpecialPositions));
            return;
        }

        var extras = new List<char>(digits);
        extras.AddRange(specials);

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

    /// <summary>
    /// Cases one word of a passphrase, where the unit a case mode works on is the word and
    /// <paramref name="wordIndex"/> is its place in the phrase.
    /// </summary>
    /// <remarks>
    /// Title and word case do the same thing here, since a word is the whole unit, and that is
    /// what the box used to call capitalising the words. Inverse uppercases every word but the
    /// last letter of the last one, so it needs to know where it is in the phrase.
    /// </remarks>
    private string ApplyWordCase(string word, int wordIndex)
    {
        if (word.Length == 0)
        {
            return word;
        }

        var caseMode = CaseModeAt(PassphraseCaseIndex);
        if (caseMode == SyllableCase.Blocks)
        {
            return ApplyCaseBlock(word, wordIndex);
        }

        var titled = char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
        return caseMode switch
        {
            SyllableCase.Upper => word.ToUpperInvariant(),
            SyllableCase.Title or SyllableCase.WordCase => titled,
            SyllableCase.Alternating => wordIndex % 2 == 0 ? word.ToLowerInvariant() : word.ToUpperInvariant(),
            SyllableCase.Inverse => wordIndex == EffectivePassphraseWordCount - 1
                ? word[..^1].ToUpperInvariant() + char.ToLowerInvariant(word[^1])
                : word.ToUpperInvariant(),
            SyllableCase.Mixed => new string(word
                .Select(character => CryptoRandomInt(4) == 0
                    ? char.ToUpperInvariant(character)
                    : char.ToLowerInvariant(character))
                .ToArray()),
            _ => word.ToLowerInvariant()
        };
    }

    private void GeneratePassphrase()
    {
        SyllableStructureText = string.Empty;
        SyllableTotalLength = 0;

        var wordList = SelectedWordList();
        if (wordList.Length == 0)
        {
            SetEmptyOutput();
            return;
        }

        var words = new string[EffectivePassphraseWordCount];
        var usedIndices = new HashSet<int>();
        for (var i = 0; i < EffectivePassphraseWordCount; i++)
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
            words[i] = ApplyWordCase(wordList[index], i);
        }

        var passphraseChars = new List<char>(string.Join(PassphraseSeparator, words));
        var effectiveSymbols = GetEffectiveSymbols();
        var placement = PlacementAt(PassphrasePlacementIndex);
        InsertExtras(
            passphraseChars,
            PassphraseDigits,
            PassphraseSpecials,
            placement,
            effectiveSymbols);

        var finalPassword = new string(passphraseChars.ToArray());
        GeneratedPassword = finalPassword;

        var entropy = Math.Log2(wordList.Length) * EffectivePassphraseWordCount;
        if (PassphraseDigits > 0) entropy += Math.Log2(DigitChars.Length) * PassphraseDigits;
        if (PassphraseSpecials > 0 && effectiveSymbols.Length > 0)
        {
            entropy += Math.Log2(effectiveSymbols.Length) * PassphraseSpecials;
        }

        if (CaseModeAt(PassphraseCaseIndex) == SyllableCase.Mixed)
        {
            entropy += MixedCaseBitsPerLetter * words.Sum(word => word.Count(char.IsLetter));
        }

        UpdateStrengthIndicator(entropy);
        UpdatePhoneticDisplay(finalPassword);
    }

    /// <summary>
    /// Builds a leet password: one word, rewritten through <see cref="LeetSubstitutions"/>,
    /// cased, then given digits and specials at the chosen placement.
    /// </summary>
    /// <remarks>
    /// <para>What the strength figure credits is the part of the result that was actually drawn
    /// at random. A word drawn from the list is worth the size of that list; a word typed by the
    /// operator is worth nothing, because an attacker guesses the word, not the spelling. The
    /// substitution table is public, so applying all of it is worth nothing either; applying it
    /// letter by letter on a coin toss is worth one bit per letter it could have touched.</para>
    /// <para>This is the mode that produces the weakest passwords of the four, and the figure
    /// says so rather than counting the result as if every character had been drawn at
    /// random.</para>
    /// </remarks>
    private void GenerateLeetPassword()
    {
        SyllableStructureText = string.Empty;
        SyllableTotalLength = 0;

        var typed = SanitizeLeetBaseWord(LeetBaseWord);
        var drawn = LeetRandomWord || typed.Length == 0;
        string baseWord;
        double wordEntropy;

        if (drawn)
        {
            var wordList = SelectedWordList();
            if (wordList.Length == 0)
            {
                SetEmptyOutput();
                return;
            }

            baseWord = wordList[CryptoRandomInt(wordList.Length)];
            wordEntropy = Math.Log2(wordList.Length);
        }
        else
        {
            baseWord = typed;
            wordEntropy = 0;
        }

        var effectiveSymbols = GetEffectiveSymbols();
        var substituted = ApplyLeetSubstitutions(baseWord, out var substitutable);
        var cased = ApplyLeetCase(substituted, (SyllableCase)LeetCaseIndex, out var casedLetters);

        var chars = new List<char>(cased);
        InsertExtras(
            chars,
            EffectiveLeetDigits,
            EffectiveLeetSpecials,
            (Placement)LeetPlacementIndex,
            effectiveSymbols);

        var finalPassword = new string(chars.ToArray());
        GeneratedPassword = finalPassword;
        LeetWordSource = drawn ? baseWord : string.Empty;
        OnPropertyChanged(nameof(ShowLeetWordSource));

        var entropy = wordEntropy;
        if (!LeetFullSubstitution) entropy += substitutable;
        if ((SyllableCase)LeetCaseIndex == SyllableCase.Mixed) entropy += MixedCaseBitsPerLetter * casedLetters;
        if (EffectiveLeetDigits > 0) entropy += Math.Log2(DigitChars.Length) * EffectiveLeetDigits;
        if (EffectiveLeetSpecials > 0 && effectiveSymbols.Length > 0)
        {
            entropy += Math.Log2(effectiveSymbols.Length) * EffectiveLeetSpecials;
        }

        UpdateStrengthIndicator(entropy);
        UpdatePhoneticDisplay(finalPassword);
    }

    /// <summary>
    /// Keeps the letters of a typed base word and drops everything else, so that what the
    /// generator rewrites is a word rather than whatever was pasted into the box.
    /// </summary>
    private static string SanitizeLeetBaseWord(string? word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return string.Empty;
        }

        return new string(word.Where(char.IsLetter).ToArray());
    }

    /// <summary>
    /// Rewrites the letters the substitution table covers, all of them or one in two, and reports
    /// how many letters it could have rewritten.
    /// </summary>
    /// <remarks>
    /// A substitution whose replacement a shell would read as syntax is skipped while CLI-safe is
    /// on, and is not counted as substitutable: <c>l</c> becomes <c>!</c>, which is history
    /// expansion in an interactive shell, and the point of the box is that the result can be
    /// pasted into one.
    /// </remarks>
    private string ApplyLeetSubstitutions(string word, out int substitutable)
    {
        substitutable = 0;
        var builder = new StringBuilder(word.Length);

        foreach (var character in word)
        {
            var lowered = char.ToLowerInvariant(character);
            if (!LeetSubstitutions.TryGetValue(lowered, out var replacement)
                || (CliSafe && ShellDangerousChars.Contains(replacement)))
            {
                builder.Append(character);
                continue;
            }

            substitutable++;
            var substitute = LeetFullSubstitution || CryptoRandomInt(2) == 0;
            builder.Append(substitute ? replacement : character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Applies a case mode to a single word and reports how many letters were still letters when
    /// it ran, which is what mixed case is paid on.
    /// </summary>
    private string ApplyLeetCase(string word, SyllableCase caseMode, out int letters)
    {
        letters = word.Count(char.IsLetter);
        var builder = new StringBuilder(word.Length);

        // A leet password has no syllables to hang a pattern on, so a block covers one letter.
        // A character the substitution turned into a digit is not a letter and does not consume
        // a block: the pattern stays on the letters it can actually case.
        var blockIndex = 0;

        for (var index = 0; index < word.Length; index++)
        {
            var character = word[index];
            if (caseMode == SyllableCase.Blocks)
            {
                if (char.IsLetter(character))
                {
                    character = ApplyCaseBlock(character.ToString(), blockIndex)[0];
                    blockIndex++;
                }

                builder.Append(character);
                continue;
            }

            switch (caseMode)
            {
                case SyllableCase.Upper:
                case SyllableCase.Inverse:
                    character = char.ToUpperInvariant(character);
                    break;
                case SyllableCase.Title:
                case SyllableCase.WordCase:
                    if (index == 0) character = char.ToUpperInvariant(character);
                    break;
                case SyllableCase.Mixed:
                    if (CryptoRandomInt(4) == 0) character = char.ToUpperInvariant(character);
                    break;
                case SyllableCase.Alternating:
                    if (index % 2 != 0) character = char.ToUpperInvariant(character);
                    break;
            }

            builder.Append(character);
        }

        var result = builder.ToString();
        if (caseMode == SyllableCase.Inverse && result.Length > 0)
        {
            result = result[..^1] + char.ToLowerInvariant(result[^1]);
        }

        return result;
    }

    private void UpdateStrengthIndicator(double entropy)
    {
        LastEntropyBits = entropy;

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

        if (FloorOutOfReach)
        {
            issues.Add(string.Format(L("ToolPwdGenIssueFloorUnreachable"), EntropyFloorBits));
        }

        if (CurrentMode == GeneratorMode.Leet
            && !LeetRandomWord
            && SanitizeLeetBaseWord(LeetBaseWord).Length > 0)
        {
            issues.Add(L("ToolPwdGenIssueChosenWord"));
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
        => _wordLists =
        [
            .. PassphraseLanguages.Select(language => LoadWordListFile(language.FileName, language.Fallback))
        ];

    /// <summary>
    /// The word list of the selected language. An index no language answers to falls back to the
    /// first rather than to a crash, because the index arrives from a saved preset.
    /// </summary>
    private string[] SelectedWordList()
    {
        if (_wordLists.Length == 0)
        {
            return [];
        }

        return _wordLists[Math.Clamp(PassphraseLanguageIndex, 0, _wordLists.Length - 1)];
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
