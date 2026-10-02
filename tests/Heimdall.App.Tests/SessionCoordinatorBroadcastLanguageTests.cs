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

using Heimdall.App.ViewModels.Session;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    /// <summary>
    /// The broadcast scope chip and the toggle's tooltip are reworded when the interface
    /// language changes.
    /// </summary>
    /// <remarks>
    /// Both are computed from the localizer. The shell rewords its own lines on a switch, but
    /// nothing told the session coordinator, so both kept the old language until the scope or
    /// the broadcast mode next moved.
    /// </remarks>
    [Fact]
    public async Task BroadcastScopeLabel_FollowsALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        SessionCoordinator session = harness.Main.Session;
        session.IsBroadcastMode = true;
        session.BroadcastScope = BroadcastScope.AllTabs;
        string labelBefore = session.BroadcastScopeLabel;

        var raised = new List<string?>();
        session.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await harness.Main.GetLocalizer().SwitchLocaleAsync("fr");

        Assert.Contains(nameof(SessionCoordinator.BroadcastScopeLabel), raised);
        Assert.Contains(nameof(SessionCoordinator.BroadcastToggleTooltip), raised);
        Assert.NotEqual(labelBefore, session.BroadcastScopeLabel);
    }
}
