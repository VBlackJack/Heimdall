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
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels.Tools;

namespace Heimdall.App.Tests;

/// <summary>
/// A combo box with a fixed width cut its selected value: "Automatic (from the protocol)" did not
/// fit the 200 px of the Remote OS box in the Edit Session dialog, and the French label is longer.
/// A combo box now grows from a minimum width, unless its items were measured to fit.
/// </summary>
/// <remarks>
/// The measurement is the one WPF performs: <see cref="FormattedText"/> in the system message
/// font at the body size the themed combo box style sets, plus the horizontal margins of the
/// selection box in that style's template, read from the template itself.
/// </remarks>
public sealed class ComboBoxWidthGuardTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] MeasuredLanguages = ["en", "fr", "es"];

    /// <summary>FontSizeBody, which ThemedComboBoxStyle applies to every combo box.</summary>
    private const double BodyFontSize = 13;

    /// <summary>Combo boxes found under src/Heimdall.App on 2026-10-21: 101. A floor for the walk.</summary>
    private const int MinimumComboBoxCount = 80;

    /// <summary>
    /// The combo boxes that keep a fixed width, each with the texts it can show. Every one of
    /// them was measured to fit; the theory below measures them again on every run.
    /// </summary>
    private static readonly Dictionary<string, Func<JsonElement, IEnumerable<string>>> MeasuredExceptions =
        new(StringComparer.Ordinal)
        {
            ["MainWindow.xaml|Mw_ColorSchemeCombo"] = Keys(
                "TerminalSchemeDefault", "TerminalSchemeDracula", "TerminalSchemeSolarizedDark",
                "TerminalSchemeMonokai", "TerminalSchemeNord"),
            ["MainWindow.xaml|Mw_SettingsPsExecutionPolicy"] = Keys(
                "PsPolicyDefault", "PsPolicyBypass", "PsPolicyRemoteSigned", "PsPolicyUnrestricted",
                "PsPolicyAllSigned"),
            ["MainWindow.xaml|{Binding Settings.SshDefaultMode}"] = Keys(
                "SettingsSshModeEmbedded", "SettingsSshModeExternal"),
            ["MainWindow.xaml|{Binding Settings.RdpDefaultMode}"] = Keys(
                "SettingsRdpModeEmbedded", "SettingsRdpModeExternal"),
            ["MainWindow.xaml|{Binding Settings.RdpDefaultColorDepth}"] = Keys(
                "SettingsColorDepth16", "SettingsColorDepth24", "SettingsColorDepth32"),
            ["MainWindow.xaml|{Binding Settings.RdpDefaultAudioMode}"] = Keys(
                "ServerDialogAudioDisabled", "ServerDialogAudioLocal", "ServerDialogAudioRemote"),
            ["SecNumCloudAuditView.xaml|CmbDepth"] = Keys(
                "ToolAuditDepthQuick", "ToolAuditDepthStandard", "ToolAuditDepthDeep"),
            ["PasswordAuditView.xaml|CmbPolicy"] = Keys(
                "ToolPwdAuditPolicyNist", "ToolPwdAuditPolicyAnssi", "ToolPwdAuditPolicyCustom"),
            ["PasswordGeneratorView.xaml|CmbClipboardDelay"] = locale =>
                PasswordGeneratorViewModel.ClipboardClearChoices.Select(
                    seconds => $"{seconds} {Text(locale, "ToolPwdGenSeconds")}"),
            ["PasswordGeneratorView.xaml|CmbSylPlacement"] = Placements(),
            ["PasswordGeneratorView.xaml|CmbPpPlacement"] = Placements(),
            ["PasswordGeneratorView.xaml|CmbLeetPlacement"] = Placements(),
            ["PasswordGeneratorView.xaml|CmbPpLanguage"] = Languages(),
            ["PasswordGeneratorView.xaml|CmbLeetLanguage"] = Languages(),
        };

    public static TheoryData<string> MeasuredExceptionIds => new(MeasuredExceptions.Keys);

    [Fact]
    public void TheRemoteOsCombo_GrowsFromAMinimumWidth()
    {
        XElement combo = AllComboBoxes()
            .Single(entry => entry.Identity == "ServerDialog.xaml|DlgSrv_RemoteOsCombo")
            .Element;

        Assert.Null(combo.Attribute("Width"));
        Assert.NotNull(combo.Attribute("MinWidth"));
    }

    [Fact]
    public void EveryFixedWidthComboBox_IsAMeasuredException()
    {
        var all = AllComboBoxes().ToList();
        Assert.True(
            all.Count >= MinimumComboBoxCount,
            $"Only {all.Count} combo box(es) found under src/Heimdall.App; the walk is broken.");

        List<string> unmeasured = all
            .Where(entry => entry.Element.Attribute("Width") is not null)
            .Select(entry => entry.Identity)
            .Where(identity => !MeasuredExceptions.ContainsKey(identity))
            .ToList();

        Assert.True(
            unmeasured.Count == 0,
            "These combo boxes keep a fixed Width without a measurement. Use MinWidth so the "
            + "selected value is never cut, or measure the items and add an entry to "
            + nameof(MeasuredExceptions) + ": " + string.Join(", ", unmeasured));
    }

    [Theory]
    [MemberData(nameof(MeasuredExceptionIds))]
    public void AMeasuredException_FitsItsLongestItem(string identity)
    {
        XElement combo = AllComboBoxes().Single(entry => entry.Identity == identity).Element;
        string? declared = (string?)combo.Attribute("Width");
        Assert.True(declared is not null, $"{identity} no longer has a fixed Width; drop its entry.");
        double width = double.Parse(declared, CultureInfo.InvariantCulture);
        double chrome = SelectionBoxHorizontalMargin();

        foreach (string language in MeasuredLanguages)
        {
            using JsonDocument locale = ReadLocale(language);
            foreach (string text in MeasuredExceptions[identity](locale.RootElement))
            {
                double needed = Measure(text) + chrome;
                Assert.True(
                    needed <= width,
                    $"{identity}: '{text}' ({language}) needs {needed:F1} px but the box is "
                    + $"{width} px wide. Replace Width by MinWidth.");
            }
        }
    }

    private static Func<JsonElement, IEnumerable<string>> Keys(params string[] keys)
        => locale => keys.Select(key => Text(locale, key));

    private static Func<JsonElement, IEnumerable<string>> Placements() => Keys(
        "ToolPwdGenPlacementRandom", "ToolPwdGenPlacementStart", "ToolPwdGenPlacementEnd",
        "ToolPwdGenPlacementMiddle", "ToolPwdGenPlacementPositions");

    private static Func<JsonElement, IEnumerable<string>> Languages() => Keys(
        "ToolPwdGenLangEnglish", "ToolPwdGenLangFrench", "ToolPwdGenLangSpanish", "ToolPwdGenLangLatin");

    private static string Text(JsonElement locale, string key)
    {
        Assert.True(locale.TryGetProperty(key, out JsonElement value), $"Locale key '{key}' is missing.");
        return value.GetString() ?? string.Empty;
    }

    private static double Measure(string text)
    {
        FormattedText formatted = new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            BodyFontSize,
            Brushes.Black,
            pixelsPerDip: 1.0);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>Left plus right margin of the selection box in ThemedComboBoxStyle.</summary>
    private static double SelectionBoxHorizontalMargin()
    {
        string path = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml");
        XElement selectionBox = XDocument.Load(path)
            .Descendants(Presentation + "ContentPresenter")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "SelectionBox");
        string margin = (string?)selectionBox.Attribute("Margin") ?? string.Empty;
        Thickness thickness = (Thickness)new ThicknessConverter().ConvertFromInvariantString(margin)!;

        Assert.True(thickness.Left + thickness.Right > 0, "The selection box margin was not read.");
        return thickness.Left + thickness.Right;
    }

    private static JsonDocument ReadLocale(string language)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "locales", language + ".json")));

    private static IEnumerable<(string Identity, XElement Element)> AllComboBoxes()
    {
        string appRoot = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
        foreach (string file in Directory.GetFiles(appRoot, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (XElement combo in XDocument.Load(file).Descendants(Presentation + "ComboBox"))
            {
                string identity = (string?)combo.Attribute(Xaml + "Name")
                    ?? (string?)combo.Attribute("SelectedValue")
                    ?? (string?)combo.Attribute("ItemsSource")
                    ?? string.Empty;
                yield return (Path.GetFileName(file) + "|" + identity, combo);
            }
        }
    }
}
