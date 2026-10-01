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
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.App.Views;

namespace Heimdall.App.Tests.Views;

/// <summary>
/// Pins the fixes of the SSH UX audit that live in the terminal page asset and in the
/// connecting-veil rules of the embedded SSH view.
/// </summary>
public sealed class SshTerminalPageAuditTests
{
    private static string Html => TerminalAssetsLoader.TerminalHtml;

    [Fact]
    public void Page_DoesNotCallSearchAddonMethodsOnTheTerminal()
    {
        // No search addon is loaded: calling clearDecorations on the Terminal threw a TypeError
        // that skipped term.focus() when the search bar closed.
        Html.Should().NotContain("clearDecorations");
        Html.Should().NotContain("searchAddon");
    }

    [Fact]
    public void Page_SearchSteppingUsesAMatchListSoNextAdvances()
    {
        Html.Should().Contain("collectSearchMatches");
        Html.Should().Contain("searchMatchIndex + delta");
        Html.Should().Contain("term.select(");
    }

    [Fact]
    public void Page_SearchControlsCarryLocalizedAccessibleNames()
    {
        foreach (string id in new[] { "search-prev", "search-next", "search-close" })
        {
            Match button = Regex.Match(Html, "<button[^>]*id=\"" + id + "\"[^>]*>");
            button.Success.Should().BeTrue(id);
            button.Value.Should().Contain("aria-label=\"{{TERMINAL_");
            button.Value.Should().Contain("title=\"{{TERMINAL_");
        }

        Html.Should().Contain("id=\"search-status\"");
        Html.Should().Contain("aria-live=\"polite\"");
    }

    [Fact]
    public void Page_FitTerminalDoesNotPostResizeBecauseOnResizeDoes()
    {
        Match fit = Regex.Match(Html, @"function fitTerminal\(\) \{.*?\r?\n            \}", RegexOptions.Singleline);
        fit.Success.Should().BeTrue();
        fit.Value.Should().NotContain("postMessage");
        Html.Should().Contain("term.onResize(");
    }

    [Fact]
    public void Page_RegistersAUrlLinkProviderThatNeedsCtrlClick()
    {
        Html.Should().Contain("registerLinkProvider");
        Html.Should().Contain("event.ctrlKey || event.metaKey");
    }

    [Fact]
    public void Page_DoesNotWriteItsOwnEndMarkerWhenTheHostDid()
    {
        Html.Should().Contain("sessionEndedMarked");
        Html.Should().NotContain("\\x1b[90m");
    }

    [Fact]
    public void Page_ZoomBoundsAndTimingsComeFromNamedValues()
    {
        Html.Should().Contain("/*{{TERMINAL_FONT_MIN}}*/");
        Html.Should().Contain("/*{{TERMINAL_FONT_MAX}}*/");
        Html.Should().NotContain("Math.max(8, Math.min(28");
        Html.Should().Contain("resizeDebounceMilliseconds");
    }

    [Fact]
    public void Localizer_ReplacesEverySearchMarkerOfTheRealPage()
    {
        string result = TerminalHtmlLocalizer.Localize(Html, static key => key + "-localized");

        result.Should().NotContain("{{TERMINAL_SEARCH_");
        result.Should().Contain("TerminalSearchPrevious-localized");
        result.Should().Contain("TerminalSearchNoResults-localized");
    }

    [Fact]
    public void Localizer_FallsBackToEnglishWhenAKeyIsMissing()
    {
        string result = TerminalHtmlLocalizer.Localize(Html, static _ => null);

        result.Should().Contain(TerminalHtmlLocalizer.FallbackSearchPrevious);
        result.Should().Contain("\"" + TerminalHtmlLocalizer.FallbackSearchNoResults + "\"");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void ConnectingVeil_StaysUntilPageReadyAndSessionAttached(
        bool terminalReady,
        bool sessionAttached,
        bool expectedHidden)
    {
        EmbeddedSshView.ShouldHideConnectingOverlay(terminalReady, sessionAttached)
            .Should().Be(expectedHidden);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ConnectionAttempt_IsPendingOnlyWithoutASessionOnALiveView(
        bool hasSession,
        bool disposed,
        bool expectedPending)
    {
        EmbeddedSshView.IsConnectionAttemptPending(hasSession, disposed)
            .Should().Be(expectedPending);
    }
}
