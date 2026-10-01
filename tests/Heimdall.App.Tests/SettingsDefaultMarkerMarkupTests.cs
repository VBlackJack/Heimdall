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
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// Every marked setting has its marker on the Settings tab, beside the field that edits it.
/// </summary>
/// <remarks>
/// The view model decides which settings carry a marker; the markup has to place one for each, or a
/// setting the user changed shows nothing. A marker bound to a name the view model does not know
/// would be a silent binding error, so the two sets are compared both ways.
/// </remarks>
public sealed class SettingsDefaultMarkerMarkupTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string MarkerElement = "SettingDefaultMarker";

    private const string MarkerBindingPrefix = "{Binding Settings.Defaults[";

    private const string SettingsBindingPrefix = "{Binding Settings.";

    /// <summary>The bindings that edit the credential provider type: it is two radio buttons.</summary>
    private static readonly Dictionary<string, string[]> EditedThrough = new(StringComparer.Ordinal)
    {
        [nameof(SettingsViewModel.CredentialProviderType)] =
        [
            nameof(SettingsViewModel.IsCommandProvider),
            nameof(SettingsViewModel.IsWindowsCredentialManagerProvider),
        ],
    };

    [Fact]
    public void EveryMarkedSettingHasExactlyOneMarkerBesideItsField()
    {
        List<string> problems = Inspect(SettingsRoot(), SettingsViewModel.DefaultMarkerSettings);

        Assert.True(SettingsViewModel.DefaultMarkerSettings.Count >= 90, "the marked settings were not read");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>The positive control: each shape the check refuses is reported.</summary>
    [Fact]
    public void TheCheckRefusesAMissingAnUnknownADuplicateAndAMisplacedMarker()
    {
        XElement sample = XElement.Parse(
            """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:controls="clr-namespace:Heimdall.App.Controls">
                <CheckBox IsChecked="{Binding Settings.A}"/>
                <controls:SettingDefaultMarker DataContext="{Binding Settings.Defaults[A]}"/>
                <controls:SettingDefaultMarker DataContext="{Binding Settings.Defaults[A]}"/>
                <CheckBox IsChecked="{Binding Settings.C}"/>
                <controls:SettingDefaultMarker DataContext="{Binding Settings.Defaults[D]}"/>
                <TextBox Text="{Binding Settings.ETextOther}"/>
                <controls:SettingDefaultMarker DataContext="{Binding Settings.Defaults[E]}"/>
            </StackPanel>
            """);

        List<string> problems = Inspect(sample, ["A", "B", "E"]);

        Assert.Contains(problems, problem => problem.StartsWith("A: 2 markers", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("B: no marker", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("D: a marker for a setting", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("E: not beside", StringComparison.Ordinal));
        Assert.Equal(5, problems.Count);
    }

    private static List<string> Inspect(XElement root, IReadOnlyCollection<string> marked)
    {
        List<string> problems = [];
        Dictionary<string, List<XElement>> markers = root.Descendants()
            .Where(element => element.Name.LocalName == MarkerElement)
            .GroupBy(MarkerKey)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (string setting in marked)
        {
            if (!markers.TryGetValue(setting, out List<XElement>? found))
            {
                problems.Add($"{setting}: no marker");
                continue;
            }

            if (found.Count != 1)
            {
                problems.Add($"{setting}: {found.Count} markers");
            }

            foreach (XElement marker in found)
            {
                XElement? field = marker.ElementsBeforeSelf().LastOrDefault();
                if (field is null || !EditsSetting(field, setting))
                {
                    problems.Add($"{setting}: not beside the field that edits it (line {Line(marker)})");
                }
            }
        }

        foreach (string unknown in markers.Keys.Where(key => !marked.Contains(key)))
        {
            problems.Add($"{unknown}: a marker for a setting that carries none");
        }

        return problems;
    }

    /// <summary>
    /// Whether the element just before a marker, or something inside it, is bound to the setting:
    /// directly, through the text of its number box, or through the controls that stand for it.
    /// </summary>
    private static bool EditsSetting(XElement field, string setting)
    {
        string[] paths = EditedThrough.TryGetValue(setting, out string[]? through)
            ? through
            : [setting, setting + "Text"];

        return field.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Select(attribute => BoundPath(attribute.Value))
            .Any(path => path is not null && paths.Contains(path, StringComparer.Ordinal));
    }

    /// <summary>The settings property a binding expression names, or null when it names none.</summary>
    private static string? BoundPath(string value)
    {
        if (!value.StartsWith(SettingsBindingPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string rest = value[SettingsBindingPrefix.Length..];
        int end = rest.IndexOfAny([',', '}', ' ']);
        return end < 0 ? rest : rest[..end];
    }

    private static string MarkerKey(XElement marker)
    {
        string value = marker.Attribute("DataContext")?.Value ?? string.Empty;
        if (!value.StartsWith(MarkerBindingPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        string rest = value[MarkerBindingPrefix.Length..];
        int end = rest.IndexOf(']', StringComparison.Ordinal);
        return end < 0 ? string.Empty : rest[..end];
    }

    private static int Line(XElement element) => ((System.Xml.IXmlLineInfo)element).LineNumber;

    private static XElement SettingsRoot()
    {
        string path = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml");
        Assert.True(File.Exists(path), $"View not found: {path}");
        return XDocument.Load(path, LoadOptions.SetLineInfo)
            .Descendants()
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "Mw_SettingsRoot");
    }
}
