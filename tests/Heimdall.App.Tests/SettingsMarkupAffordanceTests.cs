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

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// The controls of the General settings tab, measured against what they claim to offer.
/// </summary>
/// <remarks>
/// Each of these is a statement the screen makes and no other layer can contradict: a field that
/// accepts input which cannot take effect, a button that can never be pressed and never says why,
/// a list that hands out an internal identifier, and a panel with no way back from an edit. None
/// of them fails a binding or a view-model oracle, so the markup is the only place they show.
/// </remarks>
public sealed class SettingsMarkupAffordanceTests
{
    /// <summary>
    /// The update interval is dimmed while automatic checking is off.
    /// </summary>
    /// <remarks>
    /// The field stayed editable and validated after the checkbox above it was cleared, so it went
    /// on presenting itself as operative while nothing read it. Two other cards in this same file
    /// already gate their dependent field on the checkbox that owns it; this one was the outlier.
    /// </remarks>
    [Fact]
    public void TheUpdateIntervalFieldIsGatedOnAutomaticUpdateChecking()
    {
        string field = DependentFieldBlock("x:Name=\"Mw_SettingsUpdateIntervalLabel\"");

        Assert.Contains("IsEnabled=\"{Binding Settings.UpdateCheckEnabled}\"", field);
    }

    /// <summary>
    /// The language list offers language names, and still persists the locale codes.
    /// </summary>
    /// <remarks>
    /// The two items carried the literal codes as their content, which was both the one bare
    /// user-facing string left in this card and the value the setting was persisted as. Moving the
    /// codes to Tag is what lets the visible text be translated without writing a language name
    /// into the settings file, so the two halves are asserted together.
    /// </remarks>
    [Fact]
    public void TheLanguageListNamesItsLanguagesAndPersistsItsCodes()
    {
        string combo = MainWindowMarkup.Block(
            "<ComboBox SelectedValue=\"{Binding Settings.DefaultLocale}\"",
            "</ComboBox>");

        Assert.Contains("SelectedValuePath=\"Tag\"", combo);

        List<string> items =
        [
            .. Regex.Matches(combo, "<ComboBoxItem\\b[^>]*/>").Select(match => match.Value)
        ];

        Assert.True(
            items.Count >= 2,
            $"only {items.Count} language items were found, so this compared nothing");

        List<string> bare =
        [
            .. items.Where(item =>
                !Regex.IsMatch(item, "\\bContent=\"\\{loc:Translate [A-Za-z0-9_]+\\}\""))
        ];

        Assert.True(
            bare.Count == 0,
            "the language list still hands the user a literal instead of a translated name:\n"
                + string.Join("\n", bare));

        HashSet<string> codes = items
            .Select(item => Regex.Match(item, "\\bTag=\"([A-Za-z0-9_-]+)\"").Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(new HashSet<string>(["en", "fr"], StringComparer.Ordinal), codes);
    }

    /// <summary>
    /// The legacy migration card states the condition its button waits on.
    /// </summary>
    /// <remarks>
    /// The button is uncommandable on any profile that never declined a migration offer, which is
    /// most of them, and the card's description says when the offer would reappear rather than
    /// what makes the button pressable. Left silent, a permanently greyed control on the
    /// most-visited settings tab reads as a broken feature.
    /// </remarks>
    [Fact]
    public void TheLegacyMigrationCardExplainsItsDisabledButton()
    {
        string card = MainWindowMarkup.Block(
            "<TextBlock Text=\"{loc:Translate SettingsSectionLegacyMigration}\"",
            "</StackPanel>");

        Assert.Contains("Settings.ReofferLegacyMigrationNextStartupCommand", card);

        List<string> explanations =
        [
            .. Regex.Matches(card, "<TextBlock\\b[^>]*/>")
                .Select(match => match.Value)
                .Where(element =>
                    Regex.IsMatch(element, "\\bText=\"\\{loc:Translate [A-Za-z0-9_]+\\}\"")
                    && element.Contains(
                        "Settings.LegacyMigrationReofferAvailable",
                        StringComparison.Ordinal)
                    && element.Contains("ConverterParameter=Invert", StringComparison.Ordinal))
        ];

        Assert.True(
            explanations.Count == 1,
            $"{explanations.Count} lines in the legacy migration card are shown exactly when its "
                + "button cannot be pressed. Expected one: without it the card offers a control "
                + "that can never be used and never says what it is waiting for.");
    }

    /// <summary>
    /// The settings action bar offers a way back from an edit, and it cannot be mistaken for the
    /// factory reset standing beside it.
    /// </summary>
    /// <remarks>
    /// The bar held Save and Reset Defaults only, so the one control that looked like a way back
    /// was the one that loads the factory values over all six tabs. Adding a second quiet button
    /// beside it is only an improvement while the two do not read alike, which is why the styles
    /// are asserted to differ rather than merely to exist.
    /// </remarks>
    [Fact]
    public void TheSettingsActionBarOffersARevertDistinctFromTheFactoryReset()
    {
        string actionBar = MainWindowMarkup.Block(
            "<Button x:Name=\"Mw_SettingsSaveBtn\"",
            "</WrapPanel>");

        string revert = ButtonBound(actionBar, "Settings.RevertChangesCommand");
        string reset = ButtonBound(actionBar, "Settings.ResetToDefaultsCommand");

        Assert.NotEqual(StyleOf(reset), StyleOf(revert));
    }

    /// <summary>The button in <paramref name="markup"/> bound to <paramref name="command"/>.</summary>
    private static string ButtonBound(string markup, string command)
    {
        Match button = Regex.Match(
            markup,
            "<Button\\b[^>]*Command=\"\\{Binding " + Regex.Escape(command) + "\\}\"[^>]*/>");

        Assert.True(
            button.Success,
            $"the settings action bar has no button bound to {command}");

        return button.Value;
    }

    private static string StyleOf(string button)
    {
        Match style = Regex.Match(button, "Style=\"\\{DynamicResource ([A-Za-z0-9_]+)\\}\"");
        Assert.True(style.Success, $"this button carries no style at all:\n{button}");
        return style.Groups[1].Value;
    }

    /// <summary>
    /// The markup from the panel wrapping <paramref name="labelAnchor"/> down to the end of the
    /// field it labels, which is where a dependent control is gated on its master checkbox.
    /// </summary>
    private static string DependentFieldBlock(string labelAnchor)
    {
        string markup = MainWindowMarkup.Text();

        int label = markup.IndexOf(labelAnchor, StringComparison.Ordinal);
        Assert.True(label >= 0, $"the markup no longer contains \"{labelAnchor}\"");

        int panel = markup.LastIndexOf("<StackPanel", label, StringComparison.Ordinal);
        Assert.True(panel >= 0, $"\"{labelAnchor}\" is no longer inside a panel");

        int box = markup.IndexOf("<TextBox", label, StringComparison.Ordinal);
        Assert.True(box > label, $"\"{labelAnchor}\" no longer labels a field");

        int end = markup.IndexOf('>', box);
        Assert.True(end > box, "the field's opening tag is unterminated");

        return markup[panel..(end + 1)];
    }
}
