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

public sealed class FocusVisualStyleSmokeTests
{
    private static readonly XNamespace PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void FocusVisualStyle_IsDefinedAndAppliedToCoreInteractiveStyles()
    {
        XDocument commonControls = LoadXaml("src", "Heimdall.App", "Themes", "CommonControls.xaml");
        Assert.NotNull(FindStyle(commonControls, "AppFocusVisualStyle"));
        Assert.NotNull(FindStyle(commonControls, "CheckBoxFocusVisualStyle"));

        foreach (string styleKey in new[]
        {
            "PrimaryButtonStyle",
            "SecondaryButtonStyle",
            "GhostButtonStyle",
            "ComboBoxToggleButtonStyle"
        })
        {
            AssertStyleHasFocusVisualSetter(
                commonControls,
                styleKey,
                "{StaticResource AppFocusVisualStyle}");
        }

        foreach (string styleKey in new[]
        {
            "ThemedCheckBoxStyle",
            "ThemedRadioButtonStyle"
        })
        {
            AssertStyleHasFocusVisualSetter(
                commonControls,
                styleKey,
                "{StaticResource CheckBoxFocusVisualStyle}");
        }

        XDocument serverDialog = LoadXaml("src", "Heimdall.App", "Views", "Dialogs", "ServerDialog.xaml");
        AssertStyleHasFocusVisualSetter(
            serverDialog,
            "ProtocolCardButtonStyle",
            "{StaticResource AppFocusVisualStyle}");
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

    private static void AssertStyleHasFocusVisualSetter(
        XDocument document,
        string styleKey,
        string expectedValue)
    {
        XElement? style = FindStyle(document, styleKey);
        Assert.NotNull(style);

        XElement? setter = style!.Elements(PresentationNamespace + "Setter")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute("Property"), "FocusVisualStyle", StringComparison.Ordinal)
                && string.Equals(
                    (string?)element.Attribute("Value"),
                    expectedValue,
                    StringComparison.Ordinal));

        Assert.NotNull(setter);
    }

    private static XElement? FindStyle(XDocument document, string styleKey)
    {
        return document.Descendants(PresentationNamespace + "Style")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute(XamlNamespace + "Key"), styleKey, StringComparison.Ordinal));
    }
}
