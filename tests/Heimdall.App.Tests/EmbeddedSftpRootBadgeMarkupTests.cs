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
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The root badge of the SFTP toolbar, read from its markup: where it sits, what it binds, that it
/// is one announced live region, and that the list it opens shows a path as text and not as an
/// access-key header.
/// </summary>
public sealed class EmbeddedSftpRootBadgeMarkupTests
{
    private const string BadgeName = "BtnRootActivity";
    private const string SudoToggleName = "BtnSudoMode";
    private const string IconName = "RootActivityIcon";
    private const string LabelName = "RootActivityLabel";
    private const string CountName = "RootActivityCount";
    private const string MenuName = "RootActivityContextMenu";

    private static readonly XNamespace s_xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex s_bareNumber = new(@"^\s*-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?)*\s*$", RegexOptions.None, TimeSpan.FromSeconds(5));
    private static readonly string[] s_sizeAttributes = ["Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "FontSize"];

    [Fact]
    public void TheBadge_SitsRightAfterTheSudoToggle()
    {
        XDocument markup = Markup();
        XElement toggle = Named(markup, SudoToggleName);
        XElement badge = Named(markup, BadgeName);

        Assert.Same(toggle.Parent, badge.Parent);
        Assert.Same(badge, toggle.ElementsAfterSelf().First());
    }

    [Fact]
    public void TheBadge_IsShownOnlyOnceRootWasUsedAndOpensTheListLikeTheOtherMenuButtons()
    {
        XElement badge = Named(Markup(), BadgeName);

        Assert.StartsWith("{Binding HasRootActivity", Attr(badge, "Visibility"), StringComparison.Ordinal);
        Assert.Equal("OnToolbarMenuButtonClick", Attr(badge, "Click"));
        Assert.Equal("{DynamicResource GhostButtonStyle}", Attr(badge, "Style"));
        Assert.Equal("{StaticResource SftpToolbarButtonMinHeight}", Attr(badge, "MinHeight"));
        Assert.False(HasAttr(badge, "ToolTip"), "the badge tooltip must be an element that wraps its text");
        Assert.True(
            WrapsItsText(badge.Elements().First(element => element.Name.LocalName == "Button.ToolTip")),
            "the badge tooltip does not wrap");
        Assert.Equal("{Binding RootActivitySummary}", Attr(badge, "AutomationProperties.Name"));
    }

    [Fact]
    public void TheIcon_IsTheOneLiveRegionAndItsNameIsConstant()
    {
        XElement icon = Named(Markup(), IconName);

        Assert.Equal("Polite", Attr(icon, "AutomationProperties.LiveSetting"));
        Assert.Equal("True", Attr(icon, "behaviors:LiveRegionBehavior.AnnounceOnTargetUpdated"));
        string name = Attr(icon, "AutomationProperties.Name");
        Assert.Equal("{loc:Translate SftpRootBadgeAnnouncement}", name);
        Assert.Equal(1, LiveRegionsUnder(Named(Markup(), BadgeName)));
    }

    [Fact]
    public void TheNumber_ShowsTheCountAndIsHiddenWhileItIsZero()
    {
        XElement count = Named(Markup(), CountName);

        Assert.Equal("{Binding RootOperationCountText}", Attr(count, "Text"));
        Assert.StartsWith("{Binding HasRootOperations", Attr(count, "Visibility"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLabel_IsTranslatedAndCanBeHiddenByTheCompactLayout()
    {
        XElement label = Named(Markup(), LabelName);

        Assert.Equal("{loc:Translate SftpRootBadgeLabel}", Attr(label, "Text"));
    }

    [Fact]
    public void TheList_IsBoundAndEachItemShowsItsPathThroughATemplate()
    {
        XDocument markup = Markup();

        Assert.True(MenuItemsShowTheirTextThroughATemplate(markup), "the menu item header template is missing");
        XElement menu = Named(markup, MenuName);
        Assert.Equal("{Binding RootActivityEntries}", Attr(menu, "ItemsSource"));
        Assert.Equal("{loc:Translate A11ySftpRootActivityMenu}", Attr(menu, "AutomationProperties.Name"));
        Assert.Equal("{Binding}", ItemSetter(menu, "Header"));
        Assert.False(ItemSetterExists(menu, "InputGestureText"), "the time must not be the accelerator key of the item");
        Assert.True(WrapsItsText(ItemSetterElement(menu, "ToolTip")), "the item tooltip does not wrap");
        Assert.True(ItemStyleSetsTheListingLineApart(menu), "the listing line is not set apart");
        Assert.Equal("{Binding AutomationName}", ItemSetter(menu, "AutomationProperties.Name"));
    }

    [Fact]
    public void TheHeaderTemplateCheck_FailsWhenTheTemplateIsRemoved()
    {
        // The positive control: the same check, on a copy of the markup without the template.
        XDocument mutated = Markup();
        XElement menu = Named(mutated, MenuName);
        XElement template = menu
            .Descendants()
            .First(element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "HeaderTemplate");
        template.Remove();

        Assert.True(MenuItemsShowTheirTextThroughATemplate(Markup()));
        Assert.False(MenuItemsShowTheirTextThroughATemplate(mutated));
    }

    [Fact]
    public void TheWrapCheck_FailsOnATooltipThatDoesNotWrap()
    {
        XElement wrapping = XElement.Parse("<ToolTip><TextBlock TextWrapping=\"Wrap\" MaxWidth=\"{StaticResource W}\"/></ToolTip>");
        XElement plain = XElement.Parse("<ToolTip><TextBlock/></ToolTip>");

        Assert.True(WrapsItsText(wrapping));
        Assert.False(WrapsItsText(plain));
    }

    [Fact]
    public void TheBadge_HasNoBareSizeAndNoHardcodedColour()
    {
        XElement badge = Named(Markup(), BadgeName);

        IReadOnlyList<string> bareSizes = BareSizes(badge);
        IReadOnlyList<string> literalColours = LiteralColours(badge);

        Assert.True(bareSizes.Count == 0, "bare sizes: " + string.Join(", ", bareSizes));
        Assert.True(literalColours.Count == 0, "literal colours: " + string.Join(", ", literalColours));
    }

    [Fact]
    public void TheSizeReading_FlagsABareNumber()
    {
        XElement sample = XElement.Parse("<Button xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" MaxWidth=\"240\" Foreground=\"#FF0000\"/>");

        Assert.Single(BareSizes(sample));
        Assert.Single(LiteralColours(sample));
    }

    // Readings ---------------------------------------------------------------------------------

    private static XDocument Markup()
        => XDocument.Load(
            Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedSftpView.xaml"),
            LoadOptions.PreserveWhitespace);

    private static XElement Named(XDocument markup, string name)
    {
        XElement? element = markup
            .Descendants()
            .FirstOrDefault(candidate => (string?)candidate.Attribute(s_xaml + "Name") == name);
        Assert.True(element is not null, $"No element named '{name}' in EmbeddedSftpView.xaml.");
        return element!;
    }

    private static string Attr(XElement element, string name)
    {
        string local = name.Split(':')[^1];
        XAttribute? attribute = element.Attributes().FirstOrDefault(candidate => candidate.Name.LocalName == local);
        Assert.True(attribute is not null, $"{element.Name.LocalName} has no {name}");
        return attribute!.Value;
    }

    private static int LiveRegionsUnder(XElement root)
        => root
            .DescendantsAndSelf()
            .Count(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "AutomationProperties.LiveSetting"));

    private static string ItemSetter(XElement menu, string property)
    {
        XElement? setter = menu
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == property);
        Assert.True(setter is not null, $"the item style has no {property} setter");
        return (string?)setter!.Attribute("Value") ?? string.Empty;
    }

    private static bool MenuItemsShowTheirTextThroughATemplate(XDocument markup)
    {
        XElement menu = Named(markup, MenuName);
        XElement? setter = menu
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "HeaderTemplate");
        if (setter is null)
        {
            return false;
        }

        List<XElement> texts = setter
            .Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .ToList();
        XElement? path = texts.FirstOrDefault(text => (string?)text.Attribute("Text") == "{Binding Text}");
        XElement? time = texts.FirstOrDefault(text => (string?)text.Attribute("Text") == "{Binding TimeText}");
        XElement? grid = setter.Descendants().FirstOrDefault(element => element.Name.LocalName == "Grid");
        return path is not null
            && time is not null
            && grid is not null
            && (string?)path.Attribute("TextTrimming") == "CharacterEllipsis"
            && (string?)time.Attribute("HorizontalAlignment") == "Right"
            && ((string?)grid.Attribute("MaxWidth") ?? string.Empty).StartsWith("{StaticResource", StringComparison.Ordinal);
    }

    private static bool HasAttr(XElement element, string name)
        => element.Attributes().Any(attribute => attribute.Name.LocalName == name);

    // A tooltip shows its text through a TextBlock that wraps within a bounded width.
    private static bool WrapsItsText(XElement owner)
    {
        XElement? text = owner
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "TextBlock");
        return text is not null
            && (string?)text.Attribute("TextWrapping") == "Wrap"
            && ((string?)text.Attribute("MaxWidth") ?? string.Empty).StartsWith("{StaticResource", StringComparison.Ordinal);
    }

