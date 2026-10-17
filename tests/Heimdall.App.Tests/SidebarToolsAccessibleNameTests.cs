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

using System.Collections.ObjectModel;
using System.IO;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// The sidebar's Tools tree read out "Heimdall.App.ViewModels.SidebarToolItemViewModel" for every
/// tool and category: the template's AutomationProperties.Name sits on a TextBlock inside the
/// container, which UI Automation never reads, so the container fell back to ToString().
/// Measured through UI Automation on 2026-10-17, then fixed the way the session tree was.
/// </summary>
public sealed class SidebarToolsAccessibleNameTests
{
    [Fact]
    public void AToolAndItsCategoryAnnounceTheirNames()
    {
        SidebarToolItemViewModel tool = new()
        {
            Id = "REGEX",
            Name = "Regex Tester",
            BrushKey = "ToolEncodingBrush",
            Searchable = "regex tester",
        };
        SidebarToolCategoryViewModel category = new()
        {
            CategoryKey = "ToolCategoryEncoding",
            CategoryName = "Encoding & Format",
            BrushKey = "ToolEncodingBrush",
            Tools = new ObservableCollection<SidebarToolItemViewModel> { tool },
        };

        Assert.Equal("Regex Tester", Assert.IsAssignableFrom<IAccessibleItemViewModel>(tool).AccessibleName);
        Assert.Equal("Encoding & Format", Assert.IsAssignableFrom<IAccessibleItemViewModel>(category).AccessibleName);
    }

    [Fact]
    public void TheToolsTreeAppliesTheIdentityOnItsContainers()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument window = XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XElement tree = Assert.Single(window.Descendants(), e => (string?)e.Attribute(x + "Name") == "SidebarToolsTreeView");

        XElement style = Assert.Single(tree.Elements(), e => e.Name.LocalName == "TreeView.ItemContainerStyle").Elements().Single();

        Assert.Contains(
            style.Elements(),
            setter => (string?)setter.Attribute("Property") == "behaviors:ItemContainerAccessibilityBehavior.IsEnabled"
                && (string?)setter.Attribute("Value") == "True");
    }
}
