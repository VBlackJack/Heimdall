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
using Heimdall.App.ViewModels;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// One loading overlay at a time: the tab draws one over an unsplit tab, so its sole pane draws none.
/// </summary>
/// <remarks>
/// A connecting tab, and an external client launched in mstsc, showed two stacked translucent
/// overlays: the tab's (status text, a 200 wide progress bar) and the pane's (its title, a 160 wide
/// progress bar). The pane's title landed on the tab's status text. A pane in a split keeps its own
/// overlay, since the tab's covers the whole split and says nothing about that pane.
/// </remarks>
public sealed class SessionPaneLoadingOverlayTests
{
    [Theory]
    [InlineData(SessionStatusTokens.Connecting)]
    [InlineData(SessionStatusTokens.LaunchedExternalClient)]
    public void ASolePaneOfAnUnsplitTab_DrawsNoOverlayOfItsOwn(string status)
    {
        Assert.False(SessionPaneControl.ShowsOwnLoadingOverlay(
            null, hasFailureDetails: false, status, isSoleVisiblePaneOfTab: true));
    }

    // The control that keeps the test above from passing on an overlay that never shows: outside
    // that case the pane still draws its overlay (a split pane, or an owner not known yet).
    [Theory]
    [InlineData(SessionStatusTokens.Connecting)]
    [InlineData(SessionStatusTokens.LaunchedExternalClient)]
    public void APaneThatIsNotTheSoleOneOfItsTab_KeepsItsOverlay(string status)
    {
        Assert.True(SessionPaneControl.ShowsOwnLoadingOverlay(
            null, hasFailureDetails: false, status, isSoleVisiblePaneOfTab: false));
    }

    // In a split the pane keeps its overlay only while it is loading: a host, a failure or an ended
    // external client never bring it back.
    [Theory]
    [InlineData(true, false, SessionStatusTokens.Connecting)]
    [InlineData(false, true, SessionStatusTokens.Error)]
    [InlineData(false, false, SessionStatusTokens.Disconnected)]
    public void APaneInASplit_ThatIsNotLoading_DrawsNoLoadingOverlay(bool hasHost, bool hasFailure, string status)
    {
        object? host = hasHost ? new object() : null;

        Assert.False(SessionPaneControl.ShowsOwnLoadingOverlay(host, hasFailure, status, isSoleVisiblePaneOfTab: false));
    }

    private const string TabTemplateKey = "SessionTabContentTemplate";
    private const string TabOverlayName = "SessionLoadingOverlay";

    // The pane stands aside only because the tab's overlay shows under exactly the conditions of
    // ShowsLoadingOverlay. A third condition on the tab's trigger would leave an unsplit tab blank.
    [Fact]
    public void TheTabLoadingOverlay_ShowsUnderExactlyTheConditionsOfThePaneDecision()
    {
        const string keep = " Keep ShowsLoadingOverlay and ShowsOwnLoadingOverlay in SessionPaneControl.xaml.cs in step with the tab overlay triggers, or an unsplit tab goes blank.";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement template = Assert.Single(
            XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml")).Descendants(),
            e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(x + "Key") == TabTemplateKey);

        List<XElement> setters = template.Descendants()
            .Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("TargetName") == TabOverlayName)
            .ToList();
        List<XElement> triggers = setters.Select(s => s.Parent!).Distinct().ToList();

        XElement show = Assert.Single(
            triggers, t => t.Name.LocalName == "MultiDataTrigger" && setters.Any(s => s.Parent == t && (string?)s.Attribute("Value") == "Visible"));
        List<(string? Binding, string? Value)> conditions = show.Descendants()
            .Where(e => e.Name.LocalName == "Condition")
            .Select(c => ((string?)c.Attribute("Binding"), (string?)c.Attribute("Value")))
            .ToList();
        Assert.True(
            conditions.Count == 2
                && conditions.Contains(("{Binding HostControl}", "{x:Null}"))
                && conditions.Contains(("{Binding HasFailureDetails}", "False")),
            "The tab loading overlay must show on a null HostControl and no failure details, and on nothing else." + keep);

        List<XElement> collapsing = triggers
            .Where(t => setters.Any(s => s.Parent == t && (string?)s.Attribute("Value") == "Collapsed"))
            .ToList();
        XElement hide = Assert.Single(collapsing);
        Assert.True(
            hide.Name.LocalName == "DataTrigger"
                && (string?)hide.Attribute("Binding") == "{Binding Status}"
                && (string?)hide.Attribute("Value") == $"{{x:Static vm:{nameof(SessionStatusTokens)}.{nameof(SessionStatusTokens.Disconnected)}}}",
            "The only trigger that collapses the tab loading overlay must be a Status of Disconnected." + keep);

        Assert.True(
            triggers.Count == 2 && setters.Count == 2,
            "No other trigger or setter may target the tab loading overlay." + keep);
    }
}
