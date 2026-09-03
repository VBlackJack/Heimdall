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

using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Xml.Linq;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes what a user who cannot see the pane is told about the certificate question.
/// </summary>
/// <remarks>
/// <para>A prompt is where accessibility matters most: a question the user cannot hear is a
/// question they answer blind, and the three answers here differ by whether durable trust is
/// granted to a machine nobody identified.</para>
/// <para>Three WPF facts drive the assertions, and each is measured in
/// <see cref="RdpViewAutomationSurfaceTests"/> rather than taken from documentation: a plain
/// <c>Border</c> or <c>Panel</c> has no automation peer, so every <c>AutomationProperties</c>
/// value on one is read by nothing; a static <c>Name</c> on a <c>TextBlock</c> masks its live
/// text; and a <c>LiveSetting</c> with nothing raising <c>LiveRegionChanged</c> announces
/// nothing at all.</para>
/// </remarks>
public sealed class RdpCertificatePromptSurfaceTests
{
    private const string Overlay = "CertificatePromptOverlay";

    /// <summary>Every text element of the question, in reading order.</summary>
    /// <remarks>
    /// Enumerated by name rather than discovered, so deleting one from the markup fails here
    /// instead of shrinking the set this file measures to nothing.
    /// </remarks>
    public static TheoryData<string> LiveTextElements() =>
    [
        "CertificatePromptTitleText",
        "CertificatePromptMessageText",
        "CertificatePromptHostText",
        "CertificatePromptRouteText",
        "CertificatePromptSubjectText",
        "CertificatePromptAlreadyTrustedText",
        "CertificatePromptOwnerText",
    ];

    [Fact]
    public void TheQuestionDeclaresItselfADialogWithAName()
    {
        // Without both, a screen reader walking the pane finds a Border with some text in it
        // rather than a question that has taken over the session.
        XElement overlay = ViewSource.NamedElement(Overlay);

        Assert.Equal("True", ViewSource.AutomationAttribute(overlay, "IsDialog"));
        Assert.False(
            string.IsNullOrWhiteSpace(ViewSource.AutomationAttribute(overlay, "Name")),
            "The certificate question declares no automation name, so it is announced as an "
                + "unnamed group.");
    }

    [Theory]
    [MemberData(nameof(LiveTextElements))]
    public void NoTextOfTheQuestionIsMaskedByAStaticName(string elementName)
    {
        // The defect measured in RdpViewAutomationSurfaceTests, applied where it costs most: a
        // name over the message means the reader announces a label while the fingerprint, the
        // machine and the tab that asked go unread.
        XElement element = ViewSource.NamedElement(elementName);

        Assert.Null(ViewSource.AutomationAttribute(element, "Name"));
    }

    [Fact]
    public void TheMessageIsALiveRegion()
    {
        // Paired with the announcement in ShowCertificatePrompt, which RdpTrustPromptWiringTests
        // requires to stand as a step of that method. Either half alone is silent.
        XElement message = ViewSource.NamedElement("CertificatePromptMessageText");

        Assert.False(
            string.IsNullOrWhiteSpace(ViewSource.AutomationAttribute(message, "LiveSetting")),
            "The question's message declares no LiveSetting, so raising LiveRegionChanged on it "
                + "announces nothing.");
    }

    [Fact]
    public void TheMessageAnnouncesItsOwnTextOnceItIsRaised()
    {
        // The mechanism, run: a TextBlock with no name reports its text, which is what makes
        // the live region carry the question rather than a constant.
        XElement message = ViewSource.NamedElement("CertificatePromptMessageText");
        string? declaredName = ViewSource.AutomationAttribute(message, "Name");
        const string sentinel = "dc-pool.example.com presented a certificate this profile has "
            + "never approved.";

        string announced = StaRunner.Run(() =>
        {
            var block = new TextBlock { Text = sentinel };
            if (declaredName is not null)
            {
                AutomationProperties.SetName(block, "Certificate question");
            }

            return UIElementAutomationPeer.CreatePeerForElement(block)!.GetName();
        });

        Assert.Equal(sentinel, announced);
    }

    [Fact]
    public void TheDecorativeGlyphIsNotAnnouncedAsAPrivateUseCodepoint()
    {
        // A Segoe MDL2 glyph read out as U+E783. AutomationProperties.Name="" is not a name and
        // does not silence it; a single space is.
        XElement overlay = ViewSource.NamedElement(Overlay);
        XElement glyph = Assert.Single(
            overlay.Descendants(),
            e => (string?)e.Attribute("FontFamily") == "Segoe MDL2 Assets");

        string name = ViewSource.AutomationAttribute(glyph, "Name") ?? string.Empty;

        Assert.False(
            string.IsNullOrEmpty(name),
            "The question's warning glyph carries no automation name, so its private-use "
                + "codepoint is announced as text.");
        Assert.True(
            string.IsNullOrWhiteSpace(name),
            "The question's warning glyph is decorative and must not announce a word of its "
                + "own beside the message that already says it.");
    }

