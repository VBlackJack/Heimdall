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
/// Pins how the broadcast panel's toggle is wired in the markup.
/// </summary>
/// <remarks>
/// <para>
/// <b>It shipped wrong.</b> The toggle carried a <c>Command</c> beside a one-way
/// <c>IsChecked</c>. That works for a mouse, and it fails for anything that drives the control
/// through the UIA <c>Toggle</c> pattern: an automation client, or a screen reader. The pattern
/// flips the button's own <c>IsChecked</c> and never raises <c>Click</c>, so the command never
/// runs, the view model never changes, and the button reads "on" over a closed panel with no way
/// back. Measured on the running app on 2026-09-19, where the view model still held <c>false</c>
/// while the button reported its toggle state as On.
/// </para>
/// <para>
/// No view-model test can see this: the defect lives entirely in the binding mode. That is what
/// this file is for.
/// </para>
/// </remarks>
public sealed class CommandLibraryBroadcastToggleMarkupTests
{
    private const string ToggleName = "BtnBroadcastToggle";

    [Fact]
    public void TheToggleBindsIsCheckedTwoWay()
    {
        var toggle = BroadcastToggle();

        string? isChecked = (string?)toggle.Attribute("IsChecked");

        Assert.NotNull(isChecked);
        Assert.Contains("IsBroadcastPanelOpen", isChecked, StringComparison.Ordinal);
        Assert.Contains("Mode=TwoWay", isChecked, StringComparison.Ordinal);
    }

    /// <summary>
    /// A command here is the other half of the defect: with one, the button has two ways to
    /// change and only one of them reaches the view model.
    /// </summary>
    [Fact]
    public void TheToggleCarriesNoCommand()
    {
        var toggle = BroadcastToggle();

        Assert.Null(toggle.Attribute("Command"));
    }

    /// <summary>
    /// Positive control for the two above: if the element itself stopped being found, both would
    /// fail for the wrong reason, and a rename would read as a repaired binding.
    /// </summary>
    [Fact]
    public void TheToggleIsAToggleButtonInTheCommandLibraryView()
    {
        var toggle = BroadcastToggle();

        Assert.Equal("ToggleButton", toggle.Name.LocalName);
    }

    private static XElement BroadcastToggle()
    {
        string path = Path.Combine(
            ViewSource.RepoRoot(),
            "src", "Heimdall.App", "Views", "Tools", "CommandLibraryView.xaml");

        Assert.True(File.Exists(path), $"View not found: {path}");

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var matches = XDocument.Load(path)
            .Descendants()
            .Where(element => string.Equals(
                (string?)element.Attribute(x + "Name"), ToggleName, StringComparison.Ordinal))
            .ToList();

        return Assert.Single(matches);
    }
}
