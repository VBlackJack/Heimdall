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
/// Every input on the Settings tab has an accessible name.
/// </summary>
/// <remarks>
/// <para>The 2026-09-30 audit counted 24 unnamed inputs on this tab - the credential provider preset
/// and command, the session log folder, six Diagnostics numbers, every tool path, the whole Git sync
/// card, the external tool editor and both lists - and a live pass found the six top tabs reading
/// out as "System.Windows.Controls.TabItem Header: Content:". A screen reader user met each of them
/// as "edit" or as a type name. Each was named by hand; this guard is what keeps the next one from
/// arriving unnamed.</para>
/// <para>A name counts when the element carries <c>AutomationProperties.Name</c> (attribute or
/// property element) or <c>AutomationProperties.LabeledBy</c>; a check box or a radio button is also
/// named by its content, and a tab item by a Header attribute. A tab item whose header is a panel is
/// not: that is the shape that read out as a type name.</para>
/// </remarks>
public sealed class SettingsAccessibleNameGuardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> Inputs = new(StringComparer.Ordinal)
    {
        "TextBox", "PasswordBox", "ComboBox", "ListBox", "CheckBox", "RadioButton", "TabItem", "TabControl", "DataGrid",
    };

    private static readonly HashSet<string> NamedByContent = new(StringComparer.Ordinal)
    {
        "CheckBox", "RadioButton",
    };

    [Fact]
    public void EveryInputOnTheSettingsTabHasAnAccessibleName()
    {
        XElement root = SettingsRoot();

        List<string> unnamed = FindUnnamed(root);
        int inputs = root.Descendants().Count(element => Inputs.Contains(element.Name.LocalName));

        // Guarding the guard: a region lookup that found a stub would pass having read nothing.
        Assert.True(inputs >= 120, $"only {inputs} inputs were read from the Settings region");
        Assert.True(unnamed.Count == 0, "unnamed Settings inputs:" + Environment.NewLine + string.Join(Environment.NewLine, unnamed));
    }

    /// <summary>The positive control: the same check finds every shape it is meant to refuse.</summary>
    [Fact]
    public void TheCheckFindsAnUnnamedInput()
    {
        XElement sample = XElement.Parse(
            """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <TextBox Text="{Binding A}"/>
                <TextBox Text="{Binding B}" AutomationProperties.LabeledBy="{x:Reference L}"/>
                <PasswordBox x:Name="Secret"/>
                <ComboBox AutomationProperties.Name="Named"/>
                <CheckBox IsChecked="{Binding C}"/>
                <CheckBox Content="Labelled"/>
                <TabItem><TabItem.Header><StackPanel/></TabItem.Header></TabItem>
                <TabItem Header="Plain"/>
                <TabItem>
                    <AutomationProperties.Name>Bound</AutomationProperties.Name>
                    <TabItem.Header><StackPanel/></TabItem.Header>
                </TabItem>
                <ListBox/>
            </Grid>
            """);

        List<string> unnamed = FindUnnamed(sample);

        Assert.Equal(5, unnamed.Count);
        Assert.Contains(unnamed, entry => entry.StartsWith("TextBox", StringComparison.Ordinal));
        Assert.Contains(unnamed, entry => entry.StartsWith("PasswordBox Secret", StringComparison.Ordinal));
        Assert.Contains(unnamed, entry => entry.StartsWith("CheckBox", StringComparison.Ordinal));
        Assert.Contains(unnamed, entry => entry.StartsWith("TabItem", StringComparison.Ordinal));
        Assert.Contains(unnamed, entry => entry.StartsWith("ListBox", StringComparison.Ordinal));
    }

    private static List<string> FindUnnamed(XElement root)
    {
        List<string> unnamed = [];
        foreach (XElement element in root.Descendants())
        {
            string kind = element.Name.LocalName;
            if (!Inputs.Contains(kind) || HasName(element))
            {
                continue;
            }

            string name = element.Attribute(Xaml + "Name")?.Value ?? string.Empty;
            string text = element.Attribute("Text")?.Value ?? element.Attribute("ItemsSource")?.Value ?? string.Empty;
            int line = ((System.Xml.IXmlLineInfo)element).LineNumber;
            unnamed.Add($"{kind} {name} {text} (line {line})".Replace("  ", " ", StringComparison.Ordinal));
        }

        return unnamed;
    }

    private static bool HasName(XElement element)
    {
        if (element.Attributes().Any(attribute =>
                attribute.Name.LocalName is "AutomationProperties.Name" or "AutomationProperties.LabeledBy")
            || element.Elements().Any(child => child.Name.LocalName == "AutomationProperties.Name"))
        {
            return true;
        }

        string kind = element.Name.LocalName;
        if (NamedByContent.Contains(kind))
        {
            return element.Attribute("Content") is not null
                || element.Elements().Any(child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal));
        }

        return kind == "TabItem" && element.Attribute("Header") is not null;
    }

    private static XElement SettingsRoot()
    {
        string path = Path.Combine(SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml");
        XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
        return document.Descendants()
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "Mw_SettingsRoot");
    }
}
