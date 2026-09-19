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

using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Password generator with Random, Syllable, and Passphrase modes, plus a 5-level strength indicator.
/// </summary>
public partial class PasswordGeneratorView : UserControl, IToolView
{

    /// <summary>The width of a placement cursor, which the track has to leave room for.</summary>
    private const double PlacementCursorWidth = PlacementBarGeometry.CursorWidth;

    /// <summary>How far down the track the notches hang from.</summary>
    private const double PlacementTickBaseline = 28;

    /// <summary>How many places each track was last drawn with, so it is not redrawn for nothing.</summary>
    private int _lastDigitSlots = -1;
    private int _lastSpecialSlots = -1;

    private LocalizationManager? _localizer;
    private readonly PasswordGeneratorViewModel _vm;
    private bool _viewInitialized;
    private DispatcherTimer? _clipboardClearTimer;
    private DispatcherTimer? _clipboardHintTimer;
    private int _clipboardSecondsLeft;
    private string? _lastCopiedPassword;

    public PasswordGeneratorView()
    {
        InitializeComponent();
        _vm = new PasswordGeneratorViewModel(ResolvePresetStorage());
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>The one place the production preset location is reached.</summary>
    /// <remarks>
    /// <b>Deliberately without a fallback.</b> A fallback that resolved the real user
    /// directory would rebuild the hazard this wiring removes - the point is that no path
    /// outside the composition root can reach the operator's own preset file. Throwing is
    /// louder than silently writing to it, and this view is only ever created by the running
    /// application: no XAML instantiates it and no test builds it.
    /// </remarks>
    private static IPasswordPresetStorage ResolvePresetStorage()
        => (Application.Current as App)?.Services?.GetService<IPasswordPresetStorage>()
            ?? throw new InvalidOperationException(
                "Password preset storage is unavailable. This view must be created by the "
                + "running application, which supplies it from the composition root.");

    /// <summary>
    /// Initializes the view with optional context and localizer.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        PopulateComboBoxes();
        ApplyLocalization();
        RebuildCustomPresetButtons();
        _vm.Initialize(context, localizer);
        var dialogService = (Application.Current as App)?.Services?.GetService<IDialogService>();
        if (dialogService is not null)
        {
            _vm.SetDialogService((title, message) => dialogService.ShowConfirmAsync(title, message, "warning"));
        }
        _viewInitialized = true;
        RebuildCaseBlockButtons();
        RebuildPlacementCursors();
        RebuildCustomPresetButtons();
        UpdateModeDescription();
        UpdateSyllableUiHints();
        UpdateStrengthBarBrush();
        UpdateStrengthBarWidth();
        SylTotalLengthText.Text = string.Format(L("ToolPwdGenTotalLength"), _vm.SyllableTotalLength);
        UpdateQuickLengthHighlight();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            LengthSlider.Focus();
        });
    }

    private void PopulateComboBoxes()
    {
        // Mode selector
        CmbMode.Items.Clear();
        CmbMode.Items.Add(L("ToolPwdGenModeRandom"));
        CmbMode.Items.Add(L("ToolPwdGenModeSyllable"));
        CmbMode.Items.Add(L("ToolPwdGenModePassphrase"));
        CmbMode.Items.Add(L("ToolPwdGenModeLeet"));
        CmbMode.SelectedIndex = 0;

        CmbSylCase.Items.Clear();
        CmbSylCase.Items.Add(L("ToolPwdGenCaseMixed"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseLower"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseUpper"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseTitle"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseAlternating"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseWordCase"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseInverse"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseBlocks"));
        CmbSylCase.SelectedIndex = 0;

        // Syllable placement
        CmbSylPlacement.Items.Clear();
        CmbSylPlacement.Items.Add(L("ToolPwdGenPlacementRandom"));
        CmbSylPlacement.Items.Add(L("ToolPwdGenPlacementStart"));
        CmbSylPlacement.Items.Add(L("ToolPwdGenPlacementEnd"));
        CmbSylPlacement.Items.Add(L("ToolPwdGenPlacementMiddle"));
        CmbSylPlacement.Items.Add(L("ToolPwdGenPlacementPositions"));
        CmbSylPlacement.SelectedIndex = 0;

        // Passphrase placement
        CmbPpPlacement.Items.Clear();
        CmbPpPlacement.Items.Add(L("ToolPwdGenPlacementRandom"));
        CmbPpPlacement.Items.Add(L("ToolPwdGenPlacementStart"));
        CmbPpPlacement.Items.Add(L("ToolPwdGenPlacementEnd"));
        CmbPpPlacement.Items.Add(L("ToolPwdGenPlacementMiddle"));
        CmbPpPlacement.Items.Add(L("ToolPwdGenPlacementPositions"));
        CmbPpPlacement.SelectedIndex = 0;

        TxtCustomSpecials.Text = PasswordGeneratorViewModel.DefaultSymbolChars;

        CmbPpLanguage.Items.Clear();
        foreach (PasswordGeneratorViewModel.PassphraseLanguage language in PasswordGeneratorViewModel.PassphraseLanguages)
        {
            CmbPpLanguage.Items.Add(L(language.LabelKey));
        }

        CmbPpLanguage.SelectedIndex =
            PasswordGeneratorViewModel.PassphraseLanguageIndexFor(_localizer?.CurrentLocale);

        // The leet panel reads the same case, placement and language vocabulary as the two
        // panels above it, and the language box is bound to the same index, so a language
        // chosen for passphrases is the language a drawn base word comes from.
        CmbLeetCase.Items.Clear();
        foreach (object? item in CmbSylCase.Items)
        {
            CmbLeetCase.Items.Add(item);
        }

        CmbLeetCase.SelectedIndex = 0;

        CmbPpCase.Items.Clear();
        foreach (object? item in CmbSylCase.Items)
        {
            CmbPpCase.Items.Add(item);
        }

        CmbPpCase.SelectedIndex = (int)PasswordGeneratorViewModel.SyllableCase.WordCase;

        CmbLeetPlacement.Items.Clear();
        foreach (object? item in CmbSylPlacement.Items)
        {
            CmbLeetPlacement.Items.Add(item);
        }

        CmbLeetPlacement.SelectedIndex = 0;

        CmbLeetLanguage.Items.Clear();
        foreach (object? item in CmbPpLanguage.Items)
        {
            CmbLeetLanguage.Items.Add(item);
        }

        CmbLeetLanguage.SelectedIndex = CmbPpLanguage.SelectedIndex;

        CmbEntropyFloor.Items.Clear();
        foreach (int bits in PasswordGeneratorViewModel.EntropyFloorChoices)
        {
            CmbEntropyFloor.Items.Add(bits == 0
                ? L("ToolPwdGenEntropyFloorOff")
                : $"{bits} {L("ToolPwdGenBits")}");
        }

        CmbEntropyFloor.SelectedIndex = 0;

        CmbClipboardDelay.Items.Clear();
        foreach (int seconds in PasswordGeneratorViewModel.ClipboardClearChoices)
        {
            CmbClipboardDelay.Items.Add($"{seconds} {L("ToolPwdGenSeconds")}");
        }

        CmbClipboardDelay.SelectedIndex = 0;
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolPwdGenTitle");
        ModeLabel.Text = L("ToolPwdGenMode");
        ModeDescription.Text = L("ToolPwdGenModeRandomDesc");
        BtnRegenerate.Content = L("ToolPwdGenBtnGenerate");
        BtnRegenerate.ToolTip = L("TooltipRegenerate");
        BtnCopy.Content = L("ToolPwdGenBtnCopy");
        BtnCopy.ToolTip = L("TooltipCopyPassword");

        // Random mode
        LengthLabel.Text = L("ToolPwdGenLength");
        ChkUppercase.Content = L("ToolPwdGenUppercase");
        ChkLowercase.Content = L("ToolPwdGenLowercase");
        ChkDigits.Content = L("ToolPwdGenDigits");
        ChkSymbols.Content = L("ToolPwdGenSymbols");

        // Advanced options
        AdvancedExpander.Header = L("ToolPwdGenAdvanced");
        ChkExcludeAmbiguous.Content = L("ToolPwdGenExcludeAmbiguous");
        ChkCliSafe.Content = L("ToolPwdGenCliSafe");
        ChkClipboardAutoClear.Content = L("ToolPwdGenClipboardAutoClear");
        CustomSpecialsLabel.Text = L("ToolPwdGenCustomSpecials");

        // Presets
        PresetsLabel.Text = L("ToolPwdGenQuickPresets");
        BtnPresetPin4.Content = L("ToolPwdGenPresetPin4");
        BtnPresetPin6.Content = L("ToolPwdGenPresetPin6");
        BtnPresetWifi.Content = L("ToolPwdGenPresetWifi");
        BtnPresetApiKey.Content = L("ToolPwdGenPresetApiKey");
        BtnPresetMysql.Content = L("ToolPwdGenPresetMysql");
        BtnPresetPassphrase4.Content = L("ToolPwdGenPresetPassphrase4");
        BtnPresetPassphrase6.Content = L("ToolPwdGenPresetPassphrase6");
        BtnPresetSsh.Content = L("ToolPwdGenPresetSsh");
        BtnPresetSylEasy.Content = L("ToolPwdGenPresetSylEasy");
        BtnPresetSylBalanced.Content = L("ToolPwdGenPresetSylBalanced");
        BtnPresetSylStrong.Content = L("ToolPwdGenPresetSylStrong");
        QuickLengthLabel.Text = L("ToolPwdGenQuickLength");
        HistoryLabel.Text = L("ToolPwdGenHistory");
        BtnClearHistory.Content = L("ToolPwdGenClearHistory");
        HistoryEmptyText.Text = L("ToolPwdGenHistoryEmpty");

        // Layout-safe + Phonetic + Keyboard hint
        ChkLayoutSafe.Content = L("ToolPwdGenLayoutSafe");
        PhoneticLabel.Text = L("ToolPwdGenPhonetic");
        BtnCopyPhonetic.Content = L("ToolPwdGenBtnCopyPhonetic");
        BtnCopyPhonetic.ToolTip = L("TooltipCopyPhonetic");
        System.Windows.Automation.AutomationProperties.SetName(BtnCopyPhonetic, L("TooltipCopyPhonetic"));
        KeyboardHintText.Text = L("ToolPwdGenKeyboardHint");

        // Syllable mode
        SylLengthLabel.Text = L("ToolPwdGenLength");
        ChkRememberSettings.Content = L("ToolPwdGenRememberSettings");
        RememberSettingsNote.Text = L("ToolPwdGenRememberSettingsNote");
        SylStepNote.Text = ChkSylCvc.IsChecked == true ? L("ToolPwdGenSylStepNoteCvc") : L("ToolPwdGenSylStepNote");
        SylSeparatorLabel.Text = L("ToolPwdGenSeparator");
        SylCaseLabel.Text = L("ToolPwdGenCase");
        ChkSylCvc.Content = L("ToolPwdGenSylCvc");
        SylCvcHint.Text = L("ToolPwdGenSylCvcHint");
        SylDigitsLabel.Text = L("ToolPwdGenDigits");
        SylSpecialsLabel.Text = L("ToolPwdGenSymbols");
        SylPlacementLabel.Text = L("ToolPwdGenPlacement");
        ChkLeetRandomWord.Content = L("ToolPwdGenLeetRandomWord");
        ChkLeetFullSubstitution.Content = L("ToolPwdGenLeetFullSubstitution");
        LeetWordLabel.Text = L("ToolPwdGenLeetWord");
        LeetLanguageLabel.Text = L("ToolPwdGenLanguage");
        LeetCaseLabel.Text = L("ToolPwdGenCase");
        LeetDigitsLabel.Text = L("ToolPwdGenDigits");
        LeetSpecialsLabel.Text = L("ToolPwdGenSymbols");
        LeetPlacementLabel.Text = L("ToolPwdGenPlacement");
        LeetWordSourceLabel.Text = L("ToolPwdGenLeetWordSource");
        EntropyFloorLabel.Text = L("ToolPwdGenEntropyFloor");
        CaseBlocksLabel.Text = L("ToolPwdGenBlocks");
        BatchCountLabel.Text = L("ToolPwdGenBatchCount");
        BatchLabel.Text = L("ToolPwdGenBatch");
        ChkBatchMask.Content = L("ToolPwdGenBatchMask");
        BtnBatchCopyAll.Content = L("ToolPwdGenBatchCopyAll");
        BtnBatchExport.Content = L("ToolPwdGenBatchExport");
        PlacementBarLabel.Text = L("ToolPwdGenPlacementBar");
        PlacementBarHint.Text = L("ToolPwdGenPlacementBarHint");
        PlacementDigitsLabel.Text = L("ToolPwdGenDigits");
        PlacementSpecialsLabel.Text = L("ToolPwdGenSymbols");
        BtnPlacementDistribute.Content = L("ToolPwdGenPlacementDistribute");
        CaseBlocksHint.Text = L("ToolPwdGenBlocksHint");
        BtnCaseBlocksRandom.Content = L("ToolPwdGenBlocksRandom");
        UpdateCaseBlocksAutoSyncLabel();
        SylStructureLabel.Text = L("ToolPwdGenSylStructure");

        // Passphrase mode
        PpWordCountLabel.Text = L("ToolPwdGenWordCount");
        PpSeparatorLabel.Text = L("ToolPwdGenSeparator");
        PpLanguageLabel.Text = L("ToolPwdGenLanguage");
        PpCaseLabel.Text = L("ToolPwdGenCase");
        PpDigitsLabel.Text = L("ToolPwdGenDigits");
        PpSpecialsLabel.Text = L("ToolPwdGenSymbols");
        PpPlacementLabel.Text = L("ToolPwdGenPlacement");

        // Accessibility
        System.Windows.Automation.AutomationProperties.SetName(BtnRegenerate, L("ToolPwdGenBtnGenerate"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopy, L("ToolPwdGenBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(PasswordOutput, L("ToolPwdGenOutputName"));
        System.Windows.Automation.AutomationProperties.SetName(CmbMode, L("ToolPwdGenMode"));
        System.Windows.Automation.AutomationProperties.SetName(LengthSlider, L("ToolPwdGenLength"));
        System.Windows.Automation.AutomationProperties.SetName(ChkUppercase, L("ToolPwdGenUppercase"));
        System.Windows.Automation.AutomationProperties.SetName(ChkLowercase, L("ToolPwdGenLowercase"));
        System.Windows.Automation.AutomationProperties.SetName(ChkDigits, L("ToolPwdGenDigits"));
        System.Windows.Automation.AutomationProperties.SetName(ChkSymbols, L("ToolPwdGenSymbols"));
        System.Windows.Automation.AutomationProperties.SetName(ChkExcludeAmbiguous, L("ToolPwdGenExcludeAmbiguous"));
        System.Windows.Automation.AutomationProperties.SetName(ChkCliSafe, L("ToolPwdGenCliSafe"));
        System.Windows.Automation.AutomationProperties.SetName(ChkClipboardAutoClear, L("ToolPwdGenClipboardAutoClear"));
        System.Windows.Automation.AutomationProperties.SetName(TxtCustomSpecials, L("ToolPwdGenCustomSpecials"));
        System.Windows.Automation.AutomationProperties.SetName(CmbSylPlacement, L("ToolPwdGenPlacement"));
        System.Windows.Automation.AutomationProperties.SetName(CmbPpPlacement, L("ToolPwdGenPlacement"));
        System.Windows.Automation.AutomationProperties.SetName(ChkLayoutSafe, L("ToolPwdGenLayoutSafe"));
        System.Windows.Automation.AutomationProperties.SetName(SylLengthSlider, L("ToolPwdGenLength"));
        System.Windows.Automation.AutomationProperties.SetName(CmbSylCase, L("ToolPwdGenCase"));
        System.Windows.Automation.AutomationProperties.SetName(SylDigitsSlider, L("ToolPwdGenDigits"));
        System.Windows.Automation.AutomationProperties.SetName(SylSpecialsSlider, L("ToolPwdGenSymbols"));
        System.Windows.Automation.AutomationProperties.SetName(TxtSylSeparator, L("ToolPwdGenSeparator"));
        System.Windows.Automation.AutomationProperties.SetName(ChkSylCvc, L("ToolPwdGenSylCvc"));
        System.Windows.Automation.AutomationProperties.SetName(PpWordCountSlider, L("ToolPwdGenWordCount"));
        System.Windows.Automation.AutomationProperties.SetName(TxtPpSeparator, L("ToolPwdGenSeparator"));
        System.Windows.Automation.AutomationProperties.SetName(CmbPpLanguage, L("ToolPwdGenLanguage"));
        System.Windows.Automation.AutomationProperties.SetName(CmbLeetLanguage, L("ToolPwdGenLanguage"));
        System.Windows.Automation.AutomationProperties.SetName(CmbEntropyFloor, L("ToolPwdGenEntropyFloor"));
        System.Windows.Automation.AutomationProperties.SetName(BatchCountSlider, L("ToolPwdGenBatchCount"));
        System.Windows.Automation.AutomationProperties.SetName(CmbClipboardDelay, L("ToolPwdGenClipboardDelay"));
        System.Windows.Automation.AutomationProperties.SetName(BtnBatchCopyAll, L("ToolPwdGenBatchCopyAll"));
        System.Windows.Automation.AutomationProperties.SetName(BtnBatchExport, L("ToolPwdGenBatchExport"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCaseBlockAdd, L("ToolPwdGenBlocksAdd"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCaseBlockRemove, L("ToolPwdGenBlocksRemove"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCaseBlocksAllUpper, L("ToolPwdGenBlocksAllUpper"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCaseBlocksAllLower, L("ToolPwdGenBlocksAllLower"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCaseBlocksAllTitle, L("ToolPwdGenBlocksAllTitle"));
        System.Windows.Automation.AutomationProperties.SetName(CmbLeetCase, L("ToolPwdGenCase"));
        System.Windows.Automation.AutomationProperties.SetName(CmbLeetPlacement, L("ToolPwdGenPlacement"));
        System.Windows.Automation.AutomationProperties.SetName(TxtLeetWord, L("ToolPwdGenLeetWord"));
        System.Windows.Automation.AutomationProperties.SetName(LeetDigitsSlider, L("ToolPwdGenDigits"));
        System.Windows.Automation.AutomationProperties.SetName(LeetSpecialsSlider, L("ToolPwdGenSymbols"));
        System.Windows.Automation.AutomationProperties.SetName(CmbPpCase, L("ToolPwdGenCase"));
        System.Windows.Automation.AutomationProperties.SetName(PpDigitsSlider, L("ToolPwdGenDigits"));
        System.Windows.Automation.AutomationProperties.SetName(PpSpecialsSlider, L("ToolPwdGenSymbols"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetPin4, L("ToolPwdGenPresetPin4"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetPin6, L("ToolPwdGenPresetPin6"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetWifi, L("ToolPwdGenPresetWifi"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetApiKey, L("ToolPwdGenPresetApiKey"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetMysql, L("ToolPwdGenPresetMysql"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetPassphrase4, L("ToolPwdGenPresetPassphrase4"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetPassphrase6, L("ToolPwdGenPresetPassphrase6"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetSsh, L("ToolPwdGenPresetSsh"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetSylEasy, L("ToolPwdGenPresetSylEasy"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetSylBalanced, L("ToolPwdGenPresetSylBalanced"));
        System.Windows.Automation.AutomationProperties.SetName(BtnPresetSylStrong, L("ToolPwdGenPresetSylStrong"));

        BtnSavePreset.Content = L("ToolPwdGenBtnSavePreset");
        BtnSavePreset.ToolTip = L("TooltipSavePreset");
        System.Windows.Automation.AutomationProperties.SetName(BtnSavePreset, L("ToolPwdGenBtnSavePreset"));
        BtnClearHistory.ToolTip = L("TooltipClearHistory");
        System.Windows.Automation.AutomationProperties.SetName(BtnClearHistory, L("TooltipClearHistory"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        // Quick length button accessibility
        var lengthLabel = L("ToolPwdGenLength");
        foreach (var child in QuickLengthPanel.Children)
        {
            if (child is Button btn && btn.Tag is string tagStr)
                System.Windows.Automation.AutomationProperties.SetName(btn, $"{lengthLabel} {tagStr}");
        }

        // The strength bar is named after the figure it shows, which UpdateStrengthBarBrush
        // refreshes on every generation. Until the first one there is no figure to give.
        System.Windows.Automation.AutomationProperties.SetName(StrengthBar, L("ToolPwdGenStrength"));
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.Length), StringComparison.Ordinal))
        {
            UpdateQuickLengthHighlight();
        }
        else if (string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.CaseBlocks), StringComparison.Ordinal))
        {
            RebuildCaseBlockButtons();
        }
        else if (string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.DigitPositions), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.SpecialPositions), StringComparison.Ordinal))
        {
            // The lists also change without a drag: a slider adds a character, and the strength
            // floor buys one of its own.
            RebuildPlacementCursors();
        }
        else if (string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.GeneratedPassword), StringComparison.Ordinal))
        {
            // How many places there are is read off the password, so a longer one has more of
            // them. The notches were only ever redrawn when a cursor moved, which is why they sat
            // still while the length slider ran.
            RefreshPlacementTicks();
        }
        else if (string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.StrengthLevel), StringComparison.Ordinal))
        {
            UpdateStrengthBarBrush();
        }
        else if (string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.StrengthPercent), StringComparison.Ordinal))
        {
            UpdateStrengthBarWidth();
        }
        else if (string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.SyllableTotalLength), StringComparison.Ordinal))
        {
            SylTotalLengthText.Text = string.Format(L("ToolPwdGenTotalLength"), _vm.SyllableTotalLength);
        }
        else if (string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.SelectedModeIndex), StringComparison.Ordinal))
        {
            UpdateModeDescription();
            UpdateCaseBlocksAutoSyncLabel();
            UpdateSyllableUiHints();
            if (_viewInitialized)
            {
                RebuildCustomPresetButtons();
            }
        }
        else if (string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.CustomPresetsChanged), StringComparison.Ordinal))
        {
            RebuildCustomPresetButtons();
        }
    }

    private void UpdateStrengthBarBrush()
    {
        var brushKey = _vm.StrengthLevel switch
        {
            0 => "ErrorBrush",
            1 => "WarningBrush",
            2 => "AccentBrush",
            3 => "InfoBrush",
            _ => "SuccessBrush"
        };

        StrengthBar.Background = (Brush)FindResource(brushKey);
        System.Windows.Automation.AutomationProperties.SetName(StrengthBar, _vm.StrengthText);
    }

    private void UpdateStrengthBarWidth()
    {
        StrengthBarFillColumn.Width = new GridLength(_vm.StrengthPercent, GridUnitType.Star);
        StrengthBarEmptyColumn.Width = new GridLength(1 - _vm.StrengthPercent, GridUnitType.Star);
    }

    /// <summary>
    /// A block covers a syllable in one mode and a word in the other, so the box beside the
    /// pattern says which.
    /// </summary>
    private void UpdateCaseBlocksAutoSyncLabel()
    {
        ChkCaseBlocksAutoSync.Content = _vm.CurrentMode == PasswordGeneratorViewModel.GeneratorMode.Passphrase
            ? L("ToolPwdGenBlocksAutoSyncWords")
            : L("ToolPwdGenBlocksAutoSync");
    }

    private void UpdateModeDescription()
    {
        ModeDescription.Text = _vm.CurrentMode switch
        {
            PasswordGeneratorViewModel.GeneratorMode.Random => L("ToolPwdGenModeRandomDesc"),
            PasswordGeneratorViewModel.GeneratorMode.Syllable => L("ToolPwdGenModeSyllableDesc"),
            PasswordGeneratorViewModel.GeneratorMode.Passphrase => L("ToolPwdGenModePassphraseDesc"),
            PasswordGeneratorViewModel.GeneratorMode.Leet => L("ToolPwdGenModeLeetDesc"),
            _ => string.Empty
        };
    }

    private void UpdateSyllableUiHints()
    {
        var isCvc = _vm.SyllableCvc;
        SylLengthSlider.TickFrequency = isCvc ? 1 : 2;
        SylStepNote.Text = isCvc ? L("ToolPwdGenSylStepNoteCvc") : L("ToolPwdGenSylStepNote");
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_vm.GeneratedPassword))
        {
            try { Clipboard.SetText(_vm.GeneratedPassword); }
            catch (System.Runtime.InteropServices.ExternalException) { return; }
            StartClipboardClearTimer(_vm.GeneratedPassword);
            ShowClipboardClearHint();
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
    }

    private void OnCopyPhoneticClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_vm.PhoneticText))
        {
            try { Clipboard.SetText(_vm.PhoneticText); }
            catch (System.Runtime.InteropServices.ExternalException) { return; }
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
    }

    private void OnSylCvcChanged(object sender, RoutedEventArgs e)
    {
        if (!_viewInitialized)
        {
            return;
        }

        UpdateSyllableUiHints();
    }

    private void OnPasswordOutputGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && !string.IsNullOrEmpty(tb.Text))
        {
            tb.SelectAll();
        }
    }

    private void OnQuickLength(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out var len))
        {
            _vm.Length = len;
        }
    }

    private void UpdateQuickLengthHighlight()
    {
        var currentLength = _vm.Length;
        foreach (var child in QuickLengthPanel.Children)
        {
            if (child is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out var len))
            {
                btn.Style = len == currentLength
                    ? (Style)FindResource("PrimaryButtonStyle")
                    : (Style)FindResource("SecondaryButtonStyle");
            }
        }
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
        => _vm.ClearHistoryCommand.Execute(null);

    private void OnHistoryCopyButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            btn.ToolTip = L("ToolBtnCopyToClipboard");
            System.Windows.Automation.AutomationProperties.SetName(btn, L("ToolBtnCopyToClipboard"));
        }
    }

    private void OnHistoryCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string password)
        {
            try { Clipboard.SetText(password); }
            catch (System.Runtime.InteropServices.ExternalException) { return; }
            StartClipboardClearTimer(password);
            ShowClipboardClearHint();
            CopyFeedbackHelper.ShowCopyFeedback(btn);
        }
    }

    private string L(string key) => _localizer?[key] ?? key;

    // ── Preset handlers ──────────────────────────────────────────────────────

    private void ApplyPresetAndUpdateView(Action applyAction)
    {
        if (!_viewInitialized) return;
        applyAction();
        UpdateQuickLengthHighlight();
        UpdateModeDescription();
        UpdateSyllableUiHints();
    }

    private void OnPresetPin4(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplyRandomPreset(4, false, false, true, false));

    private void OnPresetPin6(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplyRandomPreset(6, false, false, true, false));

    private void OnPresetWifi(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplyRandomPreset(63, true, true, true, true));

    private void OnPresetApiKey(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplyRandomPreset(32, true, false, true, false));

    private void OnPresetMysql(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplyRandomPreset(16, true, true, true, false));

    private void OnPresetPassphrase4(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplyPassphrasePreset(4));

    private void OnPresetSsh(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplyRandomPreset(20, true, true, true, true));

    private void OnPresetSylEasy(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplySyllablePreset(18, 3, 1, 0, "-"));

    private void OnPresetSylBalanced(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplySyllablePreset(24, 0, 2, 1, "-", true));

    private void OnPresetSylStrong(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplySyllablePreset(30, 0, 3, 2, "", true));

    private void OnPresetPassphrase6(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplyPassphrasePreset(6));

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;

        if (e.Key == Key.Enter)
        {
            _vm.Generate();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.ClearOutput();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (!string.IsNullOrEmpty(_vm.GeneratedPassword))
            {
                try { Clipboard.SetText(_vm.GeneratedPassword); }
                catch (System.Runtime.InteropServices.ExternalException) { return; }
                StartClipboardClearTimer(_vm.GeneratedPassword);
                ShowClipboardClearHint();
                CopyFeedbackHelper.ShowCopyFeedback(BtnCopy);
            }
            e.Handled = true;
        }
    }

    // ── Custom presets ─────────────────────────────────────────────────────

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Views.Dialogs.InputDialog(_localizer)
        {
            Owner = Window.GetWindow(this),
            Title = L("ToolPwdGenSavePresetTitle"),
            Prompt = L("ToolPwdGenSavePresetPrompt"),
        };
        if (dialog.ShowDialog() != true) return;

        var name = dialog.InputText.Trim();
        if (string.IsNullOrEmpty(name)) return;

        _vm.SavePreset(name);
    }

    private void OnCaseBlockAdd(object sender, RoutedEventArgs e)
    {
        _vm.AddCaseBlock();
        RebuildCaseBlockButtons();
    }

    private void OnCaseBlockRemove(object sender, RoutedEventArgs e)
    {
        _vm.RemoveCaseBlock();
        RebuildCaseBlockButtons();
    }

    private void OnCaseBlocksRandom(object sender, RoutedEventArgs e)
    {
        _vm.RandomizeCaseBlocks();
        RebuildCaseBlockButtons();
    }

    private void OnCaseBlocksAllUpper(object sender, RoutedEventArgs e) => SetAllCaseBlocks('U');

    private void OnCaseBlocksAllLower(object sender, RoutedEventArgs e) => SetAllCaseBlocks('l');

    private void OnCaseBlocksAllTitle(object sender, RoutedEventArgs e) => SetAllCaseBlocks('T');

    private void SetAllCaseBlocks(char token)
    {
        _vm.SetAllCaseBlocks(token);
        RebuildCaseBlockButtons();
    }

    private void OnCaseBlockClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            _vm.CycleCaseBlock(index);
            RebuildCaseBlockButtons();
        }
    }

    /// <summary>
    /// Rebuilds one button per block. Each one shows its own token and cycles to the next when
    /// clicked, so the pattern is edited where it is read rather than typed into a box.
    /// </summary>
    private void RebuildCaseBlockButtons()
    {
        if (!_viewInitialized)
        {
            return;
        }

        CaseBlocksPanel.Children.Clear();

        string pattern = _vm.CaseBlocks;
        for (int index = 0; index < pattern.Length; index++)
        {
            var button = new Button
            {
                Content = pattern[index].ToString(),
                Tag = index,
                MinWidth = 34,
                Style = (Style)FindResource("SecondaryButtonStyle"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 6, 4),
                Padding = (Thickness)FindResource("PaddingButtonPreset"),
                FontSize = (double)FindResource("FontSizeBodyLarge")
            };

            System.Windows.Automation.AutomationProperties.SetName(
                button,
                string.Format(L("ToolPwdGenBlockButtonName"), index + 1, pattern[index]));
            button.ToolTip = L("ToolPwdGenBlocksHint");
            button.Click += OnCaseBlockClick;
            CaseBlocksPanel.Children.Add(button);
        }
    }

    private void OnPlacementDistribute(object sender, RoutedEventArgs e)
    {
        _vm.DistributePositionsEvenly();
        RebuildPlacementCursors();
    }

    /// <summary>
    /// Rebuilds one cursor per character on each of the two tracks. A cursor is a thumb the
    /// operator drags; it also takes the arrow keys once focused and the wheel while hovered, so
    /// the bar is not a mouse-only control.
    /// </summary>
    /// <summary>
    /// How many places there are to put a character on a track, which is one more than the number
    /// of characters it is read against.
    /// </summary>
    /// <remarks>
    /// The digits are placed into the password as the generator built it, and the specials are
    /// then placed into that same string with the digits already in it, which is what the two rows
    /// one above the other mean. So the two scales differ by the number of digits, and the length
    /// each is read against is recovered from the password on screen rather than kept in a second
    /// place that could disagree with it.
    /// </remarks>
    private int PlacementSlotCount(bool digits) => PlacementBarGeometry.SlotCount(
        _vm.GeneratedPassword.Length,
        _vm.CurrentDigitCount,
        _vm.CurrentSpecialCount,
        digits);

    /// <summary>
    /// Redraws the notches without touching the cursors.
    /// </summary>
    /// <remarks>
    /// This runs on every password, which includes every mouse move of a drag, so it does nothing
    /// when the number of places has not changed. Rebuilding the cursors here instead would take
    /// the thumb out from under the mouse.
    /// </remarks>
    private void RefreshPlacementTicks()
    {
        if (!_viewInitialized)
        {
            return;
        }

        int digits = PlacementSlotCount(true);
        int specials = PlacementSlotCount(false);
        if (digits == _lastDigitSlots && specials == _lastSpecialSlots)
        {
            return;
        }

        _lastDigitSlots = digits;
        _lastSpecialSlots = specials;
        BuildTicks(PlacementDigitsTrack, digits: true);
        BuildTicks(PlacementSpecialsTrack, digits: false);
    }

    private void RebuildPlacementCursors()
    {
        if (!_viewInitialized)
        {
            return;
        }

        _lastDigitSlots = PlacementSlotCount(true);
        _lastSpecialSlots = PlacementSlotCount(false);
        BuildTicks(PlacementDigitsTrack, digits: true);
        BuildTicks(PlacementSpecialsTrack, digits: false);
        BuildCursors(PlacementDigitsTrack, digits: true, _vm.DigitPositions);
        BuildCursors(PlacementSpecialsTrack, digits: false, _vm.SpecialPositions);

        PlacementDigitsRow.Visibility = _vm.CurrentDigitCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlacementSpecialsRow.Visibility = _vm.CurrentSpecialCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Rebuilds a track only when the number of cursors on it changed, and otherwise moves the
    /// ones already there.
    /// </summary>
    /// <remarks>
    /// Rebuilding on every move destroyed the thumb that had the focus, so a second arrow key
    /// went nowhere and the bar worked once per click for anyone driving it from the keyboard.
    /// </remarks>
    private void BuildCursors(Canvas track, bool digits, string positions)
    {
        double[] percents = PasswordGeneratorViewModel.ParsePositions(positions);

        // The canvas also holds the notches, so the cursors are counted rather than the children.
        // Reading Children.Count here meant the canvas was cleared on every move once the notches
        // arrived, and clearing it destroys the thumb the mouse is holding.
        List<Thumb> existingCursors = track.Children.OfType<Thumb>().ToList();

        if (existingCursors.Count == percents.Length)
        {
            for (int index = 0; index < percents.Length; index++)
            {
                DescribeCursor(existingCursors[index], index, percents[index]);
                PositionCursor(track, existingCursors[index], percents[index]);
            }

            return;
        }

        foreach (Thumb stale in existingCursors)
        {
            track.Children.Remove(stale);
        }

        for (int index = 0; index < percents.Length; index++)
        {
            var cursor = new Thumb
            {
                Width = PlacementCursorWidth,
                Height = 22,
                Tag = new PlacementCursor(digits, index),
                Style = (Style)FindResource("PlacementCursorStyle")
            };

            DescribeCursor(cursor, index, percents[index]);
            cursor.DragDelta += OnPlacementCursorDrag;
            cursor.DragCompleted += OnPlacementCursorDropped;
            cursor.KeyDown += OnPlacementCursorKey;
            cursor.MouseWheel += OnPlacementCursorWheel;

            // A Thumb captures the mouse without taking the focus, so a cursor that had just
            // been dragged ignored the arrow keys: the bar read as mouse-only to anyone who
            // tried the keyboard after touching it.
            cursor.PreviewMouseLeftButtonDown += OnPlacementCursorPressed;
            Canvas.SetTop(cursor, 3);
            track.Children.Add(cursor);
            PositionCursor(track, cursor, percents[index]);
        }

        track.SizeChanged -= OnPlacementTrackResized;
        track.SizeChanged += OnPlacementTrackResized;
    }

    /// <summary>
    /// Draws one notch at every place a character can go, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>There used to be a taller notch every fifth, to be counted. Counting them is not how
    /// this bar is read: the password sits above it and the character travels through it as the
    /// cursor moves, so where a cursor is gets read off the password. What the notches are for is
    /// saying that the bar has discrete places at all, and how far apart they are.</para>
    /// <para>They are drawn behind the cursors, and they are the places a drag settles on, so what
    /// is shown and what is possible are the same set of places.</para>
    /// </remarks>
    private void BuildTicks(Canvas track, bool digits)
    {
        foreach (var stale in track.Children.OfType<Line>().ToList())
        {
            track.Children.Remove(stale);
        }

        int slots = PlacementSlotCount(digits);
        int ticks = PlacementBarGeometry.TickCount(slots, track.ActualWidth);
        if (ticks == 0)
        {
            return;
        }

        var brush = (Brush)FindResource("TextSecondaryBrush");

        for (int slot = 0; slot < ticks; slot++)
        {
            double x = PlacementBarGeometry.TickX(slot, ticks, track.ActualWidth);

            var tick = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = PlacementTickBaseline - PlacementBarGeometry.TickHeight,
                Y2 = PlacementTickBaseline,
                Stroke = brush,
                StrokeThickness = 1,
                Opacity = PlacementBarGeometry.TickOpacity,
                IsHitTestVisible = false,
            };

            track.Children.Insert(0, tick);
        }
    }

    /// <summary>
    /// The nearest notch to a percentage, so a dragged cursor lands where a character can go.
    /// </summary>
    private double SnapToSlot(bool digits, double percent) =>
        PlacementBarGeometry.SnapToSlot(PlacementSlotCount(digits), percent);

    /// <summary>Names a cursor after where it sits, for the tooltip and for assistive technology.</summary>
    private void DescribeCursor(Thumb cursor, int index, double percent)
    {
        string description = string.Format(
            CultureInfo.CurrentCulture,
            L("ToolPwdGenPlacementCursorName"),
            index + 1,
            percent);

        cursor.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(cursor, description);
    }

    /// <summary>
    /// Puts everything back where it belongs once the track's width is known, or changed.
    /// </summary>
    /// <remarks>
    /// The notches and the cursors are both placed as a fraction of a width, and the first time
    /// this panel is laid out that width is zero: everything drawn before then lands on the left
    /// edge. Nothing here is a reaction to a resize alone; it is also how the first real width
    /// reaches the drawing.
    /// </remarks>
    private void OnPlacementTrackResized(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Canvas track)
        {
            return;
        }

        BuildTicks(track, digits: ReferenceEquals(track, PlacementDigitsTrack));

        foreach (Thumb cursor in track.Children.OfType<Thumb>())
        {
            // The one under the mouse is where the mouse put it, which is not yet written down.
            // The panel above this one relays out as the password changes, so a resize can arrive
            // in the middle of a drag, and putting every thumb back where the settings say would
            // take that one out from under the hand holding it.
            if (cursor.IsDragging)
            {
                continue;
            }

            if (cursor.Tag is PlacementCursor slot)
            {
                double[] percents = PasswordGeneratorViewModel.ParsePositions(
                    slot.Digits ? _vm.DigitPositions : _vm.SpecialPositions);
                if (slot.Index < percents.Length)
                {
                    PositionCursor(track, cursor, percents[slot.Index]);
                }
            }
        }
    }

    private static void PositionCursor(Canvas track, Thumb cursor, double percent)
    {
        Canvas.SetLeft(cursor, PlacementBarGeometry.CursorLeft(percent, track.ActualWidth));
    }

    private static void OnPlacementCursorPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is Thumb cursor)
        {
            cursor.Focus();
        }
    }

    /// <summary>
    /// Moves the cursor under the pointer, and nothing else, for as long as the drag lasts.
    /// </summary>
    /// <remarks>
    /// <para>This used to write the position on every mouse move. Two things followed from that,
    /// and both were felt rather than seen. Writing the position regenerates the password, so the
    /// machine drew a new one on every WM_MOUSEMOVE, and twenty of them when a batch had been
    /// asked for. And the write rounded the position to a tenth of a percent and put the cursor
    /// back on that grid, while the next mouse move measured its delta from the position it had
    /// just been moved to: the cursor pulled against the pointer and the two drifted apart.</para>
    /// <para>The cursor now follows the pointer in pixels and the position is written once, when
    /// the drag ends. The keyboard and the wheel still write immediately, because a key press is
    /// one discrete move and one password, which is the behaviour anyone would expect of it.</para>
    /// </remarks>
    private void OnPlacementCursorDrag(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: PlacementCursor slot } cursor
            || cursor.Parent is not Canvas track)
        {
            return;
        }

        // The change a Thumb reports is where the pointer now sits relative to where it took
        // hold, measured against the thumb as it currently stands. Adding it to the thumb's own
        // left edge therefore gives the pointer itself: nothing is accumulated, and the part of a
        // move that snapping does not use is still there to be counted on the next one.
        double usable = PlacementBarGeometry.Usable(track.ActualWidth);
        double moved = Math.Clamp(Canvas.GetLeft(cursor) + e.HorizontalChange, 0, usable);
        double percent = SnapToSlot(slot.Digits, moved / usable * 100.0);

        // The cursor settles on the notch rather than following the pointer between two of them,
        // so what it shows is a place a character can actually go.
        Canvas.SetLeft(cursor, PlacementBarGeometry.CursorLeft(percent, track.ActualWidth));
        DescribeCursor(cursor, slot.Index, percent);

        // And the password on screen shows the character at that place, straight away. It is the
        // one already in it that moves; nothing new is drawn until the operator asks for it.
        _vm.TryMoveInPlace(slot.Digits, slot.Index, percent, commit: false);
    }

    /// <summary>
    /// Commits where the cursor was dropped: one position, one password.
    /// </summary>
    private void OnPlacementCursorDropped(object sender, DragCompletedEventArgs e)
    {
        if (sender is not Thumb { Tag: PlacementCursor slot } cursor
            || cursor.Parent is not Canvas track)
        {
            return;
        }

        MovePlacementCursor(slot, SnapToSlot(
            slot.Digits,
            Canvas.GetLeft(cursor) / PlacementBarGeometry.Usable(track.ActualWidth) * 100.0));
    }

    private void OnPlacementCursorKey(object sender, KeyEventArgs e)
    {
        if (sender is not Thumb { Tag: PlacementCursor slot })
        {
            return;
        }

        // One press moves by one place. A fixed slice of the bar lands between two notches as
        // soon as the password is not fifty characters long, which is a place no character goes.
        double one = PlacementBarGeometry.StepPercent(PlacementSlotCount(slot.Digits));
        double step = e.Key switch
        {
            Key.Left or Key.Down => -one,
            Key.Right or Key.Up => one,
            Key.Home => -100,
            Key.End => 100,
            _ => 0
        };

        if (step == 0)
        {
            return;
        }

        double[] percents = PasswordGeneratorViewModel.ParsePositions(
            slot.Digits ? _vm.DigitPositions : _vm.SpecialPositions);
        if (slot.Index < percents.Length)
        {
            MovePlacementCursor(slot, SnapToSlot(slot.Digits, percents[slot.Index] + step));
            e.Handled = true;
        }
    }

    private void OnPlacementCursorWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Thumb { Tag: PlacementCursor slot })
        {
            return;
        }

        double one = PlacementBarGeometry.StepPercent(PlacementSlotCount(slot.Digits));
        double[] percents = PasswordGeneratorViewModel.ParsePositions(
            slot.Digits ? _vm.DigitPositions : _vm.SpecialPositions);
        if (slot.Index < percents.Length && one > 0)
        {
            MovePlacementCursor(
                slot,
                SnapToSlot(slot.Digits, percents[slot.Index] + (e.Delta > 0 ? one : -one)));
            e.Handled = true;
        }
    }

    /// <summary>
    /// Writes down where a cursor ended up, keeping the password that was shown while it moved.
    /// </summary>
    /// <remarks>
    /// Writing the position regenerates, and a drag that previews one password and commits another
    /// is worse than one that previews nothing. The characters already drawn are put back in the
    /// new order instead. When that cannot be done, because the material behind the password on
    /// screen no longer describes it, the position is written the old way and a new password comes
    /// out: a fresh password is a fair outcome, a wrong one is not.
    /// </remarks>
    private void MovePlacementCursor(PlacementCursor slot, double percent)
    {
        if (!_vm.TryMoveInPlace(slot.Digits, slot.Index, percent, commit: true))
        {
            _vm.MovePosition(slot.Digits, slot.Index, percent);
        }

        RebuildPlacementCursors();
    }

    /// <summary>Which track a cursor belongs to, and which character on it.</summary>
    private readonly record struct PlacementCursor(bool Digits, int Index);

    private void OnBatchCopyAll(object sender, RoutedEventArgs e)
    {
        CopyBatchText(_vm.BatchAsText, sender as Button);
    }

    private void OnBatchCopyRow(object sender, RoutedEventArgs e)
    {
        // The row shows dots while the batch is masked, so what is copied is read from the batch
        // itself, by the place the row sits in.
        if (sender is Button { Tag: string row } button)
        {
            int index = _vm.BatchRows.IndexOf(row);
            if (index >= 0 && index < _vm.GeneratedBatch.Count)
            {
                CopyBatchText(_vm.GeneratedBatch[index], button);
            }
        }
    }

    private void CopyBatchText(string text, Button? source)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        StartClipboardClearTimer(text);
        ShowClipboardClearHint();
        CopyFeedbackHelper.ShowCopyFeedback(source);
    }

    private void OnBatchExport(object sender, RoutedEventArgs e)
    {
        if (_vm.GeneratedBatch.Count == 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("FileDialogTextFilter"),
            FileName = $"passwords_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _vm.BatchAsText, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BatchLabel.Text = string.Format(L("ToolPwdGenBatchExportFailed"), exception.Message);
        }
    }

    private void RebuildCustomPresetButtons()
    {
        PanelCustomPresets.Children.Clear();
        SavedPresetsLabel.Visibility = Visibility.Collapsed;
        SavedPresetsElsewhereText.Visibility = Visibility.Collapsed;
        if (!_viewInitialized) return;

        var filtered = _vm.GetCustomPresetsForCurrentMode();
        int elsewhere = _vm.CustomPresetCount - filtered.Count;

        // A preset saved in another mode is not shown here, and used to be shown nowhere: the
        // operator saved one, came back to a different mode, and found an empty row.
        if (elsewhere > 0)
        {
            SavedPresetsElsewhereText.Text = string.Format(
                L("ToolPwdGenSavedPresetsElsewhere"),
                elsewhere.ToString(CultureInfo.InvariantCulture));
            SavedPresetsElsewhereText.Visibility = Visibility.Visible;
        }

        if (filtered.Count == 0) return;

        SavedPresetsLabel.Text = L("ToolPwdGenSavedPresets");
        SavedPresetsLabel.Visibility = Visibility.Visible;

        foreach (var preset in filtered)
        {
            var btn = new Button
            {
                Content = preset.Name,
                Tag = preset,
                Style = (Style)FindResource("SecondaryButtonStyle"),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 4),
                FontSize = (double)FindResource("FontSizeCaption"),
            };
            btn.Click += (_, _) => ApplyPresetAndUpdateView(() => _vm.ApplyPreset(preset));
            btn.ToolTip = L("ToolPwdGenPresetRightClickHint");
            System.Windows.Automation.AutomationProperties.SetName(btn, preset.Name);

            var deleteItem = new MenuItem { Header = L("ToolPwdGenDeletePreset") };
            var capturedPreset = preset;
            deleteItem.Click += async (_, _) =>
            {
                if (await _vm.DeletePresetAsync(capturedPreset.Name))
                {
                    RebuildCustomPresetButtons();
                }
            };
            btn.ContextMenu = new ContextMenu();
            btn.ContextMenu.Items.Add(deleteItem);

            PanelCustomPresets.Children.Add(btn);
        }
    }

    /// <summary>
    /// Writes down where the tool was left, when it has been told to.
    /// </summary>
    /// <remarks>
    /// The settings are saved here rather than on every change: a file written whenever a slider
    /// moves is a file written on every pixel of a drag. Unloading covers closing the tab, closing
    /// the window and switching away from the tool, which is every way of leaving it that matters.
    /// </remarks>
    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewInitialized)
        {
            _vm.PersistSettingsIfRemembering();
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtHelpContent.Text = L("ToolHelpPASSWORD").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Counts the copied password down to the moment it leaves the clipboard.
    /// </summary>
    /// <remarks>
    /// The line used to say "will auto-clear" for three seconds and then go back to the keyboard
    /// hint, which told the operator a delay was running but never how much of it was left. It now
    /// ticks, and says so when the clipboard is cleared.
    /// </remarks>
    private void ShowClipboardClearHint()
    {
        if (!_vm.ClipboardAutoClear)
        {
            return;
        }

        _clipboardHintTimer?.Stop();
        _clipboardSecondsLeft = _vm.ClipboardClearSeconds;
        KeyboardHintText.Text = string.Format(L("ToolPwdGenClipboardClearHint"), _clipboardSecondsLeft);

        _clipboardHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clipboardHintTimer.Tick += (_, _) =>
        {
            _clipboardSecondsLeft--;
            if (_clipboardSecondsLeft > 0)
            {
                KeyboardHintText.Text = string.Format(
                    L("ToolPwdGenClipboardClearHint"),
                    _clipboardSecondsLeft);
                return;
            }

            _clipboardHintTimer?.Stop();
            KeyboardHintText.Text = L("ToolPwdGenClipboardCleared");

            var revertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            revertTimer.Tick += (_, _) =>
            {
                revertTimer.Stop();
                KeyboardHintText.Text = L("ToolPwdGenKeyboardHint");
            };

            revertTimer.Start();
        };

        _clipboardHintTimer.Start();
    }
    private void StartClipboardClearTimer(string password)
    {
        if (!_vm.ClipboardAutoClear) return;

        _lastCopiedPassword = password;
        _clipboardClearTimer?.Stop();
        _clipboardClearTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_vm.ClipboardClearSeconds)
        };
        _clipboardClearTimer.Tick += (_, _) =>
        {
            _clipboardClearTimer.Stop();
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == _lastCopiedPassword)
                    Clipboard.Clear();
            }
            catch { /* clipboard may be locked by another app */ }
            _lastCopiedPassword = null;
        };
        _clipboardClearTimer.Start();
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _clipboardClearTimer?.Stop();
        GC.SuppressFinalize(this);
    }
}
