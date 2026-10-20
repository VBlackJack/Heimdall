/*
 * Copyright 2026 Nestor Gonzalez
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
using FluentAssertions;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// Where a VNC session's display controls live, and what they send.
/// </summary>
/// <remarks>
/// <para>The controls used to be drawn by the page, pinned to the top right corner of the
/// surface, which is where a remote desktop keeps the minimize, maximize and close buttons of
/// the window in use: the toolbar took the click that belonged to them. They are declared in the
/// session header now, outside the remote screen.</para>
/// <para>The two halves are held together here rather than by eye: the values the header offers
/// are the ones the host sends, the selectors open on the settings a session starts with, and the
/// page is left with nothing to draw over the corner.</para>
/// </remarks>
public sealed class VncHeaderDisplayControlsTests
{
    private static readonly XNamespace s_xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The x:Name of every control the session header carries.</summary>
    private static readonly string[] ControlNames =
        ["ClipboardButton", "ScalingSelector", "QualitySelector"];

    /// <summary>
    /// The page may not carry the toolbar back. Its position was the defect, so a page that kept
    /// the markup would take the corner again the moment the host stopped hiding it.
    /// </summary>
    [Fact]
    public void ThePage_DrawsNothingOverTheRemoteScreen()
    {
        string page = PageSource();

        page.Should().NotContain("vnc-toolbar");
        page.Should().NotContain("select-scaling");
        page.Should().NotContain("select-quality");
    }

    [Fact]
    public void TheHeader_DeclaresEveryDisplayControl()
    {
        XDocument markup = Markup();

        foreach (string name in ControlNames)
        {
            FindByName(markup, name).Should().NotBeNull($"the session header declares {name}");
        }
    }

    /// <summary>
    /// An option the host would not send is a control that does nothing: the page ignores a mode
    /// it does not know and the session keeps its previous setting.
    /// </summary>
    [Fact]
    public void TheScalingOptions_AreTheModesTheHostSends()
    {
        OptionTags("ScalingSelector").Should().Equal(EmbeddedVncView.ScalingModes);
    }

    [Fact]
    public void TheQualityOptions_AreThePresetsTheHostSends()
    {
        OptionTags("QualitySelector").Should().Equal(EmbeddedVncView.QualityPresets.Select(p => p.Name));
    }

    /// <summary>
    /// The presets read best picture first, so each one down the list gives up JPEG quality for
    /// bandwidth. The selector used to move the compression level alone, which is lossless: the
    /// first option looked like every other.
    /// </summary>
    [Fact]
    public void ThePresets_TradePictureForBandwidthDownTheList()
    {
        EmbeddedVncView.QualityPresets.Select(p => p.QualityLevel)
            .Should().BeInDescendingOrder().And.OnlyHaveUniqueItems();
        EmbeddedVncView.QualityPresets.Select(p => p.CompressionLevel)
            .Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    /// <summary>
    /// A session that keeps the header as it opens is unchanged by the presets: compression 6, as
    /// before, and noVNC's own JPEG quality, which the page never set until now. The page holds the
    /// same pair before any message arrives.
    /// </summary>
    [Fact]
    public void TheDefaultPreset_KeepsTheLevelsASessionHadBefore()
    {
        EmbeddedVncView.QualityPreset preset =
            EmbeddedVncView.QualityPresetNamed(EmbeddedVncView.DefaultQualityPresetName);

        preset.Should().Be(new EmbeddedVncView.QualityPreset("performance", 6, 6));

        StatementsOf(PageSource()).Should().Contain([
            "var qualityLevel = 6;",
            "var compressionLevel = 6;",
        ]);
    }

    /// <summary>A Tag the table does not know falls back on the default rather than on nothing.</summary>
    [Fact]
    public void AnUnknownPresetName_FallsBackOnTheDefault()
    {
        EmbeddedVncView.QualityPresetNamed("6").Name.Should().Be(EmbeddedVncView.DefaultQualityPresetName);
        EmbeddedVncView.QualityPresetNamed(null).Name.Should().Be(EmbeddedVncView.DefaultQualityPresetName);
    }

    /// <summary>
    /// What the header shows is what the session opens on, so a selector may not start empty: it
    /// would send nothing, and the session would open on the page's own defaults while the header
    /// showed something else.
    /// </summary>
    [Fact]
    public void TheSelectors_OpenOnTheSettingASessionStartsWith()
    {
        XDocument markup = Markup();

        SelectedTag(markup, "ScalingSelector").Should().Be(EmbeddedVncView.DefaultScalingMode);
        SelectedTag(markup, "QualitySelector").Should().Be(EmbeddedVncView.DefaultQualityPresetName);
    }

    [Fact]
    public void EveryDisplayControl_NamesItselfForAssistiveTechnology()
    {
        XDocument markup = Markup();

        foreach (string name in ControlNames)
        {
            FindByName(markup, name)!
                .Attribute("AutomationProperties.Name")
                .Should()
                .NotBeNull($"a screen reader can name {name}");
        }
    }

    /// <summary>The shape of the two messages the host posts, which is what the page matches on.</summary>
    [Fact]
    public void TheMessages_CarryTheValueThePageReads()
    {
        EmbeddedVncView.ScalingMessage("none").Should().Be("scaling:none");
        EmbeddedVncView.QualityMessage(new EmbeddedVncView.QualityPreset("any", 9, 0))
            .Should().Be("quality:{\"qualityLevel\":9,\"compressionLevel\":0}");
    }

    // The page half of the wiring. The host side is driven on the real view in
    // EmbeddedVncHeaderControlsTests; the page runs in WebView2, so it is read here, one statement
    // per line. A commented-out statement fails; folding one behind an always-false condition would
    // not, which is the limit of reading a script as text.

    /// <summary>A scaling message the page stores but does not apply leaves the session as it was.</summary>
    [Fact]
    public void ThePage_AppliesTheScalingItReceives()
    {
        string page = PageSource();

        StatementsOf(BlockAfter(page, "if (msg.indexOf('scaling:') === 0)"))
            .Should().Contain(["scalingMode = mode;", "applyScaling();"]);
        StatementsOf(BlockAfter(page, "function applyScaling()"))
            .Should().Contain([
                "rfb.scaleViewport = (scalingMode === 'scale');",
                "rfb.resizeSession = (scalingMode === 'remote');",
            ]);
    }

    /// <summary>
    /// The same for the quality message, which carries two levels: a page that applied only the
    /// compression would bring back the defect, a selector that leaves the picture as it was.
    /// </summary>
    [Fact]
    public void ThePage_AppliesTheQualityItReceives()
    {
        string page = PageSource();

        StatementsOf(BlockAfter(page, "if (msg.indexOf('quality:') === 0)"))
            .Should().Contain([
                "qualityLevel = levels.qualityLevel;",
                "compressionLevel = levels.compressionLevel;",
                "applyQuality();",
            ]);
        StatementsOf(BlockAfter(page, "function applyQuality()"))
            .Should().Contain([
                "rfb.qualityLevel = qualityLevel;",
                "rfb.compressionLevel = compressionLevel;",
            ]);
    }

    /// <summary>
    /// The page refuses a message whose two levels it cannot both apply, rather than handing noVNC
    /// a value it would log and drop, and keeps the levels it has.
    /// </summary>
    [Fact]
    public void ThePage_ReadsBothLevelsBeforeItAppliesEither()
    {
        StatementsOf(BlockAfter(PageSource(), "function readQualityLevels(payload)"))
            .Should().Contain(
                "if (!levels || !isLevel(levels.qualityLevel) || !isLevel(levels.compressionLevel)) {");
    }

    // The page's half of the VNC password: it holds the password and sends it only when the server
    // asks, so a security failure can say whether it came after it. Handed to the client at
    // construction, noVNC sends it unasked and the page cannot tell; the host could then drop a
    // remembered password on a refusal that never saw it.

    /// <summary>Kills: the password handed to the client at construction again.</summary>
    [Fact]
    public void ThePage_HoldsThePasswordUntilTheServerAsksForIt()
    {
        string page = PageSource();

        StatementsOf(BlockAfter(page, "rfb = new RFB(container, wsUrl,"))
            .Should().NotContain(statement => statement.Contains("credentials", StringComparison.Ordinal));
        StatementsOf(BlockAfter(page, "rfb.addEventListener('credentialsrequired', function (event)"))
            .Should().Contain([
                "passwordSent = true;",
                "client.sendCredentials({ password: heldPassword });",
            ]);
    }

    /// <summary>
    /// Kills: a page that never reports a security failure, reports it without saying whether the
    /// password was sent, or lets "connection lost" replace its words.
    /// </summary>
    [Fact]
    public void ThePage_ReportsASecurityFailure_WithWhetherThePasswordWasSent()
    {
        string page = PageSource();

        StatementsOf(BlockAfter(page, "rfb.addEventListener('securityfailure', function (event)"))
            .Should().Contain([
                "securityFailed = true;",
                "afterPassword: passwordSent,",
            ]);
        StatementsOf(BlockAfter(page, "rfb.addEventListener('disconnect', function (event)"))
            .Should().Contain("if (!securityFailed) {");
    }

    /// <summary>The text between the brace that follows <paramref name="anchor"/> and its match.</summary>
    private static string BlockAfter(string source, string anchor)
    {
        int at = source.IndexOf(anchor, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the page contains {anchor}");

        int open = source.IndexOf('{', at + anchor.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[(open + 1)..i];
            }
        }

        throw new InvalidOperationException($"The block after {anchor} is not closed.");
    }

    /// <summary>Each line of a block, trimmed, so a statement matches only when it stands alone.</summary>
    private static IEnumerable<string> StatementsOf(string block) =>
        block.Split('\n').Select(line => line.Trim());

    private static IEnumerable<string> OptionTags(string selectorName) =>
        FindByName(Markup(), selectorName)!
            .Elements()
            .Select(item => (string?)item.Attribute("Tag") ?? string.Empty);

    /// <summary>The Tag of the option each selector lands on when the header is first drawn.</summary>
    private static string SelectedTag(XDocument markup, string selectorName)
    {
        XElement selector = FindByName(markup, selectorName)!;
        int index = (int?)selector.Attribute("SelectedIndex") ?? -1;

        index.Should().BeGreaterThanOrEqualTo(0, $"{selectorName} opens on one of its options");
        return (string?)selector.Elements().ElementAt(index).Attribute("Tag") ?? string.Empty;
    }

    private static XElement? FindByName(XDocument markup, string name) =>
        markup.Descendants().FirstOrDefault(e => (string?)e.Attribute(s_xaml + "Name") == name);

    private static XDocument Markup() =>
        XDocument.Load(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedVncView.xaml"));

    private static string PageSource()
    {
        string path = Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Assets", "vnc.html");

        File.Exists(path).Should().BeTrue($"the VNC page is at {path}");
        return File.ReadAllText(path);
    }
}
