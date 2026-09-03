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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Which sentence names the tab or window a certificate question belongs to.
/// </summary>
/// <remarks>
/// <para>The line exists because the question no longer arrives at one shared window: a pane the
/// user is not looking at can be holding one, and two panes of two similarly named profiles can
/// be holding one each.</para>
/// <para>It says WHERE the question is. It is not what tells two machines apart - the profile
/// name, the endpoint and the gateway route do that - so nothing here is asserted as if it
/// were.</para>
/// </remarks>
public sealed class RdpTrustPromptOwnerTests
{
    [Fact]
    public void ATabInsideAWindow_NamesBoth()
    {
        // The case the whole change is about: two detached windows, one tab each, both called
        // "Production". The tab alone cannot tell them apart.
        RdpTrustPromptOwnerText owner =
            Assert.NotNull(RdpTrustPromptOwner.Describe("Production", "Paris datacentre"));

        Assert.Equal(RdpTrustPromptOwnerLocaleKeys.TabInWindow, owner.Key);
        Assert.Equal(["Production", "Paris datacentre"], owner.Arguments);
    }

    [Fact]
    public void AWindowThatRepeatsTheTab_IsDropped()
    {
        // "The tab X, in the window X" reads as an error rather than as an identification.
        RdpTrustPromptOwnerText owner =
            Assert.NotNull(RdpTrustPromptOwner.Describe("Production", "Production"));

        Assert.Equal(RdpTrustPromptOwnerLocaleKeys.Tab, owner.Key);
        Assert.Equal(["Production"], owner.Arguments);
    }

    [Fact]
    public void AWindowThatOnlyDecoratesTheTabName_IsDroppedToo()
    {
        // The rule this type documented and did not implement. The justification read "the
        // window is dropped when it repeats the tab, which is what a single-session detached
        // window's title does" - and it does not: FloatingSessionWindow titles itself with the
        // SessionDetachTitle format, so "Production" becomes "Production - Detached", the two
        // strings never matched, and the window clause always stood. Two same-named sessions in
        // two detached windows then read character for character alike, at twice the length.
        RdpTrustPromptOwnerText owner =
            Assert.NotNull(RdpTrustPromptOwner.Describe("Production", "Production - Detached"));

        Assert.Equal(RdpTrustPromptOwnerLocaleKeys.Tab, owner.Key);
        Assert.Equal(["Production"], owner.Arguments);
    }

    [Fact]
    public void AWindowNameThatIsNotBuiltFromTheTab_IsStillNamed()
    {
        // The negative control for the rule above. Containment must not swallow a window whose
        // title is genuinely its own, or the line stops saying where a detached question is.
        RdpTrustPromptOwnerText owner =
            Assert.NotNull(RdpTrustPromptOwner.Describe("Production", "Paris datacentre"));

        Assert.Equal(RdpTrustPromptOwnerLocaleKeys.TabInWindow, owner.Key);
    }

    [Fact]
    public void TheNameShown_IsTheAnnouncedOne_BecauseTheDisplayedOneCannotDiffer()
    {
        // The whole of finding A2 in one assertion. SessionTabViewModel documents DisplayTitle
        // as identical by construction for two sessions of one profile, and the owner line was
        // fed exactly that - so it read the same twice in the case it existed for. AccessibleName
        // is the same string with the ordinal ConnectionViewModel assigns to colliding titles.
        Assert.Equal(
            "Production (2)",
            RdpTrustPromptOwner.AnnouncedName("Production (2)", "Production"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheDisplayedNameIsTheFallback_NotTheReplacement(string? accessibleName)
    {
        // The announced name is blank until ConnectionViewModel has run once, and a session
        // detached into its own window keeps whatever it was last given. Showing nothing there
        // would lose the line entirely on the ordinary single-session case.
        Assert.Equal(
            "Production",
            RdpTrustPromptOwner.AnnouncedName(accessibleName, "Production"));
    }

    [Fact]
    public void TwoSessionsAnnouncedWithAnOrdinal_AreNamedApart()
    {
        // What the line is given now. DisplayTitle is identical by construction for two
        // sessions of one profile, so feeding it that made the line read the same twice in
        // exactly the case two same-named sessions were the problem. AccessibleName is the
        // same string with the ordinal ConnectionViewModel assigns to colliding titles - the
        // only index this application already computes and already shows.
        RdpTrustPromptOwnerText first =
            Assert.NotNull(RdpTrustPromptOwner.Describe("Production (1)", null));
        RdpTrustPromptOwnerText second =
            Assert.NotNull(RdpTrustPromptOwner.Describe("Production (2)", null));

        Assert.NotEqual(first.Arguments, second.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ATabWithNoWindowName_NamesTheTabAlone(string? windowTitle)
    {
        RdpTrustPromptOwnerText owner =
            Assert.NotNull(RdpTrustPromptOwner.Describe("Production", windowTitle));

        Assert.Equal(RdpTrustPromptOwnerLocaleKeys.Tab, owner.Key);
        Assert.Equal(["Production"], owner.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AWindowWithNoTabName_NamesTheWindowAlone(string? tabTitle)
    {
        RdpTrustPromptOwnerText owner =
            Assert.NotNull(RdpTrustPromptOwner.Describe(tabTitle, "Paris datacentre"));

        Assert.Equal(RdpTrustPromptOwnerLocaleKeys.Window, owner.Key);
        Assert.Equal(["Paris datacentre"], owner.Arguments);
    }

    [Fact]
    public void NeitherName_SaysNothingAtAll()
    {
        // Rather than a sentence with an empty clause in it. A question whose own text looks
        // broken is a question the user stops reading.
        Assert.Null(RdpTrustPromptOwner.Describe(null, "  "));
    }

    [Fact]
    public void SurroundingWhitespaceIsNotPartOfAName()
    {
        RdpTrustPromptOwnerText owner =
            Assert.NotNull(RdpTrustPromptOwner.Describe("  Production  ", null));

        Assert.Equal(["Production"], owner.Arguments);
    }

    [Fact]
    public void ThreeSentencesAreThreeDifferentKeys()
    {
        // Asserted as a set, so folding two cases onto one key is a red test rather than a
        // silent loss of the window half.
        Assert.Equal(
            3,
            new[]
            {
                RdpTrustPromptOwnerLocaleKeys.Tab,
                RdpTrustPromptOwnerLocaleKeys.TabInWindow,
                RdpTrustPromptOwnerLocaleKeys.Window,
            }.Distinct(StringComparer.Ordinal).Count());
    }
}
