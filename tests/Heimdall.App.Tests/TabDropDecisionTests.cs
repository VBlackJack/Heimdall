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
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// The strip and the content area are two drop zones with two meanings.
/// </summary>
public sealed class TabDropDecisionTests
{
    private readonly SessionTabViewModel _dragged = new() { Title = "dragged" };
    private readonly SessionTabViewModel _other = new() { Title = "other" };
    private readonly SessionTabViewModel _displayed = new() { Title = "displayed" };

    [Fact]
    public void EmptyStripAfterTheLastTab_MovesToTheEnd_NeverSplits()
    {
        Assert.Equal(
            TabDropAction.MoveToEnd,
            TabDropDecision.Decide(overTabStrip: true, targetTab: null, _dragged, _displayed));
    }

    [Fact]
    public void ReleaseOverHeaderRow_DoesNotDetach()
    {
        // The row of folder headers is inside the window and outside the content area: a tab
        // let go over it is neither dropped nor popped out into a window of its own.
        Assert.False(TabDropDecision.ShouldDetach(
            accepted: false,
            cancelled: false,
            releasedInsideWindow: true,
            releasedOverSessionContent: false));
    }

    [Fact]
    public void ContentArea_SplitsTheDisplayedSession()
    {
        Assert.Equal(
            TabDropAction.Merge,
            TabDropDecision.Decide(overTabStrip: false, targetTab: null, _dragged, _displayed));
    }

    [Fact]
    public void ContentArea_ShowingTheDraggedTabOrNothing_DoesNothing()
    {
        Assert.Equal(
            TabDropAction.None,
            TabDropDecision.Decide(overTabStrip: false, targetTab: null, _dragged, _dragged));
        Assert.Equal(
            TabDropAction.None,
            TabDropDecision.Decide(overTabStrip: false, targetTab: null, _dragged, displayed: null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnotherTab_Reorders_ItselfDoesNothing(bool overTabStrip)
    {
        Assert.Equal(
            TabDropAction.Reorder,
            TabDropDecision.Decide(overTabStrip, _other, _dragged, _displayed));
        Assert.Equal(
            TabDropAction.None,
            TabDropDecision.Decide(overTabStrip, _dragged, _dragged, _displayed));
    }
}
