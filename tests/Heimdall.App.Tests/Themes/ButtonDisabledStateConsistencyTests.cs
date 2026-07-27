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
/// Guards how a disabled button reads. The primary and secondary styles used to repaint
/// their fill and border to a flat surface colour, which made a disabled primary render
/// as a resting secondary minus its border, and made either of them vanish outright on a
/// backdrop of the same colour. Both now dim through the shared opacity token instead.
///
/// The three facts below are deliberately separate rather than one combined assertion.
/// Each covers a different failure mode, and keeping them apart is what makes a
/// regression legible: a change that repaints a brush again fails the first two, a
/// change that lets some other style start repainting fails only the second, and a
/// change that flattens the two styles onto one visual weight fails only the third.
///
/// The assertions are structural. Contrast ratios were measured once, across every
/// theme and accent tint, at the time the approach was chosen; this guard does not
/// recompute them.
/// </summary>
public sealed class ButtonDisabledStateConsistencyTests
{
    private static readonly XNamespace PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string OpacityTokenKey = "OpacityDisabled";

    private static readonly string[] DimmedButtonStyles =
    [
        "PrimaryButtonStyle",
        "SecondaryButtonStyle"
    ];

    /// <summary>
    /// The rest state each style must keep. Written out per style rather than derived,
    /// because the point of the third fact is to notice if two styles quietly converge.
    /// </summary>
    private static readonly Dictionary<string, (string Background, string BorderBrush, string Foreground)> RestState =
        new(StringComparer.Ordinal)
        {
            ["PrimaryButtonStyle"] = ("AccentBrush", "AccentBrush", "TextOnAccentBrush"),
            ["SecondaryButtonStyle"] = ("CardBrush", "InputBorderBrush", "TextPrimaryBrush")
        };

    [Fact]
    public void DisabledTriggers_OfPrimaryAndSecondaryButtons_DimThroughOpacityOnly()
    {
        XDocument commonControls = LoadCommonControls();

        foreach (string styleKey in DimmedButtonStyles)
        {
            XElement trigger = GetSingleDisabledTrigger(commonControls, styleKey);
            XElement[] setters = trigger.Elements(PresentationNamespace + "Setter").ToArray();

            Assert.True(
                setters.Length == 1,
                $"The disabled trigger of '{styleKey}' must carry exactly one setter, the shared "
                + $"opacity token, but carries {setters.Length}: "
                + $"'{string.Join("', '", setters.Select(DescribeSetter))}'. Repainting the fill or "
                + "the border makes the disabled button impersonate another style, and pinning the "
                + "foreground puts a light text colour on an undimmed accent fill.");

            XElement setter = setters[0];
            string? property = (string?)setter.Attribute("Property");
            Assert.True(
                string.Equals(property, "Opacity", StringComparison.Ordinal),
                $"The disabled trigger of '{styleKey}' must set 'Opacity' but sets "
                + $"'{property ?? "<none>"}'.");

            Assert.True(
                setter.Attribute("TargetName") is null,
                $"The opacity setter of '{styleKey}' must carry no TargetName so it applies to the "
                + "whole button, label included. Targeting the border alone would dim the chrome "
                + "and leave the label at full strength, which reads as an enabled button.");

            (string? kind, string? key) = ExtractResource((string?)setter.Attribute("Value"));
            Assert.True(
                string.Equals(kind, "StaticResource", StringComparison.Ordinal)
                && string.Equals(key, OpacityTokenKey, StringComparison.Ordinal),
                $"The opacity setter of '{styleKey}' must reference "
                + $"'{{StaticResource {OpacityTokenKey}}}' so every disabled control fades by the "
                + $"same amount, but references '{(string?)setter.Attribute("Value") ?? "<none>"}'.");
        }
    }

    [Fact]
    public void DisabledTriggers_AcrossCommonControls_NeverRepaintBackgroundOrBorder()
    {
        XDocument commonControls = LoadCommonControls();

        XElement[] disabledTriggers = commonControls.Descendants(PresentationNamespace + "Trigger")
            .Where(IsDisabledTrigger)
            .ToArray();

        // Anti-vacuity. A selector that silently matches nothing would make every
        // assertion below pass while guarding none of the file, so the sweep has to
        // prove it saw something, and specifically that it saw the two triggers this
        // lot changed.
        Assert.True(
            disabledTriggers.Length > 0,
            "The sweep found no IsEnabled=False trigger in CommonControls.xaml. The selector is "
            + "broken, not the file.");

        foreach (string styleKey in DimmedButtonStyles)
        {
            XElement expected = GetSingleDisabledTrigger(commonControls, styleKey);
            Assert.True(
                disabledTriggers.Any(trigger => ReferenceEquals(trigger, expected)),
                $"The sweep did not reach the disabled trigger of '{styleKey}'. Whatever it is "
                + "covering, it is not the styles this guard exists for.");
        }

        foreach (XElement trigger in disabledTriggers)
        {
            XElement[] repaints = trigger.Elements(PresentationNamespace + "Setter")
                .Where(setter =>
                {
                    string? property = (string?)setter.Attribute("Property");
                    return string.Equals(property, "Background", StringComparison.Ordinal)
                        || string.Equals(property, "BorderBrush", StringComparison.Ordinal);
                })
                .ToArray();

            Assert.True(
                repaints.Length == 0,
                $"A disabled trigger in CommonControls.xaml repaints a brush: "
                + $"'{string.Join("', '", repaints.Select(DescribeSetter))}'. Disabled state is "
                + "carried by opacity across this file so that a control fades against whatever "
                + "backdrop it sits on. A flat repaint matches some backdrops exactly and makes "
                + "the control disappear.");
        }
    }

