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
using System.Xml;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Pointer targets of at least 24 by 24 pixels (WCAG 2.2, 2.5.8). The tab close button, the
/// palette and command library row actions and the command action delete buttons were drawn
/// 20 or 22 pixels wide, and the text buttons of the session headers about 20 pixels high.
/// </summary>
public sealed class PointerTargetSizeGuardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ButtonTags = ["Button", "ToggleButton", "RepeatButton"];

    /// <summary>The session surfaces whose header text buttons are compact.</summary>
    public static TheoryData<string> SessionViews { get; } = new()
    {
        "EmbeddedRdpView.xaml",
        "EmbeddedSshView.xaml",
        "EmbeddedVncView.xaml",
        "EmbeddedCitrixView.xaml",
        "EmbeddedEditorView.xaml",
    };

    [Fact]
    public void TheTargetTokensAreAtLeastTheMinimum()
    {
        Assert.True(Token("TargetMinSize") >= 24);
        Assert.True(Token("SessionHeaderMinTarget") >= 24);
    }

    [Fact]
    public void NoButtonIsDrawnSmallerThanTheMinimumTarget()
    {
        double minimum = Token("TargetMinSize");
        string appRoot = AppPath(string.Empty);
        List<string> small = [];
        bool reachedTools = false;

        foreach (string file in SourceFileEnumeration.EnumerateFiles(appRoot, "*.xaml"))
        {
            reachedTools |= file.Contains(Path.Combine("Views", "Tools"), StringComparison.OrdinalIgnoreCase);
            foreach (XElement button in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants()
                .Where(e => ButtonTags.Contains(e.Name.LocalName)))
            {
                foreach (string dimension in new[] { "Width", "Height" })
                {
                    if (double.TryParse((string?)button.Attribute(dimension), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                        && value < minimum)
                    {
                        small.Add($"{Path.GetRelativePath(appRoot, file)}:{((IXmlLineInfo)button).LineNumber} {dimension}={value}");
                    }
                }
            }
        }

        Assert.True(reachedTools, "the scan never opened Views/Tools");
        Assert.True(small.Count == 0, "Buttons under the minimum target:" + Environment.NewLine + string.Join(Environment.NewLine, small));
    }

    /// <summary>
    /// A text button with two pixels of vertical padding is about twenty pixels high: the Primary
    /// and Secondary styles set no minimum, unlike Ghost, so each one carries the header minimum.
    /// </summary>
    [Theory]
    [MemberData(nameof(SessionViews))]
    public void ACompactHeaderTextButtonCarriesTheHeaderMinimum(string view)
    {
        List<XElement> compact = XDocument.Load(AppPath(Path.Combine("Views", view))).Descendants()
            .Where(e => ButtonTags.Contains(e.Name.LocalName)
                && IsTextButtonStyle((string?)e.Attribute("Style"))
                && IsCompact((string?)e.Attribute("Padding"))
                && e.Attribute("Height") is null)
            .ToList();

        Assert.NotEmpty(compact);
        Assert.All(compact, button => Assert.Equal(
            "{StaticResource SessionHeaderMinTarget}",
            (string?)button.Attribute("MinHeight")));
    }

    [Fact]
    public void ASessionPropertyValueIsATargetOfTheMinimumHeight()
    {
        XElement style = Assert.Single(
            XDocument.Load(AppPath(Path.Combine("Controls", "SessionPropertiesView.xaml"))).Descendants(),
            e => e.Name.LocalName == "Style" && (string?)e.Attribute(Xaml + "Key") == "SpvCopyValueStyle");

        Assert.Contains(
            style.Elements(),
            setter => (string?)setter.Attribute("Property") == "MinHeight"
                && (string?)setter.Attribute("Value") == "{StaticResource TargetMinSize}");
    }

    private static bool IsTextButtonStyle(string? style) =>
        style is "{DynamicResource PrimaryButtonStyle}" or "{DynamicResource SecondaryButtonStyle}";

    private static bool IsCompact(string? padding)
    {
        string[] parts = (padding ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int vertical)
            && vertical <= 3;
    }

    private static double Token(string key)
    {
        XElement token = Assert.Single(
            XDocument.Load(AppPath(Path.Combine("Themes", "CommonControls.xaml"))).Descendants(),
            e => (string?)e.Attribute(Xaml + "Key") == key);
        return double.Parse(token.Value, CultureInfo.InvariantCulture);
    }

    private static string AppPath(string relative) =>
        Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", relative);
}
