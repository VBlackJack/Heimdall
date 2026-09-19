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

namespace Heimdall.App.Tests;

/// <summary>
/// Pins how the tab-strip broadcast marker is wired in the markup.
/// </summary>
/// <remarks>
/// <para>
/// <b>It shipped as a CheckBox with a one-way IsChecked beside a Command</b> - the same shape
/// PR #439 removed from the Command Library's own toggle. The UIA <c>Toggle</c> pattern flips
/// such a control without raising <c>Click</c>, so the command never runs and the marker reads
/// ticked over a pane that was never selected.
/// </para>
/// <para>
/// Here the fix could not be the two-way binding #439 used, because
/// <c>SessionTabViewModel.IsBroadcastTarget</c> is <b>derived</b>: it is recomputed from the panes
/// by <c>RefreshBroadcastTabMarkers</c>, so writing it back would be writing into a value the next
/// refresh discards. The marker is a Button that draws a box instead, which offers Invoke and no
/// Toggle at all.
/// </para>
/// </remarks>
public sealed class BroadcastTabMarkerMarkupTests
{
    private const string MarkerName = "Mw_SessionTabBroadcastTarget";

    /// <summary>
    /// Invoke raises Click and therefore runs the command; Toggle does not. The control type is
    /// the whole fix.
    /// </summary>
    [Fact]
    public void TheMarkerIsAButton()
    {
        Assert.Equal("Button", Marker().Name.LocalName);
    }

    /// <summary>
    /// The command is the only way the selection may change, since the bound value is derived.
    /// </summary>
    [Fact]
    public void TheMarkerCarriesItsCommand()
    {
        Assert.NotNull(Marker().Attribute("Command"));
    }

    /// <summary>
    /// Nothing inside the marker may offer the Toggle pattern either, or the defect moves one
    /// level down and reads exactly the same on screen.
    /// </summary>
    [Fact]
    public void NothingInsideTheMarkerCanBeToggled()
    {
        var toggleable = Marker()
            .Descendants()
            .Select(element => element.Name.LocalName)
            .Where(name => name is "CheckBox" or "ToggleButton" or "RadioButton")
            .ToList();

        Assert.Empty(toggleable);
    }

    /// <summary>
    /// A Button announces no checked state of its own, so the marker has to say the state in its
    /// name or a screen reader reads a control that could be either.
    /// </summary>
    [Fact]
    public void TheMarkersNameCarriesItsState()
    {
        string? name = (string?)Marker().Attribute(
            XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"))
            ?? (string?)Marker().Attribute("AutomationProperties.Name");

        Assert.NotNull(name);
        Assert.Contains("BroadcastTargetAccessibleName", name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Positive control: if the element stopped being found, every test above would pass or fail
    /// for the wrong reason, and a rename would read as a repaired marker.
    /// </summary>
    [Fact]
    public void TheMarkerIsFoundExactlyOnce()
    {
        Assert.NotNull(Marker());
    }

    private static XElement Marker()
    {
        string path = Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml");

        Assert.True(File.Exists(path), $"View not found: {path}");

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var matches = XDocument.Load(path)
            .Descendants()
            .Where(element => string.Equals(
                (string?)element.Attribute(x + "Name"), MarkerName, StringComparison.Ordinal))
            .ToList();

        return Assert.Single(matches);
    }
}