    [Fact]
    public void PrimaryAndSecondaryButtons_KeepTheirDistinctRestState()
    {
        XDocument commonControls = LoadCommonControls();

        foreach (string styleKey in DimmedButtonStyles)
        {
            XElement style = FindStyle(commonControls, styleKey);
            (string background, string borderBrush, string foreground) = RestState[styleKey];

            AssertStyleSetter(style, styleKey, "Background", background);
            AssertStyleSetter(style, styleKey, "BorderBrush", borderBrush);
            AssertStyleSetter(style, styleKey, "Foreground", foreground);
        }
    }

    /// <summary>
    /// Asserts a style-level setter, that is one declared directly on the Style rather
    /// than inside its template triggers.
    /// </summary>
    private static void AssertStyleSetter(XElement style, string styleKey, string property, string expectedResourceKey)
    {
        XElement? setter = style.Elements(PresentationNamespace + "Setter")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute("Property"), property, StringComparison.Ordinal));

        Assert.True(
            setter is not null,
            $"Style '{styleKey}' must declare a '{property}' setter. Without it the disabled state, "
            + "which is now nothing but an opacity, has no rest state left to fade.");

        (_, string? key) = ExtractResource((string?)setter!.Attribute("Value"));
        Assert.True(
            string.Equals(key, expectedResourceKey, StringComparison.Ordinal),
            $"Style '{styleKey}' must set '{property}' to '{expectedResourceKey}' but sets "
            + $"'{key ?? "<none>"}'. The two button styles carry different visual weights; if they "
            + "converge, every hierarchy built on top of them silently flattens.");
    }

    private static XElement GetSingleDisabledTrigger(XDocument document, string styleKey)
    {
        XElement style = FindStyle(document, styleKey);

        XElement[] triggers = style.Descendants(PresentationNamespace + "Trigger")
            .Where(IsDisabledTrigger)
            .ToArray();

        Assert.True(
            triggers.Length == 1,
            $"Style '{styleKey}' must declare exactly one IsEnabled=False trigger but declares "
            + $"{triggers.Length}.");

        return triggers[0];
    }

    private static bool IsDisabledTrigger(XElement trigger)
    {
        return string.Equals((string?)trigger.Attribute("Property"), "IsEnabled", StringComparison.Ordinal)
            && string.Equals((string?)trigger.Attribute("Value"), "False", StringComparison.Ordinal);
    }

    private static string DescribeSetter(XElement setter)
    {
        string? targetName = (string?)setter.Attribute("TargetName");
        string property = (string?)setter.Attribute("Property") ?? "<none>";
        return targetName is null ? property : $"{targetName}.{property}";
    }

    /// <summary>
    /// Splits a "{StaticResource Key}" or "{DynamicResource Key}" markup extension into
    /// its kind and its key. Returns nulls for any other shape so a malformed reference
    /// fails the assertion instead of silently matching.
    /// </summary>
    private static (string? Kind, string? Key) ExtractResource(string? markupExtension)
    {
        if (string.IsNullOrWhiteSpace(markupExtension))
        {
            return (null, null);
        }

        string trimmed = markupExtension.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            return (null, null);
        }

        string[] parts = trimmed[1..^1]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            return (null, null);
        }

        bool isResourceReference =
            string.Equals(parts[0], "DynamicResource", StringComparison.Ordinal)
            || string.Equals(parts[0], "StaticResource", StringComparison.Ordinal);

        return isResourceReference ? (parts[0], parts[1]) : (null, null);
    }

    private static XElement FindStyle(XDocument document, string styleKey)
    {
        XElement? style = document.Descendants(PresentationNamespace + "Style")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute(XamlNamespace + "Key"), styleKey, StringComparison.Ordinal));

        Assert.True(
            style is not null,
            $"CommonControls.xaml must declare a Style with x:Key '{styleKey}'.");

        return style!;
    }

    private static XDocument LoadCommonControls()
    {
        string repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(repositoryRoot, "src", "Heimdall.App", "Themes", "CommonControls.xaml");

        Assert.True(File.Exists(path), $"Missing XAML file: {path}");
        return XDocument.Load(path);
    }
}
