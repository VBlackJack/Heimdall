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

using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Xml.Linq;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that the RDP view's accessibility declarations reach a UI Automation client.
/// </summary>
/// <remarks>
/// <para>Two WPF facts drive everything here, and both are measured below rather than asserted from
/// documentation. A <c>Panel</c> or a <c>Border</c> does not override
/// <c>UIElement.OnCreateAutomationPeer</c>, so it has no peer and every <c>AutomationProperties</c>
/// value set on it is read by nothing. And a <c>TextBlock</c> peer falls back to the element's
/// <c>Text</c> only while its name resolves empty, so a static name masks live text - while an
/// empty string is not a name at all and does not silence anything.</para>
/// <para>The view had both: announcement strings computed onto peerless panels, a constant
/// "Connection status" over the live status line, and <c>Name=""</c> over a Segoe MDL2 private-use
/// glyph.</para>
/// </remarks>
public sealed class RdpViewAutomationSurfaceTests
{
    private const char PrivateUseFirst = (char)0xE000;
    private const char PrivateUseLast = (char)0xF8FF;

    /// <summary>Elements whose declared markup type must produce an automation peer.</summary>
    public static TheoryData<string> PeerBearingElements() =>
    [
        "HealthDot",
        "ConnectionPhaseStepper",
        "RedirectionIndicatorsPanel",
        "ReconnectOverlay",
        "CertificatePromptOverlay",
        "TransientToast",
    ];

    // The mechanism, measured. Without this the assertions below are a style rule.
    [Fact]
    public void APlainPanelOrBorderHasNoAutomationPeerAtAll()
    {
        (bool panelPeerIsNull, bool borderPeerIsNull) = StaRunner.Run(() =>
        {
            var panel = new StackPanel();
            var border = new Border();
            AutomationProperties.SetName(panel, "announced");
            AutomationProperties.SetName(border, "announced");

            return (UIElementAutomationPeer.CreatePeerForElement(panel) is null,
                    UIElementAutomationPeer.CreatePeerForElement(border) is null);
        });

        Assert.True(panelPeerIsNull, "StackPanel unexpectedly has an automation peer.");
        Assert.True(borderPeerIsNull, "Border unexpectedly has an automation peer.");
    }

    [Theory]
    [MemberData(nameof(PeerBearingElements))]
    public void EveryAnnouncingElementIsDeclaredAsATypeThatHasAPeer(string elementName)
    {
        XElement declared = ViewSource.NamedElement(elementName);
        Type type = ResolveMarkupType(ViewSource.TagName(declared));

        bool hasPeer = StaRunner.Run(() =>
        {
            var element = (UIElement)Activator.CreateInstance(type)!;
            return UIElementAutomationPeer.CreatePeerForElement(element) is not null;
        });

        Assert.True(
            hasPeer,
            $"'{elementName}' is declared as {type.Name}, which produces no automation peer, so "
                + "every AutomationProperties value the view sets on it is read by nothing.");
    }

    // The mechanism behind the status-line assertion, measured.
    [Fact]
    public void AStaticNameMasksATextBlocksLiveText()
    {
        (string withName, string withoutName) = StaRunner.Run(() =>
        {
            var named = new TextBlock { Text = "Connection cancelled: certificate refused." };
            AutomationProperties.SetName(named, "Connection status");
            var bare = new TextBlock { Text = "Connection cancelled: certificate refused." };

            return (UIElementAutomationPeer.CreatePeerForElement(named)!.GetName(),
                    UIElementAutomationPeer.CreatePeerForElement(bare)!.GetName());
        });

        Assert.Equal("Connection status", withName);
        Assert.Equal("Connection cancelled: certificate refused.", withoutName);
    }

    [Fact]
    public void TheStatusLineReportsItsOwnTextRatherThanAConstant()
    {
        XElement status = ViewSource.NamedElement("StatusTextBlock");
        string? declaredName = ViewSource.AutomationAttribute(status, "Name");

        const string sentinel = "Connection cancelled: certificate refused.";
        string announced = StaRunner.Run(() =>
        {
            var block = new TextBlock { Text = sentinel };
            if (declaredName is not null)
            {
                AutomationProperties.SetName(block, ResolveDeclaredName(declaredName));
            }

            return UIElementAutomationPeer.CreatePeerForElement(block)!.GetName();
        });

        Assert.True(
            announced == sentinel,
            "StatusTextBlock declares AutomationProperties.Name=\"" + declaredName + "\", so a "
                + "screen reader announces that constant instead of the live status text, whichever "
                + "of the thirty-odd disconnect messages is showing.");
    }

    [Fact]
    public void TheOverlaySeverityGlyphIsNotAnnouncedAsAPrivateUseCodepoint()
    {
        XElement icon = ViewSource.NamedElement("OverlaySeverityIcon");
        string? declaredName = ViewSource.AutomationAttribute(icon, "Name");
        string glyph = (string?)icon.Attribute("Text") ?? string.Empty;

        Assert.False(string.IsNullOrEmpty(glyph), "OverlaySeverityIcon declares no glyph text.");

        string announced = StaRunner.Run(() =>
        {
            var block = new TextBlock { Text = glyph };
            if (declaredName is not null)
            {
                AutomationProperties.SetName(block, declaredName);
            }

            return UIElementAutomationPeer.CreatePeerForElement(block)!.GetName();
        });

        int privateUseIndex = announced.AsSpan().IndexOfAnyInRange(PrivateUseFirst, PrivateUseLast);
        Assert.True(
            privateUseIndex < 0,
            $"The decorative severity glyph is announced as U+{(int)announced[Math.Max(privateUseIndex, 0)]:X4}. "
                + "AutomationProperties.Name=\"\" is not a name, so the peer falls back to Text.");
    }

    [Fact]
    public void TheOverlaySeverityGlyphDoesNotForceItselfOnscreen()
    {
        XElement icon = ViewSource.NamedElement("OverlaySeverityIcon");

        Assert.Null(ViewSource.AutomationAttribute(icon, "IsOffscreenBehavior"));
    }

    private static string ResolveDeclaredName(string declared)
    {
        // "{loc:Translate SomeKey}" stands in for whatever that key resolves to; any non-empty
        // value masks the text identically, which is the whole point.
        return declared.StartsWith('{') ? "Connection status" : declared;
    }

    private static Type ResolveMarkupType(string localName)
    {
        Type[] candidates =
        [
            .. typeof(Border).Assembly.GetTypes(),
            .. typeof(Heimdall.App.Views.EmbeddedRdp.RdpConnectWatchdogPolicy).Assembly.GetTypes(),
        ];

        Type? match = candidates.FirstOrDefault(t =>
            t.Name == localName
            && typeof(UIElement).IsAssignableFrom(t)
            && !t.IsAbstract
            && t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes) is not null);

        Assert.True(match is not null, $"Cannot resolve markup tag '{localName}' to a UIElement type.");
        return match!;
    }
}