    [Fact]
    public void RefusalIsTheAnswerTheKeyboardReachesFirst()
    {
        // The property the window used to carry as IsDefault plus IsCancel on one button. There
        // is no window now, so it is carried by tab order plus the focus move in
        // ShowCertificatePrompt: the answer a stray keystroke gives creates no durable trust.
        XElement overlay = ViewSource.NamedElement(Overlay);

        (string Name, int TabIndex)[] answers = overlay
            .Descendants()
            .Where(e => ViewSource.TagName(e) == "Button")
            .Select(e => (
                Name: (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ?? string.Empty,
                TabIndex: int.Parse((string?)e.Attribute("TabIndex") ?? "-1")))
            .OrderBy(answer => answer.TabIndex)
            .ToArray();

        Assert.Equal(3, answers.Length);
        Assert.Equal("CertificatePromptRefuseButton", answers[0].Name);
        Assert.Equal(
            new[] { 0, 1, 2 },
            answers.Select(answer => answer.TabIndex).ToArray());
    }

    [Fact]
    public void TheQuestionTrapsTabWithinItself()
    {
        // A question that lets Tab wander into the toolbar behind it is a question the user can
        // walk away from without answering, while the session stays blocked on it.
        XElement overlay = ViewSource.NamedElement(Overlay);

        Assert.Equal("Cycle", (string?)overlay.Attribute("KeyboardNavigation.TabNavigation"));
    }

    [Fact]
    public void TheFingerprintTheUserIsAskedToCompare_IsReachableByTab()
    {
        // The defect, and it is not polish. The three answer buttons sat in their OWN
        // TabNavigation="Cycle" scope, and ShowCertificatePrompt places focus inside that scope
        // on the refuse button. WPF's Cycle returns focus to the first element of the scope
        // rather than leaving it, so Tab walked Do-not-connect, Just-this-once, Trust,
        // Do-not-connect, forever. The read-only TextBox holding the full SHA-256 fingerprint is
        // a tab stop and was unreachable: never focusable, never selectable, never copyable, and
        // never read out by a screen-reader user navigating the question with Tab. They were
        // asked to compare a fingerprint they could not reach, so they answered without it.
        //
        // The overlay's own Cycle scope, asserted above, is the single trap. Any second scope
        // inside it is a trap within a trap.
        AssertTheFingerprintIsReachable(ViewSource.Markup());
    }

    [Fact]
    public void TheFingerprintIsNotReachableWhenTheAnswersAreTheirOwnCycle()
    {
        // The control for the guard above, which is an assertion of ABSENCE over markup and
        // would otherwise pass on any file at all - including one where the buttons have been
        // moved out of the question entirely. The mutation is the exact shape that shipped.
        // Anchored on the answers panel's own margin, which occurs once: the reconnect overlay
        // above declares the very same scope legitimately, and mutating that one would only
        // prove the guard can see a shape it is not there to forbid.
        const string AnswersPanel = "Margin=\"0,12,0,0\">";
        string markup = ViewSource.MarkupText();
        Assert.Equal(1, markup.Split(AnswersPanel).Length - 1);

        XDocument mutated = XDocument.Parse(markup.Replace(
            AnswersPanel,
            "Margin=\"0,12,0,0\" KeyboardNavigation.TabNavigation=\"Cycle\">",
            StringComparison.Ordinal));

        Assert.Throws<Xunit.Sdk.TrueException>(
            () => AssertTheFingerprintIsReachable(mutated));
    }

    /// <summary>
    /// That nothing inside the question opens a tab-navigation scope of its own, and that the
    /// fingerprint is a real tab stop.
    /// </summary>
    private static void AssertTheFingerprintIsReachable(XDocument markup)
    {
        XElement overlay = ViewSource.NamedElement(markup, Overlay);

        string[] nestedScopes = overlay
            .Descendants()
            .Where(e => e.Attribute("KeyboardNavigation.TabNavigation") is not null)
            .Select(ViewSource.TagName)
            .ToArray();

        Assert.True(
            nestedScopes.Length == 0,
            "The certificate question opens a tab-navigation scope inside its own: "
                + string.Join(", ", nestedScopes)
                + ". Focus is placed inside the answers, so Tab cycles among them and never "
                + "reaches the fingerprint the user is being asked to verify.");

        XElement fingerprint = ViewSource.NamedElement(markup, "CertificatePromptThumbprintBox");

        Assert.True(
            (string?)fingerprint.Attribute("Focusable") != "False"
                && (string?)fingerprint.Attribute("IsTabStop") != "False"
                && (string?)fingerprint.Attribute("KeyboardNavigation.IsTabStop") != "False",
            "The fingerprint box is not a tab stop, so removing the nested cycle above buys "
                + "nothing: Tab still walks past the one thing the question exists to have the "
                + "user read.");
    }

    [Fact]
    public void TheQuestionStartsHidden()
    {
        // It is shown by the session having a question, not by being loaded.
        XElement overlay = ViewSource.NamedElement(Overlay);

        Assert.Equal("Collapsed", (string?)overlay.Attribute("Visibility"));
    }
}
