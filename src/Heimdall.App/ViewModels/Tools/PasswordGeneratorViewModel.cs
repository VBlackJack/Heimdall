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

        /// <summary>
        /// Written since <see cref="SylLength"/> became the length of the whole password
        /// rather than the length before the digits and the specials were added to it. A file
        /// written before that carries false, and <see cref="ApplyPreset"/> adds the counts
        /// back, so a preset keeps producing passwords of about the length it used to.
        /// </summary>
        public bool SylLengthIncludesExtras { get; set; }
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
        public int BatchCount { get; set; } = 1;
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

    /// <summary>
    /// Every special character a password may hold: the printable ASCII punctuation, and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// A password is read off one screen and typed on whatever keyboard is in front of the
    /// person, which is the same reason the passphrase word lists carry no accent. Pasting a
    /// euro sign or a typographic dash into the box used to put it in the password, where it
    /// may be untypeable, may not survive a terminal, and may not come back the same from a
    /// password field that normalises its input.
    /// </remarks>
    internal const string AllowedSpecialChars =
        "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>
    /// How long a copied password may sit on the clipboard, in seconds. The first is what the
    /// tool has always used.
    /// </summary>
    internal static readonly int[] ClipboardClearChoices = [30, 10, 60, 120];
    private const string AmbiguousChars = "0Oo1lI|";
    private const string ShellDangerousChars = "$^&*'\"\\|`(){}[]<>!~;";
    private const string DefaultPassphraseSeparator = "-";
    private const int PhoneticMaxLength = 32;
    private const string LayoutUnsafeChars = "aqwzmAQWZM";
    private const int HistoryMaxSize = 10;

    /// <summary>
    /// How many passwords one click can produce. Twenty is what the tool this idea comes from
    /// offers, and it is about as many as anyone reads off a screen before copying the lot.
    /// </summary>
    internal const int MinimumBatchCount = 1;
    internal const int MaximumBatchCount = 20;

    /// <summary>What a masked row shows instead of the password.</summary>
    private const char MaskCharacter = '\u2022';
    /// <summary>
    /// The guessing rate the crack time is worked out at: an offline attack on a stolen hash that
    /// was cheap to compute, which is the scenario a password has to survive on its own.
    /// </summary>
    /// <remarks>
    /// It is one scenario among several and the figure means nothing without it. The same password
    /// that falls in a day here stands for millennia against a hash deliberately made slow, five
    /// orders of magnitude further down, and the reader cannot tell which number they are being
    /// shown. <see cref="CrackTimeAssumptionText"/> therefore states the rate on screen, formatted
    /// from this constant so the two can never drift apart.
    /// </remarks>
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

    /// <summary>
    /// The room a syllable password keeps for its own letters, whatever the counts ask for.
    /// Two characters is one consonant-vowel pair, the smallest thing that still reads as a
    /// syllable rather than as punctuation around a number.
    /// </summary>
    private const int MinimumSyllablePortion = 2;

    /// <summary>
    /// How many times a random password is drawn before the promise that every selected class
    /// appears is given up on.
    /// </summary>
    /// <remarks>
    /// The share of draws that qualify is at its worst when there are as many classes as there
    /// are characters to put them in, which is four of each: about seven draws in a hundred
    /// qualify, so ten thousand attempts miss by a margin no one will ever observe. It is a bound
    /// rather than a loop without one because an unbounded retry is a hang waiting for a charset
    /// nobody anticipated.
    /// </remarks>
    private const int MaximumGuaranteeDraws = 10_000;
    private const int SyllableLengthStep = 2;
    private const int MaximumPassphraseWordCount = 8;
    private const int MaximumLeetExtras = 6;

    /// <summary>
    /// The most digits, and the most special characters, a syllable password takes.
    /// </summary>
    /// <remarks>
    /// The two sliders read their maximum from this, so the search below cannot look at a
    /// combination the operator has no way of asking for.
    /// </remarks>
    internal const int MaximumSyllableExtras = 6;

    /// <summary>The same ceiling, for the two sliders to read rather than carry a copy of.</summary>
    public int MaximumSyllableExtrasValue => MaximumSyllableExtras;


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
    private PasswordGeneratorStore? _cachedStore;
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
    [ObservableProperty] private int _clipboardClearIndex;
    [ObservableProperty] private int _batchCount = MinimumBatchCount;
    [ObservableProperty] private bool _maskBatch;

    [ObservableProperty] private string _generatedPassword = string.Empty;
    [ObservableProperty] private string _phoneticText = string.Empty;
    [ObservableProperty] private int _strengthLevel;
    [ObservableProperty] private string _strengthText = string.Empty;
    [ObservableProperty] private double _strengthPercent;
    [ObservableProperty] private string _crackTimeText = string.Empty;

    /// <summary>The attack the crack time above it is worked out against.</summary>
    [ObservableProperty] private string _crackTimeAssumptionText = string.Empty;

    /// <summary>How big the chosen language's word list is, and what that is worth a word.</summary>
    [ObservableProperty] private string _wordListSummaryText = string.Empty;

    /// <summary>
    /// Whether the tool reopens where it was left. Off until it is asked for: a generator that
    /// silently remembers what someone was making is a generator that writes their habits down
    /// without being told to.
    /// </summary>
    [ObservableProperty] private bool _rememberSettings;
    [ObservableProperty] private string _issuesText = string.Empty;
    [ObservableProperty] private string _syllableStructureText = string.Empty;
    [ObservableProperty] private int _syllableTotalLength;

    public bool IsInitialized => _isInitialized;
    public ObservableCollection<string> PasswordHistory { get; } = new();

    /// <summary>
    /// The passwords the last click produced, the one on display first. It always holds as many
    /// entries as the count asks for, so a batch of one is the single password and nothing else.
    /// </summary>
    internal ObservableCollection<string> GeneratedBatch { get; } = new();

    /// <summary>
    /// What the list shows, which is the batch itself or a row of dots per password. The batch
    /// above is never masked: hiding a password on screen must not hide it from the button that
    /// copies it.
    /// </summary>
    public ObservableCollection<string> BatchRows { get; } = new();

    public bool ShowBatch => GeneratedBatch.Count > 1;

    /// <summary>The delay the clipboard timer runs on, in seconds.</summary>
    internal int ClipboardClearSeconds =>
        ClipboardClearChoices[Math.Clamp(ClipboardClearIndex, 0, ClipboardClearChoices.Length - 1)];

    /// <summary>
    /// What the generator will actually use out of the custom specials box, when that differs
    /// from what was typed into it. Empty when the two agree.
    /// </summary>
    /// <remarks>
    /// The box is left exactly as typed. Rewriting what someone is still typing fights them,
    /// and silently dropping half of it tells them nothing: the line under the box says what
    /// survived, or that none of it did.
    /// </remarks>
    public string CustomSpecialsNotice
    {
        get
        {
            var typed = CustomSpecials ?? string.Empty;
            if (string.IsNullOrWhiteSpace(typed))
            {
                return string.Empty;
            }

            var usable = SanitizeCustomSpecials(typed);
            if (string.Equals(usable, typed, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return usable.Length == 0
                ? L("ToolPwdGenSpecialsNoneUsable")
                : string.Format(L("ToolPwdGenSpecialsUsable"), usable);
        }
    }

    public bool ShowCustomSpecialsNotice => !string.IsNullOrEmpty(CustomSpecialsNotice);

    /// <summary>The batch as it is copied and exported, one password per line.</summary>
    internal string BatchAsText => string.Join(Environment.NewLine, GeneratedBatch);
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

    /// <summary>
    /// What asking for a minimum did to the settings, or why it could not.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="FloorNoticeText"/>, which every generation clears because it
    /// describes that generation. This one describes a change to the settings, so it outlives the
    /// password it produced and goes when the operator moves a control of their own.
    /// </remarks>
    [ObservableProperty] private string _floorSearchNoticeText = string.Empty;

    public bool ShowFloorSearchNotice => !string.IsNullOrEmpty(FloorSearchNoticeText);

    partial void OnFloorSearchNoticeTextChanged(string value)
        => OnPropertyChanged(nameof(ShowFloorSearchNotice));

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

    /// <summary>The digits this generation has room for, which is what was asked for unless
    /// the length could not hold them all.</summary>
    internal int EffectiveSyllableDigits { get; private set; }

    /// <summary>The specials this generation has room for.</summary>
    internal int EffectiveSyllableSpecials { get; private set; }
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

        // Where the tool was left, when it was told to remember. Applied before the first draw so
        // that the password on screen is the one these settings make, not one from the defaults.
        var store = LoadStore();
        if (store.RememberSettings)
        {
            RememberSettings = true;
            if (store.Settings is { } remembered)
            {
                ApplyPreset(remembered);
            }
        }

        // The setter above only reports when the index changes, and it starts on the first
        // language, so the summary is written here rather than left blank for that one case.
        UpdateWordListSummary();

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
        SylLengthIncludesExtras = true,
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
        BatchCount = BatchCount,
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
            // A preset written before the length covered the digits and the specials meant
            // the letters alone, so the counts are added back rather than taken out of it.
            SyllableLength = preset.SylLengthIncludesExtras
                ? preset.SylLength
                : Math.Min(
                    preset.SylLength + Math.Max(preset.SylDigits, 0) + Math.Max(preset.SylSpecials, 0),
                    MaximumSyllableLength);
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
            BatchCount = Math.Clamp(preset.BatchCount, MinimumBatchCount, MaximumBatchCount);
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

    /// <summary>How many presets are saved in all, across every mode.</summary>
    internal int CustomPresetCount => LoadCustomPresets().Count;

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
        GeneratedBatch.Clear();
        RefreshBatchRows();
    }

    [RelayCommand]
    private void GenerateCore()
    {
        ResolveFloorSizes();
        ResolvePositionCounts();

        // The extra passwords are generated first and the one on display last, so the strength
        // figure, the phonetic reading and the syllable structure all describe the password in
        // the box rather than the last of a batch nobody asked to see.
        var wanted = Math.Clamp(BatchCount, MinimumBatchCount, MaximumBatchCount);
        var extras = new List<string>(Math.Max(0, wanted - 1));
        var extraMaterial = new List<PlacementMaterial?>(Math.Max(0, wanted - 1));
        for (var index = 1; index < wanted; index++)
        {
            GenerateForCurrentMode();
            extras.Add(GeneratedPassword);
            extraMaterial.Add(_currentMaterial);
        }

        GenerateForCurrentMode();

        // The batch is rearranged as one or not at all: a bar that moved the digit in the password
        // on display and left the nineteen below it alone would be showing two different rules at
        // once.
        _batchMaterial.Clear();
        _materialPassword = null;
        if (_currentMaterial is not null && extraMaterial.TrueForAll(material => material is not null))
        {
            _batchMaterial.Add(_currentMaterial);
            _batchMaterial.AddRange(extraMaterial.Select(material => material!));
            _materialPassword = GeneratedPassword;
        }

        GeneratedBatch.Clear();
        GeneratedBatch.Add(GeneratedPassword);
        foreach (var extra in extras)
        {
            GeneratedBatch.Add(extra);
        }

        RefreshBatchRows();
        RaiseVisibilityProperties();

        // Only the password on display enters the history. A batch of twenty would otherwise
        // push out everything generated before it.
        AddToHistory(GeneratedPassword);
    }

    /// <summary>Rebuilds what the list shows from the batch and the mask.</summary>
    private void RefreshBatchRows()
    {
        BatchRows.Clear();
        foreach (var password in GeneratedBatch)
        {
            BatchRows.Add(MaskBatch ? new string(MaskCharacter, password.Length) : password);
        }

        OnPropertyChanged(nameof(ShowBatch));
    }

    private void GenerateForCurrentMode()
    {
        _currentMaterial = null;

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
        if (floor > 0)
        {
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

        // Last, because the floor may have just made the password longer, and a longer
        // password has room for counts that did not fit in the one that was asked for.
        ResolveSyllableCounts();
    }

    /// <summary>
    /// Decides how many digits and specials the length this generation runs at has room for,
    /// and says so when it is fewer than were asked for.
    /// </summary>
    private void ResolveSyllableCounts()
    {
        var (_, digits, specials) = ResolveSyllableShape(EffectiveSyllableLength);
        EffectiveSyllableDigits = digits;
        EffectiveSyllableSpecials = specials;

        if (CurrentMode != GeneratorMode.Syllable
            || (digits == SyllableDigits && specials == SyllableSpecials))
        {
            return;
        }

        NoteCountsCut(digits, specials);
    }

    /// <remarks>
    /// The length is walked up rather than divided into the floor, because the bits a length
    /// carries are no longer proportional to it: promising every class costs a share of the draws
    /// that shrinks as the password grows. Walking asks the same question the strength figure
    /// answers, so the floor cannot promise bits the figure will not show.
    /// </remarks>
    private void ResolveRandomFloor(int floor)
    {
        var classes = BuildCharsetClasses();
        if (classes.Count == 0)
        {
            FloorOutOfReach = true;
            return;
        }

        for (var candidate = Math.Max(Length, 1); candidate <= MaximumLength; candidate++)
        {
            if (GuaranteedRandomBits(classes, candidate) < floor)
            {
                continue;
            }

            if (candidate > Length)
            {
                EffectiveLength = candidate;
                NoteFloorRaise(Length.ToString(), candidate.ToString());
            }

            return;
        }

        FloorOutOfReach = true;
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
    /// Splits a chosen length between the syllables and the characters placed into them.
    /// </summary>
    /// <remarks>
    /// <para>A digit is a character of the password, not an addition to it: sixteen characters
    /// with two digits is fourteen characters of syllable and two digits, and what comes out is
    /// as long as what was asked for. The separator is counted too, being as much a character
    /// of the password as the letters it sits between; it is spent inside the portion returned
    /// here rather than counted here, because how many separators there are depends on how the
    /// syllables fall.</para>
    /// <para>When the counts ask for more characters than the password can hold, the counts are
    /// what gives. A length is exact and a count is a wish for how the length is spent, so the
    /// password stays the size it was asked for and the notice line says what was cut. The cut
    /// takes from whichever count is larger, so one kind of character does not disappear
    /// entirely while the other keeps every place it asked for.</para>
    /// </remarks>
    private (int Portion, int Digits, int Specials) ResolveSyllableShape(int total)
        => ResolveSyllableShape(total, SyllableDigits, SyllableSpecials);

    private static (int Portion, int Digits, int Specials) ResolveSyllableShape(
        int total, int wantedDigits, int wantedSpecials)
    {
        var digits = Math.Max(wantedDigits, 0);
        var specials = Math.Max(wantedSpecials, 0);

        while (total - digits - specials < MinimumSyllablePortion && digits + specials > 0)
        {
            if (specials >= digits)
            {
                specials--;
            }
            else
            {
                digits--;
            }
        }

        return (Math.Max(total - digits - specials, 0), digits, specials);
    }

    /// <summary>
    /// How many syllables a chosen length holds, which is what the build produces and what the
    /// block editor lines its pattern up with.
    /// </summary>
    /// <remarks>
    /// Every syllable is a consonant-vowel pair, the shape the build falls back to and the
    /// cheapest one per character, so this is a count the password always reaches and never
    /// exceeds. Each pair after the first pays for its separator as well as its two letters,
    /// the separator being a character of the password like any other; with no separator this
    /// is the portion halved, which is what the count has always been.
    /// </remarks>
    private int SyllableCountFor(int totalLength)
        => SyllableCountFor(totalLength, SyllableDigits, SyllableSpecials);

    /// <summary>
    /// How many syllable blocks a password of this shape holds.
    /// </summary>
    /// <remarks>
    /// The shape has to be passed in, not read off the properties. A search that asks what six
    /// digits would be worth while this counts the blocks left by two is counting those four
    /// characters twice, once as digits and once as the syllables they displaced, and it says a
    /// minimum is reachable when it is not.
    /// </remarks>
    private int SyllableCountFor(int totalLength, int wantedDigits, int wantedSpecials)
    {
        var (portion, _, _) = ResolveSyllableShape(totalLength, wantedDigits, wantedSpecials);
        var separatorLength = (SyllableSeparator ?? string.Empty).Length;
        return (portion + separatorLength) / (CharactersPerSyllableBlock + separatorLength);
    }

    /// <summary>
    /// The bits a syllable password of this length carries whatever the dice do: open syllables
    /// only, which is the cheaper of the two shapes per character, and no credit for mixed case.
    /// </summary>
    private double GuaranteedSyllableBits(int totalLength)
        => GuaranteedSyllableBits(totalLength, SyllableDigits, SyllableSpecials);

    /// <summary>
    /// What a syllable password of this shape is worth, without it having to be the shape on
    /// screen, so a search can ask about one it has not committed to.
    /// </summary>
    private double GuaranteedSyllableBits(int totalLength, int wantedDigits, int wantedSpecials)
    {
        var consonants = LayoutSafe ? LayoutSafeConsonants : Consonants;
        var vowels = LayoutSafe ? LayoutSafeVowels : Vowels;
        var (_, digits, specials) = ResolveSyllableShape(totalLength, wantedDigits, wantedSpecials);
        var bits = Math.Log2(consonants.Length * vowels.Length)
            * SyllableCountFor(totalLength, wantedDigits, wantedSpecials);
        return bits + ExtrasBits(digits, specials);
    }

    private double GuaranteedPassphraseBits(int wordCount)
        => GuaranteedPassphraseBits(wordCount, PassphraseDigits, PassphraseSpecials);

    private double GuaranteedPassphraseBits(int wordCount, int digits, int specials)
    {
        var wordList = SelectedWordList();
        if (wordList.Length < 2)
        {
            return 0;
        }

        return Math.Log2(wordList.Length) * wordCount + ExtrasBits(digits, specials);
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


    /// <summary>
    /// What a search for the minimum found: whether it got there, the most it could reach, and
    /// the writing of what it found.
    /// </summary>
    private readonly record struct FloorFound(bool Reached, double Ceiling, Func<string>? Apply);

    /// <summary>
    /// The settings as they stood before a minimum moved them, which is everything a search is
    /// allowed to write and nothing else.
    /// </summary>
    private readonly record struct FloorUndo(
        int Length,
        int SyllableLength,
        int SyllableDigits,
        int SyllableSpecials,
        int PassphraseWordCount,
        int PassphraseDigits,
        int PassphraseSpecials,
        int LeetDigits,
        int LeetSpecials);

    /// <summary>
    /// Where the controls were before the minimum in force moved them, or null when it has not.
    /// </summary>
    /// <remarks>
    /// This is what makes the minimum something that can be taken back. Without it, asking for a
    /// hundred bits and then changing your mind leaves the settings wherever the search put them,
    /// and there is nothing on screen to say what they used to be. It is also what the next
    /// search starts from, so lowering the minimum lowers the settings instead of ratcheting them
    /// up for the rest of the session.
    /// </remarks>
    private FloorUndo? _beforeFloor;

    private FloorUndo CaptureBeforeFloor() => new(
        Length,
        SyllableLength,
        SyllableDigits,
        SyllableSpecials,
        PassphraseWordCount,
        PassphraseDigits,
        PassphraseSpecials,
        LeetDigits,
        LeetSpecials);

    private void RestoreBeforeFloor(FloorUndo undo)
    {
        Length = undo.Length;
        SyllableLength = undo.SyllableLength;
        SyllableDigits = undo.SyllableDigits;
        SyllableSpecials = undo.SyllableSpecials;
        PassphraseWordCount = undo.PassphraseWordCount;
        PassphraseDigits = undo.PassphraseDigits;
        PassphraseSpecials = undo.PassphraseSpecials;
        LeetDigits = undo.LeetDigits;
        LeetSpecials = undo.LeetSpecials;
    }

    /// <summary>
    /// Puts the settings where they have to be to guarantee the minimum, in one write.
    /// </summary>
    /// <remarks>
    /// <para>Asking for a hundred bits used to be a question the tool answered rather than a thing
    /// it did. When the answer was no, nothing moved and the operator was left to find the
    /// combination by hand, one slider at a time, with no way of knowing which one was in the way.
    /// The search here tries every shape the controls can be put in, and writes the first one that
    /// reaches the minimum.</para>
    /// <para><b>It commits once or not at all.</b> An older version of the floor raised the
    /// slider itself, one step at a time, regenerating as it went; a minimum that turned out to be
    /// out of reach left every control pinned at its maximum with no way back. Everything here is
    /// computed before anything is written, and when nothing reaches the minimum nothing is
    /// written at all.</para>
    /// <para><b>What it will not do</b> is turn a class of characters on, change the word list, or
    /// take away digits the operator asked for. Those are what the password is made of, not how
    /// much of it there is. It will also not count what varies between draws, such as closed
    /// syllables or mixed case, because the minimum is a guarantee: see ResolveFloorSizes.</para>
    /// <para>Of the shapes that reach the minimum it takes the shortest, and of those the one that
    /// adds the fewest digits and special characters.</para>
    /// </remarks>
    internal void ApplyFloorToSettings()
    {
        var floor = EntropyFloorBits;
        var sentence = string.Empty;
        var undo = _beforeFloor;

        // Back to the operator's own settings before deciding anything. A search that started
        // from where the last minimum left things could only ever push them further up, so
        // lowering the minimum would never lower anything.
        if (undo is not null)
        {
            _isSuspended = true;
            try
            {
                RestoreBeforeFloor(undo.Value);
            }
            finally
            {
                _isSuspended = false;
            }
        }

        if (floor > 0)
        {
            var found = CurrentMode switch
            {
                GeneratorMode.Random => SearchRandomFloor(floor),
                GeneratorMode.Syllable => SearchSyllableFloor(floor),
                GeneratorMode.Passphrase => SearchPassphraseFloor(floor),
                GeneratorMode.Leet => SearchLeetFloor(floor),
                _ => default,
            };

            if (found.Reached && found.Apply is not null)
            {
                // One write, with the generation held off until every part of the shape is in
                // place: a password drawn halfway through comes from a shape nobody chose.
                undo ??= CaptureBeforeFloor();
                _isSuspended = true;
                try
                {
                    sentence = found.Apply();
                }
                finally
                {
                    _isSuspended = false;
                }
            }
        }
        else
        {
            // No minimum asked for, so there is nothing of the operator's being held.
            undo = null;
        }

        // The sentence goes on after the generation, never before, because the generation
        // announces that the settings moved and that announcement is what clears this line. Both
        // endings of the search come through here: the first version wrote the ceiling and
        // returned, and the caller's generation wiped it before anybody read it.
        // Both of these go on after the generation, never before: the generation announces that
        // the settings moved, and that announcement clears them both.
        RegenerateIfReady();
        _beforeFloor = undo;
        FloorSearchNoticeText = sentence;
    }

    /// <summary>
    /// The most these settings can guarantee, whatever is asked of them.
    /// </summary>
    /// <remarks>
    /// Worked out here rather than remembered from the last search, because the settings move
    /// between one and the next and a remembered ceiling would go quietly out of date. The search
    /// is a few thousand arithmetic operations over a space the sliders bound, so it costs
    /// nothing to run again.
    /// </remarks>
    private double FloorCeilingBits()
    {
        var floor = EntropyFloorBits;
        var found = CurrentMode switch
        {
            GeneratorMode.Random => SearchRandomFloor(floor),
            GeneratorMode.Syllable => SearchSyllableFloor(floor),
            GeneratorMode.Passphrase => SearchPassphraseFloor(floor),
            GeneratorMode.Leet => SearchLeetFloor(floor),
            _ => default,
        };

        return found.Ceiling;
    }

    private FloorFound SearchRandomFloor(int floor)
    {
        var classes = BuildCharsetClasses();
        if (classes.Count == 0)
        {
            return new FloorFound(false, 0, null);
        }

        var ceiling = 0.0;
        for (var candidate = Math.Max(Length, 1); candidate <= MaximumLength; candidate++)
        {
            var bits = GuaranteedRandomBits(classes, candidate);
            ceiling = Math.Max(ceiling, bits);
            if (bits < floor)
            {
                continue;
            }

            if (candidate == Length)
            {
                return new FloorFound(true, ceiling, null);
            }

            var chosen = candidate;
            return new FloorFound(true, ceiling, () =>
            {
                Length = chosen;
                return string.Format(
                    L("ToolPwdGenFloorSetLength"),
                    chosen.ToString(CultureInfo.InvariantCulture),
                    floor.ToString(CultureInfo.InvariantCulture));
            });
        }

        return new FloorFound(false, ceiling, null);
    }

    private FloorFound SearchSyllableFloor(int floor)
    {
        var ceiling = 0.0;
        for (var length = SyllableLength; length <= MaximumSyllableLength; length += SyllableLengthStep)
        {
            var room = (MaximumSyllableExtras - SyllableDigits) + (MaximumSyllableExtras - SyllableSpecials);
            for (var added = 0; added <= Math.Max(room, 0); added++)
            {
                for (var moreDigits = 0; moreDigits <= added; moreDigits++)
                {
                    var digits = SyllableDigits + moreDigits;
                    var specials = SyllableSpecials + (added - moreDigits);
                    if (digits > MaximumSyllableExtras || specials > MaximumSyllableExtras)
                    {
                        continue;
                    }

                    var bits = GuaranteedSyllableBits(length, digits, specials);
                    ceiling = Math.Max(ceiling, bits);
                    if (bits < floor)
                    {
                        continue;
                    }

                    if (length == SyllableLength && digits == SyllableDigits && specials == SyllableSpecials)
                    {
                        return new FloorFound(true, ceiling, null);
                    }

                    var (l, d, s) = (length, digits, specials);
                    return new FloorFound(true, ceiling, () =>
                    {
                        SyllableLength = l;
                        SyllableDigits = d;
                        SyllableSpecials = s;
                        return string.Format(
                            L("ToolPwdGenFloorSetSyllable"),
                            l.ToString(CultureInfo.InvariantCulture),
                            d.ToString(CultureInfo.InvariantCulture),
                            s.ToString(CultureInfo.InvariantCulture),
                            floor.ToString(CultureInfo.InvariantCulture));
                    });
                }
            }
        }

        return new FloorFound(false, ceiling, null);
    }

    private FloorFound SearchPassphraseFloor(int floor)
    {
        var ceiling = 0.0;
        var room = (MaximumLeetExtras - PassphraseDigits) + (MaximumLeetExtras - PassphraseSpecials);

        // Words before extras, which is the other way round from the syllable search above. A
        // passphrase is words: reaching a minimum by hanging digits and punctuation off the end
        // of it takes away the one thing it is for, so every word count is tried before the first
        // extra character is.
        for (var added = 0; added <= Math.Max(room, 0); added++)
        {
            for (var words = PassphraseWordCount; words <= MaximumPassphraseWordCount; words++)
            {
                for (var moreDigits = 0; moreDigits <= added; moreDigits++)
                {
                    var digits = PassphraseDigits + moreDigits;
                    var specials = PassphraseSpecials + (added - moreDigits);
                    if (digits > MaximumLeetExtras || specials > MaximumLeetExtras)
                    {
                        continue;
                    }

                    var bits = GuaranteedPassphraseBits(words, digits, specials);
                    ceiling = Math.Max(ceiling, bits);
                    if (bits < floor)
                    {
                        continue;
                    }

                    if (words == PassphraseWordCount
                        && digits == PassphraseDigits
                        && specials == PassphraseSpecials)
                    {
                        return new FloorFound(true, ceiling, null);
                    }

                    var (w, d, s) = (words, digits, specials);
                    return new FloorFound(true, ceiling, () =>
                    {
                        PassphraseWordCount = w;
                        PassphraseDigits = d;
                        PassphraseSpecials = s;
                        return string.Format(
                            L("ToolPwdGenFloorSetPassphrase"),
                            w.ToString(CultureInfo.InvariantCulture),
                            d.ToString(CultureInfo.InvariantCulture),
                            s.ToString(CultureInfo.InvariantCulture),
                            floor.ToString(CultureInfo.InvariantCulture));
                    });
                }
            }
        }

        return new FloorFound(false, ceiling, null);
    }

    private FloorFound SearchLeetFloor(int floor)
    {
        var ceiling = 0.0;
        var room = (MaximumLeetExtras - LeetDigits) + (MaximumLeetExtras - LeetSpecials);
        for (var added = 0; added <= Math.Max(room, 0); added++)
        {
            for (var moreDigits = 0; moreDigits <= added; moreDigits++)
            {
                var digits = LeetDigits + moreDigits;
                var specials = LeetSpecials + (added - moreDigits);
                if (digits > MaximumLeetExtras || specials > MaximumLeetExtras)
                {
                    continue;
                }

                var bits = GuaranteedLeetBits(digits, specials);
                ceiling = Math.Max(ceiling, bits);
                if (bits < floor)
                {
                    continue;
                }

                if (digits == LeetDigits && specials == LeetSpecials)
                {
                    return new FloorFound(true, ceiling, null);
                }

                var (d, s) = (digits, specials);
                return new FloorFound(true, ceiling, () =>
                {
                    LeetDigits = d;
                    LeetSpecials = s;
                    return string.Format(
                        L("ToolPwdGenFloorSetLeet"),
                        d.ToString(CultureInfo.InvariantCulture),
                        s.ToString(CultureInfo.InvariantCulture),
                        floor.ToString(CultureInfo.InvariantCulture));
                });
            }
        }

        return new FloorFound(false, ceiling, null);
    }

    private void NoteFloorRaise(string chosen, string used)
        => FloorNoticeText = string.Format(L("ToolPwdGenFloorRaised"), chosen, used);

    /// <summary>
    /// Says how many digits and specials the chosen length had room for, when it had room for
    /// fewer than were asked for.
    /// </summary>
    private void NoteCountsCut(int digits, int specials)
        => AppendNotice(string.Format(
            L("ToolPwdGenCountsCut"), digits.ToString(), specials.ToString()));

    /// <summary>
    /// Says that this password is not the promised one, which is the only way that can be true
    /// without anybody noticing.
    /// </summary>
    private void NoteClassesNotPromised()
        => AppendNotice(L("ToolPwdGenClassesNotPromised"));

    /// <summary>
    /// Adds a sentence to the notice line, keeping what is already on it.
    /// </summary>
    /// <remarks>
    /// The floor may have written first. Both things happened, so the line says both rather than
    /// the second one silently taking the place of the first.
    /// </remarks>
    private void AppendNotice(string sentence)
        => FloorNoticeText = string.IsNullOrEmpty(FloorNoticeText)
            ? sentence
            : FloorNoticeText + " " + sentence;

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

    /// <summary>
    /// What one password was made of, kept apart so the same characters can be put back together
    /// in a different order.
    /// </summary>
    private sealed record PlacementMaterial(string Drawn, string Digits, string Specials);

    /// <summary>The material behind each password on display, in the order the batch shows them.</summary>
    private readonly List<PlacementMaterial> _batchMaterial = [];

    /// <summary>The material of the generation running right now, or null if it kept none.</summary>
    private PlacementMaterial? _currentMaterial;

    /// <summary>
    /// The password the material last put on the screen, which is how the material is known to
    /// still describe what is there.
    /// </summary>
    private string? _materialPassword;

    /// <summary>
    /// Moves one character to another place in the passwords on display, using the characters they
    /// already have rather than drawing new ones.
    /// </summary>
    /// <remarks>
    /// <para>Writing a position regenerates, so the only way to see what moving a cursor did was
    /// to compare two passwords that had nothing in common but their shape. What makes the bar
    /// readable is the opposite: one character travels while every other one holds still, and it
    /// does so under the mouse rather than after it.</para>
    /// <para>The material is checked against the password on display before it is trusted, by
    /// remembering which password it last produced. Asking the saved positions to rebuild it
    /// instead looks like the same check and is not: an uncommitted preview is a display that has
    /// deliberately run ahead of the settings, so that check called the material stale the moment
    /// the first preview succeeded and the cursor froze one notch along. What makes material
    /// unusable is the password on screen no longer being the one it made, which is the question
    /// asked here.</para>
    /// </remarks>
    /// <param name="commit">
    /// Whether the position is written down. A drag previews on every move and commits once, on
    /// the drop, so what is written is the password that was on screen when the mouse came up.
    /// </param>
    /// <returns>False when there is nothing to rearrange, or the material no longer fits.</returns>
    internal bool TryMoveInPlace(bool digit, int index, double percent, bool commit)
    {
        if (_batchMaterial.Count == 0 || _batchMaterial.Count != GeneratedBatch.Count)
        {
            return false;
        }

        var positions = ParsePositions(digit ? DigitPositions : SpecialPositions);
        if (index < 0 || index >= positions.Length)
        {
            return false;
        }

        if (!string.Equals(_materialPassword, GeneratedPassword, StringComparison.Ordinal))
        {
            return false;
        }

        var currentDigits = ParsePositions(DigitPositions);
        var currentSpecials = ParsePositions(SpecialPositions);

        positions[index] = ClampPercent(percent);
        var text = FormatPositions(positions);
        var rebuilt = _batchMaterial
            .Select(material => Rearrange(
                material,
                digit ? positions : currentDigits,
                digit ? currentSpecials : positions))
            .ToList();

        if (commit)
        {
            // Both lists are settings, and writing one regenerates. What is wanted here is the
            // setting written down and the password left as it was shown, so the generation is
            // held off and the hold lifted without one.
            _isSuspended = true;
            try
            {
                if (digit)
                {
                    DigitPositions = text;
                }
                else
                {
                    SpecialPositions = text;
                }
            }
            finally
            {
                _isSuspended = false;
            }
        }

        // Replaced one by one rather than cleared and refilled: this runs on every mouse move of
        // a drag, and emptying a list of twenty on each of them is what a drag felt like before.
        for (var row = 0; row < rebuilt.Count; row++)
        {
            GeneratedBatch[row] = rebuilt[row];
        }

        if (BatchRows.Count == rebuilt.Count)
        {
            for (var row = 0; row < rebuilt.Count; row++)
            {
                BatchRows[row] = MaskBatch
                    ? new string(MaskCharacter, rebuilt[row].Length)
                    : rebuilt[row];
            }
        }
        else
        {
            RefreshBatchRows();
        }

        GeneratedPassword = rebuilt[0];
        _materialPassword = rebuilt[0];
        UpdatePhoneticDisplay(rebuilt[0]);

        if (commit)
        {
            AddToHistory(rebuilt[0]);
            RaiseSettingsChanged();
        }

        return true;
    }

    /// <summary>Puts one password's characters back together at these positions.</summary>
    private static string Rearrange(
        PlacementMaterial material,
        IReadOnlyList<double> digits,
        IReadOnlyList<double> specials)
    {
        var chars = new List<char>(material.Drawn);
        InsertAtPositions(chars, material.Digits.ToCharArray(), digits);
        InsertAtPositions(chars, material.Specials.ToCharArray(), specials);
        return new string(chars.ToArray());
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
            GeneratorMode.Syllable => SyllableCountFor(SyllableLength),
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
    partial void OnCustomSpecialsChanged(string value)
    {
        OnPropertyChanged(nameof(CustomSpecialsNotice));
        OnPropertyChanged(nameof(ShowCustomSpecialsNotice));
        RegenerateIfReady();
    }
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
    // The counts and the separator are spent out of the length, so they decide how many
    // syllables there are just as the length does, and the block editor follows them too.
    partial void OnSyllableDigitsChanged(int value)
    {
        SyncCaseBlocksToUnitCount();
        RegenerateIfReady();
    }

    partial void OnSyllableSpecialsChanged(int value)
    {
        SyncCaseBlocksToUnitCount();
        RegenerateIfReady();
    }

    partial void OnSyllablePlacementIndexChanged(int value) => RegenerateIfReady();

    partial void OnSyllableSeparatorChanged(string value)
    {
        SyncCaseBlocksToUnitCount();
        RegenerateIfReady();
    }
    partial void OnSyllableCvcChanged(bool value) => RegenerateIfReady();
    partial void OnPassphraseWordCountChanged(int value)
    {
        SyncCaseBlocksToUnitCount();
        RegenerateIfReady();
    }
    partial void OnPassphraseSeparatorChanged(string value) => RegenerateIfReady();
    partial void OnPassphraseLanguageIndexChanged(int value)
    {
        UpdateWordListSummary();
        RegenerateIfReady();
    }
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
    /// <summary>
    /// Asking for a minimum puts the settings where they have to be for it.
    /// </summary>
    /// <remarks>
    /// Only when the operator asks. A preset carries a minimum of its own and writes it with the
    /// generation held off, and searching from there would throw away the very settings the preset
    /// was chosen for.
    /// </remarks>
    partial void OnEntropyFloorIndexChanged(int value)
    {
        if (_isSuspended || !_isInitialized)
        {
            return;
        }

        ApplyFloorToSettings();
    }

    partial void OnCaseBlocksChanged(string value) => RegenerateIfReady();
    partial void OnDigitPositionsChanged(string value) => RegenerateIfReady();
    partial void OnSpecialPositionsChanged(string value) => RegenerateIfReady();
    partial void OnBatchCountChanged(int value) => RegenerateIfReady();

    /// <summary>
    /// Masking hides what is already on screen; it does not ask for other passwords.
    /// </summary>
    partial void OnMaskBatchChanged(bool value) => RefreshBatchRows();

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

    /// <summary>
    /// Raised when one of the settings a preset holds has been changed.
    /// </summary>
    /// <remarks>
    /// Rerolling is not a change: the same settings are asked for another password, and a preset
    /// named on screen is still the one in force. What this reports is the operator moving a
    /// control, which is the moment the settings stop being the preset they came from.
    /// </remarks>
    internal event EventHandler? SettingsChanged;

    private void RaiseSettingsChanged()
    {
        // A control moved by hand makes both of these stale at once: the sentence describes
        // settings that have since changed, and the way back leads somewhere the operator has
        // left. What is on screen now is what they want, and it becomes the new starting point.
        FloorSearchNoticeText = string.Empty;
        _beforeFloor = null;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RegenerateIfReady()
    {
        if (!_isInitialized || _isSuspended)
        {
            return;
        }

        RaiseSettingsChanged();
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
        OnPropertyChanged(nameof(ShowBatch));
        OnPropertyChanged(nameof(HasActiveSpecials));
    }

    private void GenerateRandomPassword()
    {
        SyllableStructureText = string.Empty;
        SyllableTotalLength = 0;

        var classes = BuildCharsetClasses();
        var charset = string.Concat(classes);
        if (charset.Length == 0)
        {
            SetEmptyOutput();
            return;
        }

        // A ticked box says the password carries that kind of character. It used to say only that
        // the kind was in the bag drawn from, which at eight characters left two passwords in five
        // with no digit in them at all. The draw is repeated until the promise holds, which is a
        // uniform draw over the passwords that keep it rather than a fix-up of one that does not.
        var promised = classes.Count <= EffectiveLength;
        var finalPassword = string.Empty;
        var kept = false;

        for (var attempt = 0; attempt < MaximumGuaranteeDraws; attempt++)
        {
            var password = new StringBuilder(EffectiveLength);
            for (var i = 0; i < EffectiveLength; i++)
            {
                password.Append(charset[CryptoRandomInt(charset.Length)]);
            }

            finalPassword = password.ToString();

            if (!promised)
            {
                break;
            }

            if (CarriesEveryClass(finalPassword, classes))
            {
                kept = true;
                break;
            }
        }

        GeneratedPassword = finalPassword;

        if (!kept)
        {
            // Either the length cannot hold one of every kind, or ten thousand draws all missed.
            // Whichever it was, this password is an ordinary draw and the figure says so.
            NoteClassesNotPromised();
        }

        var totalEntropy = kept
            ? GuaranteedRandomBits(classes, EffectiveLength)
            : Math.Log2(charset.Length) * EffectiveLength;
        UpdateStrengthIndicator(totalEntropy);
        UpdatePhoneticDisplay(finalPassword);
    }

    private string BuildCharset() => string.Concat(BuildCharsetClasses());

    /// <summary>
    /// What each selected class still contributes once the exclusions have run.
    /// </summary>
    /// <remarks>
    /// <para>The classes are kept apart rather than poured into one string because a box that is
    /// ticked is a promise that the password carries that kind of character, and a promise has to
    /// know what it is about. The custom specials are held to punctuation on the way in, so the
    /// four lists never share a character.</para>
    /// <para>A class the exclusions empty out is not in the list at all: layout-safe and the
    /// ambiguous filter cut into the classes themselves, and a class with nothing left in it can
    /// be neither drawn from nor promised.</para>
    /// </remarks>
    private List<string> BuildCharsetClasses()
    {
        var classes = new List<string>(4);

        void Take(string characters)
        {
            var kept = new StringBuilder(characters.Length);
            foreach (var character in characters)
            {
                if (ExcludeAmbiguous && AmbiguousChars.Contains(character))
                {
                    continue;
                }

                if (LayoutSafe && LayoutUnsafeChars.Contains(character))
                {
                    continue;
                }

                kept.Append(character);
            }

            if (kept.Length > 0)
            {
                classes.Add(kept.ToString());
            }
        }

        if (IncludeUppercase) Take(UppercaseChars);
        if (IncludeLowercase) Take(LowercaseChars);
        if (IncludeDigits) Take(DigitChars);
        if (IncludeSymbols) Take(GetEffectiveSymbols());

        return classes;
    }

    /// <summary>
    /// The share of unconstrained draws that carry at least one character of every class.
    /// </summary>
    /// <remarks>
    /// Counted by inclusion and exclusion over the classes: each term is the share of draws that
    /// avoid one set of classes entirely, added and subtracted by how many classes that set holds.
    /// It is computed as a share rather than as a count of passwords so that nothing overflows at
    /// a hundred and twenty-eight characters. More classes than characters is not a near miss but
    /// an impossibility, and says so rather than leaving rounding to answer.
    /// </remarks>
    private static double ValidDrawShare(IReadOnlyList<string> classes, int length)
    {
        if (classes.Count == 0 || length <= 0 || classes.Count > length)
        {
            return 0;
        }

        var total = 0;
        foreach (var characters in classes)
        {
            total += characters.Length;
        }

        if (total == 0)
        {
            return 0;
        }

        var share = 0.0;
        for (var subset = 0; subset < (1 << classes.Count); subset++)
        {
            var avoided = 0;
            var avoidedClasses = 0;
            for (var index = 0; index < classes.Count; index++)
            {
                if ((subset & (1 << index)) == 0)
                {
                    continue;
                }

                avoided += classes[index].Length;
                avoidedClasses++;
            }

            var term = Math.Pow((double)(total - avoided) / total, length);
            share += avoidedClasses % 2 == 0 ? term : -term;
        }

        return share;
    }

    /// <summary>
    /// The bits a random password of this length carries when every selected class is promised.
    /// </summary>
    /// <remarks>
    /// Drawing again until the promise holds is a uniform draw over the passwords that keep it, so
    /// the figure is the log of how many of those there are: the unconstrained figure plus the log
    /// of the share that qualify. The promise costs about one bit at eight characters with all
    /// four classes, and about a thirtieth of a bit at twenty-four. It is subtracted rather than
    /// ignored because a figure that credits passwords the generator refuses to produce is wrong
    /// in the direction that flatters it.
    /// </remarks>
    private static double GuaranteedRandomBits(IReadOnlyList<string> classes, int length)
    {
        var total = 0;
        foreach (var characters in classes)
        {
            total += characters.Length;
        }

        if (total == 0 || length <= 0)
        {
            return 0;
        }

        var unconstrained = Math.Log2(total) * length;
        var share = ValidDrawShare(classes, length);
        return share <= 0 ? unconstrained : unconstrained + Math.Log2(share);
    }

    /// <summary>Whether the password carries at least one character of every class.</summary>
    private static bool CarriesEveryClass(string password, IReadOnlyList<string> classes)
    {
        foreach (var characters in classes)
        {
            var found = false;
            foreach (var character in password)
            {
                if (characters.Contains(character))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// What the box is worth: the characters of <see cref="AllowedSpecialChars"/> it holds,
    /// each one once.
    /// </summary>
    /// <remarks>
    /// No length cap is needed and one was removed after it was written: ASCII holds exactly
    /// thirty-two punctuation characters, so a deduplicated result cannot be longer than the
    /// allowed set itself, whatever is pasted into the box.
    /// </remarks>
    internal static string SanitizeCustomSpecials(string? typed)
    {
        if (string.IsNullOrEmpty(typed))
        {
            return string.Empty;
        }

        return new string(typed
            .Where(AllowedSpecialChars.Contains)
            .Distinct()
            .ToArray());
    }

    private string GetEffectiveSymbols()
    {
        bool useCustom = CurrentMode == GeneratorMode.Random
            ? IncludeSymbols && !string.IsNullOrWhiteSpace(CustomSpecials)
            : !string.IsNullOrWhiteSpace(CustomSpecials);

        var symbols = useCustom
            ? SanitizeCustomSpecials(CustomSpecials)
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

        // Decided once for the whole batch by ResolveSyllableCounts, so twenty passwords are
        // twenty draws of one shape rather than twenty chances to disagree about it.
        var digitCount = EffectiveSyllableDigits;
        var specialCount = EffectiveSyllableSpecials;
        var portion = Math.Max(EffectiveSyllableLength - digitCount - specialCount, 0);

        // The portion is the whole syllable part of the password, separators included, so the
        // room a separator takes is room the letters do not get. Each syllable after the first
        // is therefore asked what it costs with its separator in front of it.
        var separatorLength = separator.Length;
        var groups = new List<string>();
        var writtenChars = 0;
        var cvcCount = 0;
        while (writtenChars < portion)
        {
            var cost = groups.Count > 0 ? separatorLength : 0;
            var remaining = portion - writtenChars - cost;
            if (remaining < CharactersPerSyllableBlock)
            {
                break;
            }

            var consonant = consonants[CryptoRandomInt(consonants.Length)];
            var vowel = vowels[CryptoRandomInt(vowels.Length)];

            if (useCvc && remaining >= 3 && CryptoRandomInt(2) == 0)
            {
                var ending = endings[CryptoRandomInt(endings.Length)];
                groups.Add(consonant + vowel + ending);
                writtenChars += cost + 3;
                cvcCount++;
            }
            else
            {
                groups.Add(consonant + vowel);
                writtenChars += cost + 2;
            }
        }

        // What is left over is less than one more syllable: a character too few to open one, or
        // too few to pay for a separator and open one. It goes on the end of the last syllable,
        // where a consonant still reads as part of it, rather than standing alone as a syllable
        // of one letter or coming off a length that was asked for exactly.
        for (var shortfall = portion - writtenChars; shortfall > 0; shortfall--)
        {
            var leftover = endings[CryptoRandomInt(endings.Length)];

            // A length with no room for even one syllable still has to produce a password.
            if (groups.Count == 0)
            {
                groups.Add(leftover);
            }
            else
            {
                groups[^1] += leftover;
            }

            writtenChars++;
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
        InsertExtras(chars, digitCount, specialCount, placement, effectiveSymbols);

        var finalPassword = new string(chars.ToArray());
        GeneratedPassword = finalPassword;
        SyllableTotalLength = finalPassword.Length;

        if (digitCount > 0 || specialCount > 0)
        {
            structure += $"  + {digitCount}# {specialCount}!";
        }

        SyllableStructureText = structure;

        var cvPool = consonants.Length * vowels.Length;
        var cvcPool = consonants.Length * vowels.Length * endings.Length;
        var cvGroups = groups.Count - cvcCount;
        var entropy = Math.Log2(cvPool) * cvGroups + Math.Log2(cvcPool) * cvcCount;
        if (useCvc) entropy += groups.Count;
        if (digitCount > 0) entropy += Math.Log2(DigitChars.Length) * digitCount;
        if (specialCount > 0 && effectiveSymbols.Length > 0) entropy += Math.Log2(effectiveSymbols.Length) * specialCount;
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
            // What the password is made of, kept before any of it is put together, so the cursors
            // can be dragged afterwards without the generator running again. Only this placement
            // keeps it: it is the only one with a bar to drag.
            _currentMaterial = new PlacementMaterial(
                new string(chars.ToArray()),
                new string(digits.ToArray()),
                new string(specials.ToArray()));

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
            CrackTimeAssumptionText = string.Empty;
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

    /// <summary>
    /// The guessing rate as a power of ten, which is how a number that large is read.
    /// </summary>
    /// <remarks>
    /// Derived from the constant rather than written out beside it, so that changing the rate
    /// changes the sentence. A rate that is not a round power of ten keeps its leading figure.
    /// </remarks>
    private static string GuessRateText()
    {
        var exponent = (int)Math.Floor(Math.Log10(BruteForceGuessesPerSecond));
        var mantissa = BruteForceGuessesPerSecond / Math.Pow(10, exponent);

        return Math.Abs(mantissa - 1) < 0.001
            ? string.Create(CultureInfo.InvariantCulture, $"10^{exponent}")
            : string.Create(CultureInfo.InvariantCulture, $"{mantissa:0.#} x 10^{exponent}");
    }

    /// <summary>
    /// A count and its unit, in the spelling that count takes.
    /// </summary>
    /// <remarks>
    /// Every count that reaches here is at least one: each branch of the estimate is entered only
    /// once the figure has passed that unit's first whole value, so one is the singular and
    /// everything else the plural, which is the same rule in the three languages the tool speaks.
    /// French would also want the singular for zero and never gets the chance.
    /// </remarks>
    private string Counted(int value, string singularKey, string pluralKey)
        => string.Format(L(value == 1 ? singularKey : pluralKey), value);

    private void UpdateCrackTimeEstimate(double entropy)
    {
        if (entropy <= 0)
        {
            CrackTimeText = string.Empty;
            CrackTimeAssumptionText = string.Empty;
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
            timeStr = Counted((int)secondsAvg, "ToolPwdGenCrackSecond", "ToolPwdGenCrackSeconds");
        }
        else if (secondsAvg < 3600)
        {
            timeStr = Counted((int)(secondsAvg / 60), "ToolPwdGenCrackMinute", "ToolPwdGenCrackMinutes");
        }
        else if (secondsAvg < 86400)
        {
            timeStr = Counted((int)(secondsAvg / 3600), "ToolPwdGenCrackHour", "ToolPwdGenCrackHours");
        }
        else if (secondsAvg < 365.25 * 86400)
        {
            timeStr = Counted((int)(secondsAvg / 86400), "ToolPwdGenCrackDay", "ToolPwdGenCrackDays");
        }
        else if (secondsAvg < 100 * 365.25 * 86400)
        {
            timeStr = Counted(
                (int)(secondsAvg / (365.25 * 86400)), "ToolPwdGenCrackYear", "ToolPwdGenCrackYears");
        }
        else if (secondsAvg < 1_000_000 * 365.25 * 86400)
        {
            timeStr = Counted(
                (int)(secondsAvg / (100 * 365.25 * 86400)),
                "ToolPwdGenCrackCentury",
                "ToolPwdGenCrackCenturies");
        }
        else
        {
            timeStr = L("ToolPwdGenCrackForever");
        }

        CrackTimeText = string.Format(L("ToolPwdGenCrackTime"), timeStr);
        CrackTimeAssumptionText = string.Format(L("ToolPwdGenCrackAssumption"), GuessRateText());
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
            // How far these settings do reach is the difference between a refusal and an answer,
            // and it belongs in the sentence that was already saying the refusal. Said twice, in
            // two places, in two colours, it reads as two different problems.
            issues.Add(string.Format(
                L("ToolPwdGenIssueFloorUnreachable"),
                EntropyFloorBits,
                (int)Math.Floor(FloorCeilingBits())));
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
        CrackTimeAssumptionText = string.Empty;
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

    private PasswordGeneratorStore LoadStore()
        => _cachedStore ??= _presetStorage.Load();

    private List<PasswordPreset> LoadCustomPresets() => LoadStore().Presets;

    private void SaveCustomPresets(List<PasswordPreset> presets)
    {
        var store = LoadStore();
        store.Presets = presets;
        _presetStorage.Save(store);
    }

    /// <summary>
    /// Writes down where the tool was left, or forgets it.
    /// </summary>
    /// <remarks>
    /// Called when the switch is turned, and when the tool is put away. It is not called on every
    /// change: a file written on every slider movement would be a file written on every pixel of a
    /// drag, and the whole of this setting is a convenience.
    /// </remarks>
    internal void PersistSettingsIfRemembering()
    {
        // Nothing is written while the tool is still being set up. Turning the switch on during a
        // restore would otherwise save the defaults over the very snapshot being restored, which
        // is what it did: the settings came back for exactly as long as it took to overwrite them.
        if (!_isInitialized)
        {
            return;
        }

        var store = LoadStore();
        store.RememberSettings = RememberSettings;
        store.Settings = RememberSettings ? SnapshotCurrentPreset(string.Empty) : null;
        _presetStorage.Save(store);
    }

    partial void OnRememberSettingsChanged(bool value) => PersistSettingsIfRemembering();

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

    /// <summary>
    /// What the chosen language is worth: how many words it draws from, and the bits one draw
    /// carries.
    /// </summary>
    /// <remarks>
    /// The lists are not the same size, and a passphrase of four words is worth what its own list
    /// gives it: 47 bits drawn from the English list of 3525 words, 38 from the Spanish list of
    /// 725. The strength figure has always counted this correctly and the minimum strength has
    /// always enforced it, but neither of them says why one language needs a longer passphrase
    /// than another, and the language box is where that is decided.
    /// </remarks>
    private void UpdateWordListSummary()
    {
        var words = SelectedWordList();

        WordListSummaryText = words.Length < 2
            ? string.Empty
            : string.Format(
                L("ToolPwdGenWordListSize"),
                words.Length.ToString(CultureInfo.InvariantCulture),
                Math.Log2(words.Length).ToString("0.0", CultureInfo.InvariantCulture));
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
