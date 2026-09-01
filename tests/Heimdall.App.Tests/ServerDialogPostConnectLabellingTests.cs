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

namespace Heimdall.App.Tests;

/// <summary>
/// A post-connect row was four controls with nothing on screen saying what any of them was.
/// The shape chosen instead of a header row is recorded here, because the reason a header was
/// rejected is a layout property that no screenshot preserves: the controls beside the command
/// sit in a WrapPanel and change line at the dialog's own width, so anything aligned to them
/// from outside the list is aligned to a position that does not hold.
/// </summary>
public sealed class ServerDialogPostConnectLabellingTests
{
    /// <summary>
    /// The delay field and the failure policy are the two controls a first-time reader cannot
    /// name from their contents: a bare number and a combo that reads "Continue". Each one is
    /// stacked with its caption inside a single panel, so the WrapPanel lays the pair out as one
    /// item and a line break can never fall between a label and the control it names.
    /// </summary>
    [Fact]
    public void NeitherNarrowStepControlCanBeSeparatedFromItsLabel()
    {
        XElement wrapPanel = PostConnectOverflowPanel();

        AssertTravelsWithItsLabel(wrapPanel, IsDelayInput, "delay");
        AssertTravelsWithItsLabel(wrapPanel, IsFailurePolicyInput, "failure policy");
    }

    /// <summary>
    /// The enabled checkbox is the one control here that keeps a hover label. Its column is
    /// 32 px wide and shares the command's row, where ServerDialogPostConnectLayoutTests holds
    /// the fixed width down to protect the field the user types in, so a caption cannot be put
    /// beside it without spending that budget. A tooltip is what is left.
    /// </summary>
    [Fact]
    public void TheEnabledToggleCarriesAHoverLabel()
    {
        XElement checkBox = Assert.Single(
            PostConnectRowGrid().Descendants(),
            element => element.Name.LocalName == "CheckBox");

        Assert.False(
            string.IsNullOrWhiteSpace(checkBox.Attribute("ToolTip")?.Value),
            "The enabled checkbox shows no label at all: it has no room for a caption and no "
            + "tooltip either, so nothing on screen or on hover says what it toggles.");
    }

    /// <summary>
    /// An empty sequence is the normal case, so the blank card is the first thing most people
    /// see. The sentence has to be inside it - a line printed under the list explains a box the
    /// reader has already given up on - and it must not take the click that lands on it.
    /// </summary>
    [Fact]
    public void TheEmptyStateSentenceSitsInsideTheListBoxWithoutSwallowingClicks()
    {
        XDocument document = LoadServerDialogXaml();

        XElement emptyState = Assert.Single(
            document.Descendants(),
            element => element.Attribute("Visibility")?.Value.Contains(
                "HasNoPostConnectSteps",
                StringComparison.Ordinal) == true);

        XElement? overlayHost = PostConnectStepsList(document).Parent;
        Assert.Equal("Grid", overlayHost?.Name.LocalName);
        Assert.Same(overlayHost, emptyState.Parent);
        Assert.Equal("False", emptyState.Attribute("IsHitTestVisible")?.Value);
    }

    /// <summary>
    /// Two fields on this dialog have to teach something before anything is typed: the folder
    /// path, whose separator is the only thing that makes a tree, and the post-connect command,
    /// whose row is too dense to carry a caption. Both rely on a placeholder, and a placeholder
    /// set on a control whose template has no watermark part is text that never renders - the
    /// silent half of this pair, and the reason the style is asserted alongside the tag.
    /// </summary>
    [Fact]
    public void TheFieldsThatTeachAConventionCarryAPlaceholderThatActuallyRenders()
    {
        XDocument document = LoadServerDialogXaml();
        Dictionary<string, string> styleParents = CollectLocalStyleParents(document);

        XElement folderBox = Assert.Single(
            document.Descendants(),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "DlgSrv_FolderBox"));

        XElement commandBox = Assert.Single(
            PostConnectRowGrid().Descendants(),
            IsCommandInput);

