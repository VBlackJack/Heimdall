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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private const int ClipboardClearDelaySeconds = 30;

    private LocalizationManager? _localizer;
    private readonly PasswordGeneratorViewModel _vm;
    private bool _viewInitialized;
    private DispatcherTimer? _clipboardClearTimer;
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
        CmbMode.SelectedIndex = 0;

        CmbSylCase.Items.Clear();
        CmbSylCase.Items.Add(L("ToolPwdGenCaseMixed"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseLower"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseUpper"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseTitle"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseAlternating"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseWordCase"));
        CmbSylCase.Items.Add(L("ToolPwdGenCaseInverse"));
        CmbSylCase.SelectedIndex = 0;

        // Syllable placement
        CmbSylPlacement.Items.Clear();
        CmbSylPlacement.Items.Add(L("ToolPwdGenPlacementRandom"));
        CmbSylPlacement.Items.Add(L("ToolPwdGenPlacementStart"));
        CmbSylPlacement.Items.Add(L("ToolPwdGenPlacementEnd"));
        CmbSylPlacement.Items.Add(L("ToolPwdGenPlacementMiddle"));
        CmbSylPlacement.SelectedIndex = 0;

        // Passphrase placement
        CmbPpPlacement.Items.Clear();
        CmbPpPlacement.Items.Add(L("ToolPwdGenPlacementRandom"));
        CmbPpPlacement.Items.Add(L("ToolPwdGenPlacementStart"));
        CmbPpPlacement.Items.Add(L("ToolPwdGenPlacementEnd"));
        CmbPpPlacement.Items.Add(L("ToolPwdGenPlacementMiddle"));
        CmbPpPlacement.SelectedIndex = 0;

        TxtCustomSpecials.Text = PasswordGeneratorViewModel.DefaultSymbolChars;

        CmbPpLanguage.Items.Clear();
        CmbPpLanguage.Items.Add(L("ToolPwdGenLangEnglish"));
        CmbPpLanguage.Items.Add(L("ToolPwdGenLangFrench"));
        CmbPpLanguage.SelectedIndex = string.Equals(_localizer?.CurrentLocale, "fr", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
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
        SylLengthLabel.Text = L("ToolPwdGenBaseLength");
        SylStepNote.Text = ChkSylCvc.IsChecked == true ? L("ToolPwdGenSylStepNoteCvc") : L("ToolPwdGenSylStepNote");
        SylSeparatorLabel.Text = L("ToolPwdGenSeparator");
        SylCaseLabel.Text = L("ToolPwdGenCase");
        ChkSylCvc.Content = L("ToolPwdGenSylCvc");
        SylCvcHint.Text = L("ToolPwdGenSylCvcHint");
        SylDigitsLabel.Text = L("ToolPwdGenDigits");
        SylSpecialsLabel.Text = L("ToolPwdGenSymbols");
        SylPlacementLabel.Text = L("ToolPwdGenPlacement");
        SylStructureLabel.Text = L("ToolPwdGenSylStructure");

        // Passphrase mode
        PpWordCountLabel.Text = L("ToolPwdGenWordCount");
        PpSeparatorLabel.Text = L("ToolPwdGenSeparator");
        PpLanguageLabel.Text = L("ToolPwdGenLanguage");
        ChkPpCapitalize.Content = L("ToolPwdGenCapitalize");
        ChkPpDigit.Content = L("ToolPwdGenAddDigit");
        ChkPpSpecial.Content = L("ToolPwdGenAddSpecial");
        PpPlacementLabel.Text = L("ToolPwdGenPlacement");

        // Accessibility
        System.Windows.Automation.AutomationProperties.SetName(BtnRegenerate, L("ToolPwdGenBtnRegenerate"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopy, L("ToolPwdGenBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(PasswordOutput, L("ToolPwdGenTitle"));
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
        System.Windows.Automation.AutomationProperties.SetName(SylLengthSlider, L("ToolPwdGenBaseLength"));
        System.Windows.Automation.AutomationProperties.SetName(CmbSylCase, L("ToolPwdGenCase"));
        System.Windows.Automation.AutomationProperties.SetName(SylDigitsSlider, L("ToolPwdGenDigits"));
        System.Windows.Automation.AutomationProperties.SetName(SylSpecialsSlider, L("ToolPwdGenSymbols"));
        System.Windows.Automation.AutomationProperties.SetName(TxtSylSeparator, L("ToolPwdGenSeparator"));
        System.Windows.Automation.AutomationProperties.SetName(ChkSylCvc, L("ToolPwdGenSylCvc"));
        System.Windows.Automation.AutomationProperties.SetName(PpWordCountSlider, L("ToolPwdGenWordCount"));
        System.Windows.Automation.AutomationProperties.SetName(TxtPpSeparator, L("ToolPwdGenSeparator"));
        System.Windows.Automation.AutomationProperties.SetName(CmbPpLanguage, L("ToolPwdGenLanguage"));
        System.Windows.Automation.AutomationProperties.SetName(ChkPpCapitalize, L("ToolPwdGenCapitalize"));
        System.Windows.Automation.AutomationProperties.SetName(ChkPpDigit, L("ToolPwdGenAddDigit"));
        System.Windows.Automation.AutomationProperties.SetName(ChkPpSpecial, L("ToolPwdGenAddSpecial"));
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
        System.Windows.Automation.AutomationProperties.SetName(BtnSavePreset, L("TooltipSavePreset"));
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

        // Strength bar accessibility
        System.Windows.Automation.AutomationProperties.SetName(StrengthBar, L("ToolPwdGenStrengthStrong"));
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(PasswordGeneratorViewModel.Length), StringComparison.Ordinal))
        {
            UpdateQuickLengthHighlight();
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

    private void UpdateModeDescription()
    {
        ModeDescription.Text = _vm.CurrentMode switch
        {
            PasswordGeneratorViewModel.GeneratorMode.Random => L("ToolPwdGenModeRandomDesc"),
            PasswordGeneratorViewModel.GeneratorMode.Syllable => L("ToolPwdGenModeSyllableDesc"),
            PasswordGeneratorViewModel.GeneratorMode.Passphrase => L("ToolPwdGenModePassphraseDesc"),
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
        ApplyPresetAndUpdateView(() => _vm.ApplySyllablePreset(12, 3, 1, 0, "-"));

    private void OnPresetSylBalanced(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplySyllablePreset(16, 0, 2, 1, "-", true));

    private void OnPresetSylStrong(object sender, RoutedEventArgs e) =>
        ApplyPresetAndUpdateView(() => _vm.ApplySyllablePreset(24, 0, 3, 2, "", true));

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

    private void RebuildCustomPresetButtons()
    {
        PanelCustomPresets.Children.Clear();
        if (!_viewInitialized) return;

        var filtered = _vm.GetCustomPresetsForCurrentMode();
        if (filtered.Count == 0) return;

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

    private void ShowClipboardClearHint()
    {
        if (!_vm.ClipboardAutoClear) return;

        var originalText = L("ToolPwdGenKeyboardHint");
        KeyboardHintText.Text = L("ToolPwdGenClipboardClearHint");

        var revertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        revertTimer.Tick += (_, _) =>
        {
            revertTimer.Stop();
            KeyboardHintText.Text = originalText;
        };
        revertTimer.Start();
    }

    private void StartClipboardClearTimer(string password)
    {
        if (!_vm.ClipboardAutoClear) return;

        _lastCopiedPassword = password;
        _clipboardClearTimer?.Stop();
        _clipboardClearTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(ClipboardClearDelaySeconds)
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