    private static XElement ItemSetterElement(XElement menu, string property)
    {
        XElement? setter = menu
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == property);
        Assert.True(setter is not null, $"the item style has no {property} setter");
        return setter!;
    }

    private static bool ItemSetterExists(XElement menu, string property)
        => menu.Descendants().Any(element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == property);

    private static bool ItemStyleSetsTheListingLineApart(XElement menu)
        => menu
            .Descendants()
            .Any(element => element.Name.LocalName == "DataTrigger" && (string?)element.Attribute("Binding") == "{Binding IsSummary}");

    private static List<string> BareSizes(XElement root)
    {
        List<string> offenders = [];
        foreach (XElement element in root.DescendantsAndSelf())
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                if (s_sizeAttributes.Contains(attribute.Name.LocalName) && s_bareNumber.IsMatch(attribute.Value))
                {
                    offenders.Add($"{element.Name.LocalName}.{attribute.Name.LocalName}={attribute.Value}");
                }
            }
        }

        return offenders;
    }

    private static List<string> LiteralColours(XElement root)
    {
        List<string> offenders = [];
        foreach (XElement element in root.DescendantsAndSelf())
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                bool isBrush = attribute.Name.LocalName is "Foreground" or "Background" or "BorderBrush" or "Fill" or "Stroke";
                if (isBrush && !attribute.Value.StartsWith('{'))
                {
                    offenders.Add($"{element.Name.LocalName}.{attribute.Name.LocalName}={attribute.Value}");
                }
            }
        }

        return offenders;
    }
}