        AssertRendersItsPlaceholder(folderBox, styleParents, "folder");
        AssertRendersItsPlaceholder(commandBox, styleParents, "post-connect command");
    }

    private static void AssertTravelsWithItsLabel(
        XElement wrapPanel,
        Func<XElement, bool> isControl,
        string description)
    {
        XElement[] directChildren = [.. wrapPanel.Elements()
            .Where(element => !element.Name.LocalName.Contains('.', StringComparison.Ordinal))];

        Assert.False(
            directChildren.Any(isControl),
            $"The {description} control is a direct child of the overflow panel, so it wraps on "
            + "its own and any label beside it wraps separately.");

        XElement group = Assert.Single(
            directChildren,
            child => child.Descendants().Any(isControl));

        Assert.True(
            group.Descendants().Any(element => element.Name.LocalName == "TextBlock"),
            $"The {description} control is laid out on its own, with no label inside the same "
            + "panel. The overflow row wraps at the dialog's width, so a label placed beside it "
            + "rather than with it can end a line while its control starts the next one.");
    }

    private static void AssertRendersItsPlaceholder(
        XElement box,
        IReadOnlyDictionary<string, string> styleParents,
        string description)
    {
        string? tag = box.Attribute("Tag")?.Value;
        Assert.True(
            tag is not null && tag.Contains("Translate", StringComparison.Ordinal),
            $"The {description} field carries no placeholder, so an empty field says nothing "
            + "about what belongs in it.");

        string? styleKey = StyleKeyOf(box);
        Assert.True(
            styleKey is not null,
            $"The {description} field declares no style, so its placeholder cannot render.");

        Assert.True(
            ResolvesToWatermark(styleKey!, styleParents),
            $"The {description} field sets a placeholder but its style resolves to {styleKey}, "
            + "whose template has no watermark part. The tag is set and nothing is drawn.");
    }

    private static bool ResolvesToWatermark(
        string styleKey,
        IReadOnlyDictionary<string, string> styleParents)
    {
        string? current = styleKey;
        // Bounded by the number of styles declared locally, so a BasedOn cycle cannot hang here.
        for (int hop = 0; current is not null && hop <= styleParents.Count; hop++)
        {
            if (current.Contains("Watermark", StringComparison.Ordinal))
            {
                return true;
            }

            current = styleParents.TryGetValue(current, out string? parent) ? parent : null;
        }

        return false;
    }

    /// <summary>
    /// Maps every keyed style declared in the dialog itself to the key it is based on. Styles
    /// that come from a merged dictionary end the walk, which is why the check is on the name
    /// rather than on the template: their content is not in this file.
    /// </summary>
    private static Dictionary<string, string> CollectLocalStyleParents(XDocument document)
    {
        Dictionary<string, string> parents = new(StringComparer.Ordinal);

        foreach (XElement style in document.Descendants().Where(element => element.Name.LocalName == "Style"))
        {
            string? key = style.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "Key")?.Value;
            string? basedOn = ResourceKeyOf(style.Attribute("BasedOn")?.Value);

            if (key is not null && basedOn is not null)
            {
                parents[key] = basedOn;
            }
        }

        return parents;
    }

    private static string? StyleKeyOf(XElement box)
    {
        string? inlineStyle = ResourceKeyOf(box.Attribute("Style")?.Value);
        if (inlineStyle is not null)
        {
            return inlineStyle;
        }

        XElement? styleElement = box.Elements()
            .FirstOrDefault(element => element.Name.LocalName.EndsWith(".Style", StringComparison.Ordinal))
            ?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Style");

        return ResourceKeyOf(styleElement?.Attribute("BasedOn")?.Value);
    }

    private static string? ResourceKeyOf(string? markup)
    {
        if (markup is null)
        {
            return null;
        }

        int start = markup.IndexOf("Resource ", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += "Resource ".Length;
        int end = markup.IndexOf('}', start);
        return end < 0 ? null : markup[start..end].Trim();
    }

    private static bool IsCommandInput(XElement element)
        => element.Name.LocalName == "TextBox"
           && string.Equals(
               element.Attribute("Text")?.Value,
               "{Binding Input, UpdateSourceTrigger=PropertyChanged}",
               StringComparison.Ordinal);

    private static bool IsDelayInput(XElement element)
        => element.Name.LocalName == "TextBox"
           && element.Attribute("Text")?.Value.Contains("DelayMs", StringComparison.Ordinal) == true;

    private static bool IsFailurePolicyInput(XElement element)
        => element.Name.LocalName == "ComboBox"
           && element.Attribute("SelectedValue")?.Value.Contains("OnFailure", StringComparison.Ordinal) == true;

    private static XElement PostConnectOverflowPanel()
        => Assert.Single(
            PostConnectRowGrid().Descendants(),
            element => element.Name.LocalName == "WrapPanel");

    private static XElement PostConnectStepsList()
        => PostConnectStepsList(LoadServerDialogXaml());

    private static XElement PostConnectStepsList(XDocument document)
    {
        return Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "ListView"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == "DlgSrv_PostConnectStepsList"));
    }

    private static XElement PostConnectRowGrid()
    {
        XElement template = Assert.Single(
            PostConnectStepsList().Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value.Contains(
                    "PostConnectStepItemViewModel",
                    StringComparison.Ordinal) == true);

        return Assert.Single(template.Elements(), element => element.Name.LocalName == "Grid");
    }

    private static XDocument LoadServerDialogXaml()
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(
            repoRoot,
            "src",
            "Heimdall.App",
            "Views",
            "Dialogs",
            "ServerDialog.xaml");

        Assert.True(File.Exists(path), $"Server dialog XAML not found: {path}");
        return XDocument.Load(path);
    }
}
