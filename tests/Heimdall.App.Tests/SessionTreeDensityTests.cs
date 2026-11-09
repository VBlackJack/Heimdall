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
using System.Windows;
using System.Xml.Linq;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The compact spacing of the session tree: which resources it swaps, that the row templates read
/// them instead of literals, and the row pitch the two sets add up to.
/// </summary>
public sealed class SessionTreeDensityTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The pointer target floor, which is also the expander hit area.</summary>
    private const double PointerTargetFloor = 24d;

    private const string ExpanderHitSizeKey = "TreeExpanderHitSize";
    private const string OutlineKey = "SessionTreeRowOutlineThickness";

    [Fact]
    public void WithTheSettingOff_NothingIsOverridden()
    {
        Assert.Empty(SessionTreeDensity.Overrides(compact: false));
    }

    [Fact]
    public void WithTheSettingOn_EveryKeyTakesItsCompactCounterpart()
    {
        IReadOnlyDictionary<string, string> overrides = SessionTreeDensity.Overrides(compact: true);

        Assert.Equal(SessionTreeDensity.RowSpacingKeys.Count, overrides.Count);
        foreach (string key in SessionTreeDensity.RowSpacingKeys)
        {
            Assert.Equal(key + "Compact", overrides[key]);
        }
    }

    [Fact]
    public void TurningTheSettingOnThenOff_PutsTheCompactSetInThenTakesItOut()
    {
        ResourceDictionary tree = new() { ["UnrelatedTemplateKey"] = "kept" };
        static object? Lookup(string key) => "value of " + key;

        SessionTreeDensity.Apply(tree, Lookup, compact: true);

        foreach (string key in SessionTreeDensity.RowSpacingKeys)
        {
            Assert.Equal("value of " + key + "Compact", tree[key]);
        }

        SessionTreeDensity.Apply(tree, Lookup, compact: true);
        Assert.Equal(SessionTreeDensity.RowSpacingKeys.Count + 1, tree.Count);

        SessionTreeDensity.Apply(tree, Lookup, compact: false);

        foreach (string key in SessionTreeDensity.RowSpacingKeys)
        {
            Assert.False(tree.Contains(key), $"{key} is still overridden with the setting off");
        }

        Assert.Equal("kept", tree["UnrelatedTemplateKey"]);
    }

    [Fact]
    public void ApplyingTheSameStateTwice_ChangesNothingTheSecondTime()
    {
        ResourceDictionary tree = new();
        static object? Lookup(string key) => "value of " + key;

        SessionTreeDensity.Apply(tree, Lookup, compact: false);
        Assert.Empty(tree.Keys);

        SessionTreeDensity.Apply(tree, Lookup, compact: true);
        object[] before = [.. SessionTreeDensity.RowSpacingKeys.Select(key => tree[key])];
        SessionTreeDensity.Apply(tree, Lookup, compact: true);

        Assert.Equal(before, SessionTreeDensity.RowSpacingKeys.Select(key => tree[key]));
        Assert.Equal(SessionTreeDensity.RowSpacingKeys.Count, tree.Count);
    }

    [Fact]
    public void ACompactKeyTheThemeLacks_IsRefusedRatherThanSkipped()
    {
        ResourceDictionary tree = new();

        Assert.Throws<InvalidOperationException>(
            () => SessionTreeDensity.Apply(tree, _ => null, compact: true));
    }

    [Fact]
    public void EveryKeyIsDefinedTwice_AsTheDefaultAndAsTheCompactValue()
    {
        Dictionary<string, string> theme = ReadThemeValues();

        foreach (string key in SessionTreeDensity.RowSpacingKeys)
        {
            Assert.True(theme.ContainsKey(key), $"{key} is not defined in CommonControls.xaml");
            Assert.True(
                theme.ContainsKey(SessionTreeDensity.CompactKeyFor(key)),
                $"{key} has no compact counterpart in CommonControls.xaml");
        }
    }

    /// <summary>
    /// The templates read the resources; a literal left in one would make the setting change a
    /// part of the row and leave the rest as it was.
    /// </summary>
    [Fact]
    public void TheRowTemplatesAndTheContainerStyle_ReadTheResourcesWithDynamicResource()
    {
        XElement folderRow = FindNamed("FolderSelectionChrome");
        XElement serverRow = FindNamed("ServerSelectionChrome");

        Assert.Equal("{DynamicResource SessionTreeFolderRowMargin}", (string?)folderRow.Attribute("Margin"));
        Assert.Equal("{DynamicResource SessionTreeFolderRowPadding}", (string?)folderRow.Attribute("Padding"));
        Assert.Equal("{DynamicResource SessionTreeFolderRowMinHeight}", (string?)folderRow.Attribute("MinHeight"));
        Assert.Equal("{DynamicResource SessionTreeServerRowMargin}", (string?)serverRow.Attribute("Margin"));
        Assert.Equal("{DynamicResource SessionTreeServerRowPadding}", (string?)serverRow.Attribute("Padding"));
        Assert.Equal("{DynamicResource SessionTreeServerRowMinHeight}", (string?)serverRow.Attribute("MinHeight"));
        Assert.Equal("{DynamicResource SessionTreeChildrenHostMargin}", (string?)FindNamed("ChildrenHost").Attribute("Margin"));
        Assert.Equal("{DynamicResource SessionTreeItemsHostMargin}", (string?)FindNamed("ItemsHost").Attribute("Margin"));

        XElement containerStyle = Assert.Single(
            FindNamed("SessionTreeView").Elements(),
            e => e.Name.LocalName == "TreeView.ItemContainerStyle")
            .Elements().Single();
        XElement padding = Assert.Single(
            containerStyle.Elements(),
            e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "Padding");
        Assert.Equal("{DynamicResource SessionTreeItemPadding}", (string?)padding.Attribute("Value"));
    }

    [Fact]
    public void TheDefaultValues_AreTheOnesTheRowsHadBeforeTheSettingExisted()
    {
        Dictionary<string, string> theme = ReadThemeValues();

        Assert.Equal("0,3,4,1", theme["SessionTreeFolderRowMargin"]);
        Assert.Equal("1,3,6,3", theme["SessionTreeFolderRowPadding"]);
        Assert.Equal("26", theme["SessionTreeFolderRowMinHeight"]);
        Assert.Equal("0,0,4,0", theme["SessionTreeServerRowMargin"]);
        Assert.Equal("3,2,4,2", theme["SessionTreeServerRowPadding"]);
        Assert.Equal("24", theme["SessionTreeServerRowMinHeight"]);
        Assert.Equal("1,1", theme["SessionTreeItemPadding"]);
        Assert.Equal("0,1,0,0", theme["SessionTreeChildrenHostMargin"]);
        Assert.Equal("2,1,0,0", theme["SessionTreeItemsHostMargin"]);
    }

    [Fact]
    public void TheDefaultRowPitch_IsWhatItWasBefore()
    {
        Dictionary<string, string> theme = ReadThemeValues();

        Assert.Equal(30d, Pitch(theme, "SessionTreeServerRowMargin", "SessionTreeServerRowMinHeight", compact: false));
        Assert.Equal(36d, Pitch(theme, "SessionTreeFolderRowMargin", "SessionTreeFolderRowMinHeight", compact: false));
    }

    [Fact]
    public void TheCompactRowPitch_IsTheTargetFloorForBothRowKinds()
    {
        Dictionary<string, string> theme = ReadThemeValues();
        double expanderHit = double.Parse(theme[ExpanderHitSizeKey], CultureInfo.InvariantCulture);

        Assert.Equal(PointerTargetFloor, expanderHit);
        Assert.Equal(PointerTargetFloor, Pitch(theme, "SessionTreeServerRowMargin", "SessionTreeServerRowMinHeight", compact: true));
        Assert.Equal(PointerTargetFloor, Pitch(theme, "SessionTreeFolderRowMargin", "SessionTreeFolderRowMinHeight", compact: true));
    }

    /// <summary>
    /// A folder row is followed by its first child through the two host margins: left in, the
    /// compact tree would open a 2 px gap under every expanded folder.
    /// </summary>
    [Fact]
    public void TheGapUnderAnExpandedFolder_ClosesWithTheSettingOn()
    {
        Dictionary<string, string> theme = ReadThemeValues();

        Assert.Equal(2d, ChildrenGap(theme, compact: false));
        Assert.Equal(0d, ChildrenGap(theme, compact: true));
    }

    /// <summary>
    /// The outline stays whole in both sets: the pitch is the outline on each side plus the rest,
    /// never the outline squeezed out of it.
    /// </summary>
    [Fact]
    public void TheOutline_IsTheSameInBothSets()
    {
        Dictionary<string, string> theme = ReadThemeValues();

        Assert.Equal("2", theme[OutlineKey]);
        Assert.False(theme.ContainsKey(OutlineKey + "Compact"));
    }

    private static double Pitch(Dictionary<string, string> theme, string marginKey, string minHeightKey, bool compact)
    {
        string Key(string key) => compact ? SessionTreeDensity.CompactKeyFor(key) : key;

        double outline = Vertical(theme[OutlineKey]);
        double containerPadding = Vertical(theme[Key("SessionTreeItemPadding")]);
        double margin = Vertical(theme[Key(marginKey)]);
        double minHeight = double.Parse(theme[Key(minHeightKey)], CultureInfo.InvariantCulture);

        return outline + containerPadding + margin + minHeight;
    }

    private static double ChildrenGap(Dictionary<string, string> theme, bool compact)
    {
        string Key(string key) => compact ? SessionTreeDensity.CompactKeyFor(key) : key;

        return Top(theme[Key("SessionTreeChildrenHostMargin")]) + Top(theme[Key("SessionTreeItemsHostMargin")]);
    }

    /// <summary>Top plus bottom of a Thickness written as one, two or four numbers.</summary>
    private static double Vertical(string thickness)
    {
        double[] parts = ParseThickness(thickness);
        return parts[1] + parts[3];
    }

    private static double Top(string thickness) => ParseThickness(thickness)[1];

    /// <summary>Left, top, right, bottom.</summary>
    private static double[] ParseThickness(string thickness)
    {
        double[] numbers =
        [
            .. thickness.Split(',').Select(part => double.Parse(part, CultureInfo.InvariantCulture))
        ];

        return numbers.Length switch
        {
            1 => [numbers[0], numbers[0], numbers[0], numbers[0]],
            2 => [numbers[0], numbers[1], numbers[0], numbers[1]],
            4 => numbers,
            _ => throw new FormatException($"'{thickness}' is not a Thickness"),
        };
    }

    private static Dictionary<string, string> ReadThemeValues()
    {
        XDocument theme = XDocument.Load(
            Path.Combine(FindRepositoryRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));

        Dictionary<string, string> values = [];
        foreach (XElement element in theme.Descendants())
        {
            string? key = (string?)element.Attribute(XamlNamespace + "Key");
            if (key is not null && element.Name.LocalName is "Double" or "Thickness")
            {
                values[key] = element.Value.Trim();
            }
        }

        Assert.True(values.Count > 50, "the theme scan found almost nothing, so it no longer reads what it thinks it does");
        return values;
    }

    private static XDocument LoadWindow()
        => XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Heimdall.App", "MainWindow.xaml"));

    private static XElement FindNamed(string name)
        => Assert.Single(LoadWindow().Descendants(), e => (string?)e.Attribute(XamlNamespace + "Name") == name);

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException($"Cannot find repository root containing Heimdall.slnx from {AppContext.BaseDirectory}.");
    }
}
