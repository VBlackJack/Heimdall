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
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Heimdall.App.Tests;

/// <summary>
/// A settings number shows its unit once, from a locale key.
/// </summary>
/// <remarks>
/// A live pass read "Tunnel establishment delay (ms)" above a box followed by "ms": the unit twice,
/// and the suffix a literal no translation could reach. Some fields carried the unit in the label,
/// some in a suffix, some in both, and the update interval said "(hours)" where its neighbours
/// said "s".
/// </remarks>
public sealed class SettingsUnitDisplayGuardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Regex UnitInLabel = new(
        @"\((?:ms|s|px|h|min|hours?|seconds?|minutes?|heures?|secondes?|horas?|segundos?|minutos?)(?:\)|,)",
        RegexOptions.CultureInvariant);

    private static readonly Regex UnitLiteral = new(@"^(?:ms|s|px|h|min)$", RegexOptions.CultureInvariant);

    [Fact]
    public void EverySettingsUnitIsLocalizedAndShownOnce()
    {
        XDocument markup = XDocument.Load(
            Path.Combine(SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XElement root = markup.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == "Mw_SettingsRoot");
        Dictionary<string, Dictionary<string, string>> locales = new(StringComparer.Ordinal)
        {
            ["en"] = ReadLocale("en"),
            ["fr"] = ReadLocale("fr"),
            ["es"] = ReadLocale("es"),
        };

        (List<string> problems, int suffixed) = Inspect(root, locales);

        Assert.True(suffixed >= 18, $"only {suffixed} unit suffixes were found; the scan is not reading the settings");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheCheckRefusesALiteralUnitAndAUnitRepeatedInTheLabel()
    {
        XElement sample = XElement.Parse(
            """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:loc="clr-namespace:Heimdall.App.Localization">
                <TextBlock x:Name="Label" Text="{loc:Translate DelayLabel}"/>
                <StackPanel Orientation="Horizontal">
                    <TextBox AutomationProperties.LabeledBy="{x:Reference Label}"/>
                    <TextBlock Text="{loc:Translate SettingsUnitMilliseconds}"/>
                </StackPanel>
                <TextBlock Text="ms"/>
            </Grid>
            """);
        Dictionary<string, Dictionary<string, string>> locales = new(StringComparer.Ordinal)
        {
            ["en"] = new(StringComparer.Ordinal) { ["DelayLabel"] = "Delay (ms)" },
        };

        (List<string> problems, int suffixed) = Inspect(sample, locales);

        Assert.Equal(1, suffixed);
        Assert.Equal(2, problems.Count);
    }

    private static (List<string> Problems, int Suffixed) Inspect(
        XElement root,
        Dictionary<string, Dictionary<string, string>> locales)
    {
        List<string> problems = [];
        int suffixed = 0;
        Dictionary<string, XElement> byName = root.DescendantsAndSelf()
            .Where(element => element.Attribute(Xaml + "Name") is not null)
            .ToDictionary(element => element.Attribute(Xaml + "Name")!.Value, StringComparer.Ordinal);

        foreach (XElement block in root.Descendants().Where(element => element.Name.LocalName == "TextBlock"))
        {
            string text = block.Attribute("Text")?.Value ?? string.Empty;
            if (UnitLiteral.IsMatch(text))
            {
                problems.Add($"a unit is written as the literal \"{text}\" instead of a SettingsUnit key");
            }
        }

        foreach (XElement panel in root.Descendants().Where(element => element.Name.LocalName == "StackPanel"))
        {
            XElement? unit = panel.Elements().FirstOrDefault(child =>
                child.Name.LocalName == "TextBlock"
                && (child.Attribute("Text")?.Value ?? string.Empty).StartsWith("{loc:Translate SettingsUnit", StringComparison.Ordinal));
            XElement? box = panel.Elements().FirstOrDefault(child => child.Name.LocalName == "TextBox");
            if (unit is null || box is null)
            {
                continue;
            }

            suffixed++;
            string labelledBy = box.Attribute("AutomationProperties.LabeledBy")?.Value ?? string.Empty;
            Match reference = Regex.Match(labelledBy, @"x:Reference\s+(\w+)");
            if (!reference.Success || !byName.TryGetValue(reference.Groups[1].Value, out XElement? label))
            {
                problems.Add($"a box with a unit suffix has no label to check ({labelledBy})");
                continue;
            }

            Match key = Regex.Match(label.Attribute("Text")?.Value ?? string.Empty, @"loc:Translate\s+(\w+)");
            if (!key.Success)
            {
                continue;
            }

            foreach ((string locale, Dictionary<string, string> values) in locales)
            {
                if (values.TryGetValue(key.Groups[1].Value, out string? value) && UnitInLabel.IsMatch(value))
                {
                    problems.Add($"{locale} {key.Groups[1].Value} = \"{value}\" repeats the unit its suffix shows");
                }
            }
        }

        return (problems, suffixed);
    }

    private static Dictionary<string, string> ReadLocale(string locale)
    {
        string path = Path.Combine(SettingsNumericFields.FindRepoRoot(), "locales", locale + ".json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }
}
