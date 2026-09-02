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

using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Xml.Linq;
using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that a live region declared in the RDP view actually announces.
/// </summary>
/// <remarks>
/// <c>AutomationProperties.LiveSetting</c> publishes a property and nothing else. A client learns
/// that the region changed from one event, raised on the element's peer. The view declared the
/// setting on ten elements and raised the event on none of them, so a screen reader stayed silent
/// through every status change - including the disconnect message on a tab the user was not
/// looking at, which is the one case the live region exists for.
/// </remarks>
public sealed class RdpLiveRegionTests
{
    [Fact]
    public void AnnouncingAnElementWithAPeerSucceeds()
    {
        bool announced = StaRunner.Run(() => RdpLiveRegion.Announce(new TextBlock { Text = "Reconnecting" }));

        Assert.True(announced);
    }

    // Positive control: the helper reports failure rather than pretending, so a true above means
    // an event really went out and not that the method is unconditional.
    [Fact]
    public void AnnouncingAPeerlessElementReportsFailure()
    {
        bool announced = StaRunner.Run(() => RdpLiveRegion.Announce(new Border()));

        Assert.False(announced);
    }

    [Fact]
    public void AnnouncingNothingIsNotAnAnnouncement()
    {
        Assert.False(RdpLiveRegion.Announce(null));
    }

    /// <summary>
    /// Every element that declares a live setting has a matching announcement in the view.
    /// </summary>
    /// <remarks>
    /// The mapping is by element name because the announcement is imperative: the text is assigned
    /// in code, so there is no binding for the repository's target-updated behavior to hook.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AnnouncingRegions))]
    public void EachAnnouncingRegionIsRaisedSomewhereInTheView(string elementName)
    {
        string source = ViewSource.Code();

        Assert.True(
            Regex.IsMatch(source, @"RdpLiveRegion\.Announce\(" + Regex.Escape(elementName) + @"\)"),
            $"'{elementName}' declares AutomationProperties.LiveSetting but nothing raises a "
                + "live-region event for it, so no screen reader is ever told it changed.");
    }

    // Guarding the guard: the elements named above really do declare the live setting, so the
    // theory is measuring the view's own declarations rather than a list that drifted.
    [Theory]
    [MemberData(nameof(AnnouncingRegions))]
    public void EachNamedRegionStillDeclaresALiveSetting(string elementName)
    {
        Assert.Equal(
            "Polite",
            ViewSource.AutomationAttribute(ViewSource.NamedElement(elementName), "LiveSetting"));
    }

    /// <summary>
    /// And nothing declares a live setting that nothing announces.
    /// </summary>
    /// <remarks>
    /// This keeps the list honest in both directions: a declaration added later either gets an
    /// announcement or fails here. Three per-second timers had their declaration removed rather
    /// than announced - a polite live region firing every second is a flood, not an announcement,
    /// and the status line beside them already carries the state.
    /// </remarks>
    [Fact]
    public void NoElementDeclaresALiveSettingWithoutAnAnnouncement()
    {
        XName xamlName = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");

        string[] declared = ViewSource.Markup()
            .Descendants()
            .Where(e => ViewSource.AutomationAttribute(e, "LiveSetting") is not null)
            .Select(e => (string?)e.Attribute(xamlName) ?? string.Empty)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] expected = AnnouncingRegionNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, declared);
    }

    /// <summary>Every element in the RDP view that is declared as a live region.</summary>
    private static readonly string[] AnnouncingRegionNames =
    [
        "AutofillStatusText",
        "ConnectionPhaseStepper",
        "HealthDot",
        "ReconnectMessageText",
        "RedirectionIndicatorsPanel",
        "StatusTextBlock",
        "TransientToast",
    ];

    public static TheoryData<string> AnnouncingRegions()
    {
        var data = new TheoryData<string>();
        foreach (string name in AnnouncingRegionNames)
        {
            data.Add(name);
        }

        return data;
    }
}
