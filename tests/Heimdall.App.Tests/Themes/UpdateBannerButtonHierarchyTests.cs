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

namespace Heimdall.App.Tests.Themes;

/// <summary>
/// Guards the visual weight assigned to each update-banner button, and the order the
/// row lays them out in. The banner mixes actions of very different consequence:
/// installing, opening a release page, hiding the banner, and persisting a skipped
/// version that no UI can undo. Collapsing them back onto a single style, or shuffling
/// them so the layout contradicts the hierarchy, are both silent regressions, so they
/// are asserted here rather than left to a visual pass.
/// </summary>
public sealed class UpdateBannerButtonHierarchyTests
{
    private static readonly XNamespace PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// The nominal row, heaviest first. Declaration order also drives keyboard tab
    /// order here, since none of these buttons carries an explicit TabIndex.
    /// </summary>
    private static readonly string[] NominalRowOrder =
    [
        "Mw_UpdateBannerDownloadInstall",
        "Mw_UpdateBannerLater",
        "Mw_UpdateBannerViewRelease",
        "Mw_UpdateBannerSkip"
    ];

    [Fact]
    public void UpdateBanner_KeepsThreeDistinctButtonWeightsInDecreasingOrder()
    {
        XDocument mainWindow = LoadXaml("src", "Heimdall.App", "MainWindow.xaml");

        AssertButtonStyle(mainWindow, "Mw_UpdateBannerSkip", "QuietButtonStyle");
        AssertButtonStyle(mainWindow, "Mw_UpdateBannerViewRelease", "QuietButtonStyle");

        // Without the next two the test would still pass if every button went quiet,
        // which is the opposite of the intended hierarchy.
        AssertButtonStyle(mainWindow, "Mw_UpdateBannerDownloadInstall", "PrimaryButtonStyle");
        AssertButtonStyle(mainWindow, "Mw_UpdateBannerLater", "SecondaryButtonStyle");

        AssertNominalRowOrder(mainWindow);

        XDocument commonControls = LoadXaml("src", "Heimdall.App", "Themes", "CommonControls.xaml");
        XElement? quietStyle = FindStyle(commonControls, "QuietButtonStyle");
        Assert.True(
            quietStyle is not null,
            "CommonControls.xaml must declare a Style with x:Key 'QuietButtonStyle'.");

        string? basedOnKey = ExtractResourceKey((string?)quietStyle!.Attribute("BasedOn"));
        Assert.True(
            string.Equals(basedOnKey, "ToolbarGhostButtonStyle", StringComparison.Ordinal),
            $"Style 'QuietButtonStyle' must derive from 'ToolbarGhostButtonStyle' so the ghost "
            + $"template and its disabled-state opacity are inherited, but BasedOn references "
            + $"'{basedOnKey ?? "<none>"}'.");
    }

    /// <summary>
    /// Asserts the four nominal buttons appear in decreasing visual weight. Only their
    /// relative order is checked: any other element added to the banner, such as the
    /// install-only cancel button, is ignored rather than shifting an index.
    /// </summary>
    private static void AssertNominalRowOrder(XDocument document)
    {
        string[] actualOrder = document.Descendants(PresentationNamespace + "Button")
            .Select(element => (string?)element.Attribute(XamlNamespace + "Name"))
            .Where(name => name is not null && NominalRowOrder.Contains(name, StringComparer.Ordinal))
            .Select(name => name!)
            .ToArray();

        Assert.True(
            NominalRowOrder.SequenceEqual(actualOrder, StringComparer.Ordinal),
            $"The update banner must lay its buttons out in decreasing visual weight, "
            + $"'{string.Join(" -> ", NominalRowOrder)}', but MainWindow.xaml declares "
            + $"'{string.Join(" -> ", actualOrder)}'. Declaration order is also the keyboard "
            + "tab order, so a shuffled row contradicts the hierarchy twice over.");
    }

    private static void AssertButtonStyle(XDocument document, string elementName, string expectedStyleKey)
    {
        XElement? button = document.Descendants(PresentationNamespace + "Button")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute(XamlNamespace + "Name"), elementName, StringComparison.Ordinal));

        Assert.True(
            button is not null,
            $"Button '{elementName}' was not found in MainWindow.xaml.");

        string? actualStyleKey = ExtractResourceKey((string?)button!.Attribute("Style"));
        Assert.True(
            string.Equals(actualStyleKey, expectedStyleKey, StringComparison.Ordinal),
            $"Button '{elementName}' must reference style '{expectedStyleKey}' but references "
            + $"'{actualStyleKey ?? "<none>"}'.");
    }

    /// <summary>
    /// Reads the resource key out of a "{DynamicResource Key}" or "{StaticResource Key}"
    /// markup extension. Returns null for any other shape so a malformed reference fails
    /// the assertion instead of silently matching.
    /// </summary>
    private static string? ExtractResourceKey(string? markupExtension)
    {
        if (string.IsNullOrWhiteSpace(markupExtension))
        {
            return null;
        }

        string trimmed = markupExtension.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            return null;
        }

        string[] parts = trimmed[1..^1]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            return null;
        }

        bool isResourceReference =
            string.Equals(parts[0], "DynamicResource", StringComparison.Ordinal)
            || string.Equals(parts[0], "StaticResource", StringComparison.Ordinal);

        return isResourceReference ? parts[1] : null;
    }

    private static XDocument LoadXaml(params string[] relativeSegments)
    {
        string[] pathSegments = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))
        }.Concat(relativeSegments).ToArray();
        string path = Path.Combine(pathSegments);

        Assert.True(File.Exists(path), $"Missing XAML file: {path}");
        return XDocument.Load(path);
    }

    private static XElement? FindStyle(XDocument document, string styleKey)
    {
        return document.Descendants(PresentationNamespace + "Style")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute(XamlNamespace + "Key"), styleKey, StringComparison.Ordinal));
    }
}
